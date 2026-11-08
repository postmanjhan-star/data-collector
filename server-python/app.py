"""HF811 Server Sync receiver — FastAPI + SQLAlchemy (SQLite/MySQL/PostgreSQL).

Public API is wire-compatible with the C# v1.5 SyncAgent. These are scan events,
not approved MES pass transactions. Use HTTPS at the reverse proxy in production.
"""
from __future__ import annotations

import hashlib
import hmac
import os
import re
from contextlib import asynccontextmanager
from datetime import datetime
from pathlib import Path
from typing import Any
from uuid import UUID

from fastapi import FastAPI, Header, HTTPException, status
from pydantic import BaseModel, ConfigDict
from sqlalchemy import Boolean, Engine, String, Text, create_engine, func, select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.orm import DeclarativeBase, Mapped, Session, mapped_column

COLLECTOR_ID_PATTERN = re.compile(r"^[A-Za-z0-9_.-]{1,96}$")
HEX_PATTERN = re.compile(r"^(?:[0-9a-fA-F]{2})*$")
MAX_BATCH = 100


class Base(DeclarativeBase):
    pass


class ScanEvent(Base):
    __tablename__ = "scan_events"

    event_id: Mapped[str] = mapped_column(String(36), primary_key=True)
    collector_id: Mapped[str] = mapped_column(String(96), nullable=False, index=True)
    device_id: Mapped[str] = mapped_column(String(128), nullable=False)
    barcode: Mapped[str] = mapped_column(Text, nullable=False)
    received_at_utc: Mapped[str] = mapped_column(String(64), nullable=False)
    raw_hex: Mapped[str] = mapped_column(Text, nullable=False)
    source_address: Mapped[str] = mapped_column(String(256), nullable=False)
    is_duplicate: Mapped[bool] = mapped_column(Boolean, nullable=False)
    is_no_read: Mapped[bool] = mapped_column(Boolean, nullable=False)
    ingested_at_utc: Mapped[str] = mapped_column(String(40), nullable=False)


class IncomingScan(BaseModel):
    model_config = ConfigDict(extra="forbid")

    event_id: str
    device_id: str
    barcode: str
    received_at_utc: str
    raw_hex: str
    source_address: str
    is_duplicate: bool
    is_no_read: bool


class BatchIn(BaseModel):
    model_config = ConfigDict(extra="forbid")

    collector_id: str
    events: list[IncomingScan]


def _validate_event(event: IncomingScan) -> str | None:
    if not event.device_id.strip() or len(event.device_id) > 128:
        return "Invalid device_id"
    if len(event.barcode) > 4096:
        return "Invalid barcode length"
    if len(event.received_at_utc) > 64:
        return "Invalid received_at_utc"
    try:
        datetime.fromisoformat(event.received_at_utc.replace("Z", "+00:00"))
    except ValueError:
        return "Invalid received_at_utc"
    if len(event.raw_hex) > 8192 or not HEX_PATTERN.fullmatch(event.raw_hex):
        return "Invalid raw_hex"
    if len(event.source_address) > 256:
        return "Invalid source_address"
    return None


def _equivalent(stored: ScanEvent, collector_id: str, e: IncomingScan) -> bool:
    return (
        stored.collector_id == collector_id
        and stored.device_id == e.device_id
        and stored.barcode == e.barcode
        and stored.received_at_utc == e.received_at_utc
        and stored.raw_hex == e.raw_hex
        and stored.source_address == e.source_address
        and stored.is_duplicate == e.is_duplicate
        and stored.is_no_read == e.is_no_read
    )


def _engine(database_url: str) -> Engine:
    # Relative SQLite database paths are created for the local demo.
    if database_url.startswith("sqlite:///") and not database_url.startswith("sqlite:///:memory:"):
        sqlite_path = database_url.removeprefix("sqlite:///")
        Path(sqlite_path).parent.mkdir(parents=True, exist_ok=True)
    return create_engine(database_url, pool_pre_ping=True)


