"""FastAPI receiver contract tests — used by the unchanged C# SyncAgent."""
import os
import sys
from pathlib import Path
from uuid import uuid4

os.environ.setdefault("HF811_SERVER_API_KEY", "test-only-strong-collector-key")
os.environ.setdefault("HF811_DATABASE_URL", "sqlite:///:memory:")
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from fastapi.testclient import TestClient

from app import create_app

KEY = "test-only-strong-collector-key"
AUTH = {"X-Collector-Key": KEY}
BATCH_URL = "/api/v1/collector/scans/batch"
STATS_URL = "/api/v1/collector/stats"


def event(*, barcode: str = "PCB000001", event_id: str | None = None) -> dict:
    return {
        "event_id": event_id or str(uuid4()),
        "device_id": "SMT-LINE-01-HF811",
        "barcode": barcode,
        "received_at_utc": "2026-11-08T02:00:00.000+00:00",
        "raw_hex": barcode.encode().hex(),
        "source_address": "192.168.16.50:55256",
        "is_duplicate": False,
        "is_no_read": False,
    }


def client(tmp_path) -> TestClient:
    return TestClient(create_app(
        api_key=KEY,
        database_url=f"sqlite:///{tmp_path / 'receiver.sqlite3'}",
    ))


def test_health_and_auth(tmp_path):
    with client(tmp_path) as api:
        assert api.get("/health").json()["status"] == "ok"
        assert api.get(STATS_URL).status_code == 401
        assert api.get(STATS_URL, headers={"X-Collector-Key": "wrong"}).status_code == 401
        assert api.get(STATS_URL, headers=AUTH).json() == {"total": 0, "collectors": 0}
        assert api.post(BATCH_URL, json={"collector_id": "LINE01", "events": [event()]}).status_code == 401


def test_ack_replay_conflict_and_restart(tmp_path):
    first = event()
    payload = {"collector_id": "LINE01", "events": [first]}
    with client(tmp_path) as api:
        first_receipt = api.post(BATCH_URL, headers=AUTH, json=payload)
        assert first_receipt.status_code == 200, first_receipt.text
        assert first_receipt.json() == {"acknowledged_event_ids": [first["event_id"]], "rejected": []}
        replay = api.post(BATCH_URL, headers=AUTH, json=payload)
        assert replay.json() == first_receipt.json()
        edited = {"collector_id": "LINE01", "events": [{**first, "barcode": "CONFLICT"}]}
        conflict = api.post(BATCH_URL, headers=AUTH, json=edited).json()
        assert conflict["acknowledged_event_ids"] == []
        assert len(conflict["rejected"]) == 1
        assert conflict["rejected"][0]["event_id"] == first["event_id"]
        assert api.get(STATS_URL, headers=AUTH).json() == {"total": 1, "collectors": 1}
    with client(tmp_path) as restarted:
        assert restarted.post(BATCH_URL, headers=AUTH, json=payload).json()["acknowledged_event_ids"] == [first["event_id"]]
        assert restarted.get(STATS_URL, headers=AUTH).json()["total"] == 1


def test_partial_reject_and_no_read(tmp_path):
    good = event(barcode="PCB000002")
    invalid = event(barcode="BAD")
    invalid["raw_hex"] = "this-is-not-hex"
    no_read = event(barcode="NOREAD")
    no_read["is_no_read"] = True
    reply = client(tmp_path).post(BATCH_URL, headers=AUTH, json={
        "collector_id": "LINE02", "events": [good, invalid, no_read]
    })
    assert reply.status_code == 200
    assert reply.json()["acknowledged_event_ids"] == [good["event_id"], no_read["event_id"]]
    assert reply.json()["rejected"][0]["event_id"] == invalid["event_id"]


def test_invalid_ids_batches_and_collector(tmp_path):
    with client(tmp_path) as api:
        same = event()
        for payload in [
            {"collector_id": "LINE1", "events": []},
            {"collector_id": "LINE1", "events": [same, same]},
            {"collector_id": "LINE 1", "events": [same]},
            {"collector_id": "LINE1", "events": [{**same, "event_id": "abc"}]},
            {"collector_id": "LINE1", "events": [event() for _ in range(101)]},
        ]:
            assert api.post(BATCH_URL, headers=AUTH, json=payload).status_code == 400
        assert api.get(STATS_URL, headers=AUTH).json()["total"] == 0
