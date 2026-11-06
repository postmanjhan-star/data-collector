using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace HF811Collector;

public sealed record UploadEvent(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("barcode")] string Barcode,
    [property: JsonPropertyName("received_at_utc")] string ReceivedAtUtc,
    [property: JsonPropertyName("raw_hex")] string RawHex,
    [property: JsonPropertyName("source_address")] string SourceAddress,
    [property: JsonPropertyName("is_duplicate")] bool IsDuplicate,
    [property: JsonPropertyName("is_no_read")] bool IsNoRead);

public sealed record UploadRejected(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record UploadReceipt(
    [property: JsonPropertyName("acknowledged_event_ids")] List<string> AcknowledgedEventIds,
    [property: JsonPropertyName("rejected")] List<UploadRejected> Rejected);

public sealed record SyncState(long Pending, long Synced, long Rejected, long Total,
    string? LastSyncedAtUtc);

public sealed partial class ScanStore
{
    private void UpgradeSyncSchema()
    {
        // Migration is additive. Never drop existing scan records.
        using var tx = _db.BeginTransaction();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "PRAGMA table_info(scans)";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        foreach (var (name, sql) in new (string, string)[]
        {
            ("event_id", "ALTER TABLE scans ADD COLUMN event_id TEXT"),
            ("sync_status", "ALTER TABLE scans ADD COLUMN sync_status TEXT NOT NULL DEFAULT 'pending'"),
            ("uploaded_at_utc", "ALTER TABLE scans ADD COLUMN uploaded_at_utc TEXT"),
            ("retry_count", "ALTER TABLE scans ADD COLUMN retry_count INTEGER NOT NULL DEFAULT 0"),
            ("last_sync_error", "ALTER TABLE scans ADD COLUMN last_sync_error TEXT")
        })
        {
            if (columns.Contains(name)) continue;
            using var alter = _db.CreateCommand();
            alter.Transaction = tx;
            alter.CommandText = sql;
            alter.ExecuteNonQuery();
        }

        // Give all historical rows durable event identities, once, in one transaction.
        var missing = new List<long>();
        using (var select = _db.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = "SELECT id FROM scans WHERE event_id IS NULL OR event_id = ''";
            using var reader = select.ExecuteReader();
            while (reader.Read()) missing.Add(reader.GetInt64(0));
        }
        using (var update = _db.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = "UPDATE scans SET event_id = $event WHERE id = $id";
            var id = update.Parameters.Add("$id", SqliteType.Integer);
            var ev = update.Parameters.Add("$event", SqliteType.Text);
            foreach (long row in missing)
            {
                id.Value = row;
                ev.Value = Guid.NewGuid().ToString("D");
                update.ExecuteNonQuery();
            }
        }
        using (var index = _db.CreateCommand())
        {
            index.Transaction = tx;
            index.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS ux_scans_event_id ON scans(event_id);
                CREATE INDEX IF NOT EXISTS ix_scans_sync_id ON scans(sync_status, id);
                """;
            index.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public IReadOnlyList<UploadEvent> PendingBatch(int limit)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT event_id, device_id, barcode, received_at_utc, raw_hex, source_address,
                       is_duplicate, is_no_read FROM scans
                WHERE sync_status='pending' ORDER BY id LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
            using var reader = cmd.ExecuteReader();
            var events = new List<UploadEvent>();
            while (reader.Read())
                events.Add(new UploadEvent(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.GetInt64(6) != 0, reader.GetInt64(7) != 0));
            return events;
        }
    }

    public SyncState GetSyncState()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN sync_status='pending' THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN sync_status='synced' THEN 1 ELSE 0 END),0),
                       COALESCE(SUM(CASE WHEN sync_status='rejected' THEN 1 ELSE 0 END),0),
                       MAX(uploaded_at_utc)
                FROM scans
                """;
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return new SyncState(reader.GetInt64(1), reader.GetInt64(2),
                reader.GetInt64(3), reader.GetInt64(0),
                reader.IsDBNull(4) ? null : reader.GetString(4));
        }
    }

    public void RecordUploadFailure(IEnumerable<string> eventIds, string message)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE scans SET retry_count=retry_count+1, last_sync_error=$error
                WHERE event_id=$id AND sync_status='pending'
                """;
            var id = cmd.Parameters.Add("$id", SqliteType.Text);
            var error = cmd.Parameters.Add("$error", SqliteType.Text);
            error.Value = message.Length > 300 ? message[..300] : message;
            foreach (string eventId in eventIds)
            {
                id.Value = eventId;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void AcceptReceipt(IReadOnlyList<UploadEvent> submitted, UploadReceipt receipt)
    {
        // A server must never be allowed to acknowledge unrelated local events.
        var valid = submitted.Select(x => x.EventId).ToHashSet(StringComparer.Ordinal);
        var acknowledged = (receipt.AcknowledgedEventIds ?? new()).ToHashSet(StringComparer.Ordinal);
        var rejected = receipt.Rejected ?? new();
        if (acknowledged.Any(x => !valid.Contains(x)) ||
            rejected.Any(x => !valid.Contains(x.EventId)) ||
            rejected.Any(x => acknowledged.Contains(x.EventId)) ||
            rejected.GroupBy(x => x.EventId).Any(g => g.Count() != 1))
            throw new InvalidDataException("Unexpected, overlapping, or duplicated event IDs in server receipt");

        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE scans
                SET sync_status=$status, uploaded_at_utc=$time, last_sync_error=$error
                WHERE event_id=$event AND sync_status='pending'
                """;
            var status = cmd.Parameters.Add("$status", SqliteType.Text);
            var time = cmd.Parameters.Add("$time", SqliteType.Text);
            var error = cmd.Parameters.Add("$error", SqliteType.Text);
            var ev = cmd.Parameters.Add("$event", SqliteType.Text);
            time.Value = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            foreach (var eventId in acknowledged)
            {
                status.Value = "synced";
                error.Value = DBNull.Value;
                ev.Value = eventId;
                cmd.ExecuteNonQuery();
            }
            foreach (var item in rejected)
            {
                status.Value = "rejected";
                error.Value = item.Reason.Length > 300 ? item.Reason[..300] : item.Reason;
                ev.Value = item.EventId;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        // Unacknowledged items stay pending and can be safely retried with the SAME event_id.
        var unresolved = submitted.Where(x => !acknowledged.Contains(x.EventId)
            && !rejected.Any(y => y.EventId == x.EventId)).Select(x => x.EventId);
        RecordUploadFailure(unresolved, "No acknowledgment in server receipt");
    }
}
