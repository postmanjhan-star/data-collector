using System.Text;
using Microsoft.Data.Sqlite;

namespace HF811Collector;

/// <summary>Regression checks for non-destructive schema migration and ACK semantics.</summary>
public static class SyncSelfTest
{
    public static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hf811-sync-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "legacy.sqlite3");
        try
        {
            // Legacy v1.4 database has no event_id or synchronization columns.
            using (var db = new SqliteConnection("Data Source=" + path))
            {
                db.Open();
                using var sql = db.CreateCommand();
                sql.CommandText = """
                    CREATE TABLE scans (
                      id INTEGER PRIMARY KEY AUTOINCREMENT,
                      received_at_utc TEXT NOT NULL,device_id TEXT NOT NULL,
                      barcode TEXT NOT NULL,raw_hex TEXT NOT NULL,source_address TEXT NOT NULL,
                      is_duplicate INTEGER NOT NULL,is_no_read INTEGER NOT NULL
                    );
                    INSERT INTO scans(received_at_utc,device_id,barcode,raw_hex,source_address,is_duplicate,is_no_read)
                    VALUES ('2026-11-06T01:00:00.000+00:00','LEGACY','OLDPCB','4f4c44504342','127.0.0.1',0,0);
                    """;
                sql.ExecuteNonQuery();
            }
            string oldId;
            string unresolvedId;
            using (var store = new ScanStore(path, 2000, "NOREAD"))
            {
                var initial = store.PendingBatch(20);
                if (initial.Count != 1 || !Guid.TryParse(initial[0].EventId, out _))
                    throw new InvalidOperationException("Legacy row was not backfilled with event_id");
                oldId = initial[0].EventId;
                store.Save(Encoding.ASCII.GetBytes("PCB-NEW"), "SIM", "127.0.0.1");
                store.Save(Encoding.ASCII.GetBytes("NOREAD"), "SIM", "127.0.0.1");
                var batch = store.PendingBatch(20);
                if (batch.Count != 3 || batch.Select(x => x.EventId).Distinct().Count() != 3)
                    throw new InvalidOperationException("Pending rows must have unique IDs");

                // Reject an invalid receipt BEFORE making any state changes.
                try
                {
                    store.AcceptReceipt(batch, new UploadReceipt(new() { Guid.NewGuid().ToString("D") }, new()));
                    throw new InvalidOperationException("Unknown ACK was accepted");
                }
                catch (InvalidDataException) { }
                store.AcceptReceipt(batch, new UploadReceipt(
                    new() { batch[0].EventId }, new() { new UploadRejected(batch[1].EventId, "invalid test") }));
                unresolvedId = batch[2].EventId;
                var state = store.GetSyncState();
                if (state.Pending != 1 || state.Synced != 1 || state.Rejected != 1)
                    throw new InvalidOperationException("ACK / rejection state is wrong");
                store.RecordUploadFailure(new[] { unresolvedId }, "simulated outage");
            }
            using (var reopened = new ScanStore(path, 2000, "NOREAD"))
            {
                var pending = reopened.PendingBatch(100);
                if (pending.Count != 1 || pending[0].EventId != unresolvedId)
                    throw new InvalidOperationException("Pending retry ID changed across restart");
                var stats = reopened.GetSyncState();
                if (stats.Total != 3 || stats.Synced != 1 || stats.Rejected != 1)
                    throw new InvalidOperationException("Schema migration damaged history");
                // Re-opening does not regenerate historical IDs.
                using var db = new SqliteConnection("Data Source=" + path);
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "SELECT event_id FROM scans WHERE barcode='OLDPCB'";
                if ((string?)cmd.ExecuteScalar() != oldId)
                    throw new InvalidOperationException("Legacy event ID changed after restart");
            }
            ServerSyncSettings.Validate(false, "LINE01", "", 5, 50);
            try
            {
                ServerSyncSettings.Validate(true, "LINE01", "http://192.168.1.2/api", 5, 50);
                throw new InvalidOperationException("Insecure remote HTTP endpoint was accepted");
            }
            catch (ArgumentException) { }
            Console.WriteLine("SYNC SELFTEST PASS: historical migration, stable IDs, ACK/reject/retry, HTTPS guard.");
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { /* Windows AV can delay SQLite file release. */ }
        }
    }
}
