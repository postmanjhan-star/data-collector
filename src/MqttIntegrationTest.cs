using System.Text;
using Microsoft.Data.Sqlite;

namespace HF811Collector;

public static class MqttIntegrationTest
{
    public static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        string url = SimulatorRunner.Arg(args, "--url", "mqtt://127.0.0.1:1883");
        string sodaDatabase = SimulatorRunner.Arg(args, "--soda-db", "");
        if (string.IsNullOrWhiteSpace(sodaDatabase))
            throw new ArgumentException("mqtt-e2e requires --soda-db pointing to the Soda test SQLite database");
        if (!new Uri(url).IsLoopback)
            throw new ArgumentException("CI loopback only");
        string key = SyncSecrets.Read();
        string runId = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(Path.GetTempPath(), "mqtt-e2e-" + runId);
        using var soda = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(sodaDatabase),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        soda.Open();
        Directory.CreateDirectory(folder);
        try
        {
            var cfg = new Settings
            {
                CollectorId = "CI-MQTT-" + runId,
                SyncEnabled = true,
                ServerUrl = url,
                DatabasePath = Path.Combine(folder, "scans.sqlite3"),
                SyncBatchSize = 10
            };
            IReadOnlyList<UploadEvent> original;
            using (var store = new ScanStore(cfg.DatabasePath, 2000, "NOREAD"))
            {
                store.Save(Encoding.ASCII.GetBytes("PCB001"), "SC2-UPPER", "loopback");
                store.Save(Encoding.ASCII.GetBytes("PCB001"), "SC2-UPPER", "loopback");
                store.Save(Encoding.ASCII.GetBytes("PCB001"), "SC2-LOWER", "loopback");
                store.Save(Encoding.ASCII.GetBytes("NOREAD"), "SC2-LOWER", "loopback");
                original = store.PendingBatch(10);
                var offline = new Settings
                {
                    CollectorId = cfg.CollectorId,
                    SyncEnabled = true,
                    ServerUrl = "mqtt://127.0.0.1:1",
                    SyncBatchSize = 10
                };
                using var agent = new SyncAgent(offline, store);
                if (await agent.SyncOnceAsync(token) != 0 || store.GetSyncState().Pending != 4)
                    throw new InvalidOperationException("Offline records lost");
            }
            using (var store = new ScanStore(cfg.DatabasePath, 2000, "NOREAD"))
            {
                if (!store.PendingBatch(10).SequenceEqual(original))
                    throw new InvalidOperationException("Pending events changed after restart");
                using var agent = new SyncAgent(cfg, store);
                if (await agent.SyncOnceAsync(token) != 4 || store.GetSyncState().Synced != 4)
                    throw new InvalidOperationException("Soda commit/ACK failed");

                if (original.Count(e => e.DeviceId == "SC2-UPPER") != 2 || original.Count(e => e.DeviceId == "SC2-LOWER") != 2 || original.Count(e => e.IsDuplicate) != 1 || original.Count(e => e.IsNoRead) != 1)
                    throw new InvalidOperationException("Upper/lower raw scan identity or classification lost");
                var committed = ReadEvents(soda, cfg.CollectorId);
                if (!committed.Select(row => row.Event).SequenceEqual(original.OrderBy(e => e.EventId, StringComparer.Ordinal)))
                    throw new InvalidOperationException("Soda did not commit the submitted events");

                // The same SyncAgent must upload a second non-empty batch without reconnecting.
                store.Save(Encoding.ASCII.GetBytes("PCB002"), "SC2-UPPER", "loopback");
                if (await agent.SyncOnceAsync(token) != 1 || agent.MqttConnectionCount != 1)
                    throw new InvalidOperationException("MQTT session not reused");
                var allCommitted = ReadEvents(soda, cfg.CollectorId);
                if (allCommitted.Count != 5) throw new InvalidOperationException("Second batch not committed");
                // Do not connect a second client with the same ID while the agent is alive.
                using var replaySession = new MqttBatchTransport.Session(url, cfg.CollectorId);
                var replay = await replaySession.SendAsync(key, new UploadEnvelope(cfg.CollectorId, original), token);
                if (!replay.AcknowledgedEventIds.SequenceEqual(original.Select(e => e.EventId)) ||
                    replay.Rejected.Count != 0 || !ReadEvents(soda, cfg.CollectorId).SequenceEqual(allCommitted))
                    throw new InvalidOperationException("Replay not idempotent");

                var changed = original[0] with { Barcode = "CHANGED" };
                var conflict = await replaySession.SendAsync(key, new UploadEnvelope(cfg.CollectorId, new[] { changed }), token);
                if (conflict.Rejected.Count != 1 || conflict.Rejected[0].EventId != changed.EventId ||
                    conflict.Rejected[0].Reason != "event_id already exists with different content" ||
                    conflict.AcknowledgedEventIds.Count != 0 || !ReadEvents(soda, cfg.CollectorId).SequenceEqual(allCommitted))
                    throw new InvalidOperationException("Soda failed UUID conflict check");
                if (replaySession.ConnectionCount != 1) throw new InvalidOperationException("Replay session reconnected unexpectedly");
            }
            Console.WriteLine("MQTT SODA E2E PASS: offline SQLite, restart, commit ACK, replay, conflict");
            return 0;
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    private static List<(UploadEvent Event, string IngestedAtUtc)> ReadEvents(SqliteConnection soda, string collectorId)
    {
        using var cmd = soda.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, device_id, barcode, received_at_utc, raw_hex, source_address,
                   is_duplicate, is_no_read, ingested_at_utc
            FROM hf811_scan_events
            WHERE collector_id = $collector
            ORDER BY event_id
            """;
        cmd.Parameters.AddWithValue("$collector", collectorId);
        using var reader = cmd.ExecuteReader();
        var events = new List<(UploadEvent Event, string IngestedAtUtc)>();
        while (reader.Read())
        {
            var scan = new UploadEvent(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.GetBoolean(7));
            events.Add((scan, reader.GetString(8)));
        }
        return events;
    }
}