def create_app(*, database_url: str | None = None, api_key: str | None = None) -> FastAPI:
    database_url = database_url or os.environ.get(
        "HF811_DATABASE_URL", "sqlite:///./data/receiver.sqlite3"
    )
    api_key = api_key if api_key is not None else os.environ.get("HF811_SERVER_API_KEY", "")
    if len(api_key) < 16 or api_key.strip() != api_key:
        raise RuntimeError("HF811_SERVER_API_KEY must be a nonempty, 16+ character secret")
    # Compare fixed-length digests so even malformed header lengths take the same path.
    known_digest = hashlib.sha256(api_key.encode("utf-8")).digest()

    engine = _engine(database_url)
    Base.metadata.create_all(engine)

    @asynccontextmanager
    async def lifespan(_app: FastAPI):
        try:
            yield
        finally:
            engine.dispose()

    app = FastAPI(title="HF811 Receiver (FastAPI)", version="1.5.0", lifespan=lifespan)
    app.state.engine = engine

    def authorize(presented: str | None) -> None:
        candidate = hashlib.sha256((presented or "").encode("utf-8")).digest()
        if presented is None or not hmac.compare_digest(known_digest, candidate):
            raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Unauthorized")

    @app.get("/health")
    def health() -> dict[str, str]:
        return {"status": "ok", "role": "HF811Receiver"}

    @app.get("/api/v1/collector/stats")
    def stats(x_collector_key: str | None = Header(default=None)) -> dict[str, int]:
        authorize(x_collector_key)
        with Session(engine) as session:
            return {
                "total": session.scalar(select(func.count()).select_from(ScanEvent)) or 0,
                "collectors": session.scalar(select(func.count(func.distinct(ScanEvent.collector_id)))) or 0,
            }

    @app.post("/api/v1/collector/scans/batch")
    def batch(payload: BatchIn, x_collector_key: str | None = Header(default=None)) -> dict[str, Any]:
        authorize(x_collector_key)
        if not COLLECTOR_ID_PATTERN.fullmatch(payload.collector_id) or not 1 <= len(payload.events) <= MAX_BATCH:
            raise HTTPException(400, "Invalid collector_id or event list (limit 100)")

        # Prevent ambiguous replies: the C# client verifies each ID appears in just one state.
        ids = [e.event_id for e in payload.events]
        if any(not _is_guid(value) for value in ids) or len(set(ids)) != len(ids):
            raise HTTPException(400, "Invalid or repeated event_id in batch")

        acknowledged: list[str] = []
        rejected: list[dict[str, str]] = []
        # Each accepted item is committed before it can be ACKed. If a later
        # insert fails, previous rows remain durable and are safe to replay.
        with Session(engine) as session:
            for e in payload.events:
                reason = _validate_event(e)
                if reason is not None:
                    rejected.append({"event_id": e.event_id, "reason": reason})
                    continue

                existing = session.get(ScanEvent, e.event_id)
                if existing is not None:
                    if _equivalent(existing, payload.collector_id, e):
                        acknowledged.append(e.event_id)
                    else:
                        rejected.append({
                            "event_id": e.event_id,
                            "reason": "event_id already exists with different content",
                        })
                    continue
                session.add(
                    ScanEvent(
                        event_id=e.event_id, collector_id=payload.collector_id,
                        device_id=e.device_id, barcode=e.barcode,
                        received_at_utc=e.received_at_utc, raw_hex=e.raw_hex,
                        source_address=e.source_address, is_duplicate=e.is_duplicate,
                        is_no_read=e.is_no_read,
                        ingested_at_utc=datetime.now().astimezone().isoformat(),
                    )
                )
                try:
                    session.commit()
                    acknowledged.append(e.event_id)
                except IntegrityError:
                    # Concurrent retry racing with the same UUID: query the winner.
                    session.rollback()
                    winner = session.get(ScanEvent, e.event_id)
                    if winner is not None and _equivalent(winner, payload.collector_id, e):
                        acknowledged.append(e.event_id)
                    elif winner is not None:
                        rejected.append({
                            "event_id": e.event_id,
                            "reason": "event_id already exists with different content",
                        })
                    else:
                        raise
        return {"acknowledged_event_ids": acknowledged, "rejected": rejected}

    return app


def _is_guid(value: str) -> bool:
    try:
        UUID(value)
        return True
    except (ValueError, AttributeError):
        return False


# Uvicorn import target: uvicorn app:app --app-dir server-python ...
# Initialize only when launched; pytest imports create_app with an isolated DB.
app = create_app()
