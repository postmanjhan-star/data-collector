using System.Net.Http.Json;
using System.Text;

namespace HF811Collector;

public static class MqttIntegrationTest
{
    public static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        string url = SimulatorRunner.Arg(args, "--url", "mqtt://127.0.0.1:1883");
        string stats = SimulatorRunner.Arg(args, "--stats-url", "http://127.0.0.1:18090/api/v1/collector/stats");
        string key = SyncSecrets.Read();
        string apiKey = Environment.GetEnvironmentVariable("HF811_SERVER_API_KEY") ?? throw new InvalidOperationException("Missing test receiver key");
        if (!new Uri(url).IsLoopback || !new Uri(stats).IsLoopback) throw new ArgumentException("CI loopback only");
        var folder = Path.Combine(Path.GetTempPath(), "mqtt-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var cfg = new Settings { CollectorId = "CI-MQTT", SyncEnabled = true, ServerUrl = url,
                DatabasePath = Path.Combine(folder, "scans.sqlite3"), SyncBatchSize = 10 };
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("X-Collector-Key", apiKey);
            async Task<long> Count()
            {
                using var response = await http.GetAsync(stats, token);
                response.EnsureSuccessStatusCode();
                using var doc = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                return doc.RootElement.GetProperty("total").GetInt64();
            }
            var before = await Count();
            IReadOnlyList<UploadEvent> original;
            using (var store = new ScanStore(cfg.DatabasePath, 2000, "NOREAD"))
            {
                store.Save(Encoding.ASCII.GetBytes("PCB001"), "SC2-UPPER", "loopback");
                store.Save(Encoding.ASCII.GetBytes("PCB001"), "SC2-UPPER", "loopback");
                store.Save(Encoding.ASCII.GetBytes("NOREAD"), "SC2-LOWER", "loopback");
                original = store.PendingBatch(10);
                var offline = new Settings { CollectorId = cfg.CollectorId, SyncEnabled = true,
                    ServerUrl = "mqtt://127.0.0.1:1", SyncBatchSize = 10 };
                using var agent = new SyncAgent(offline, store);
                if (await agent.SyncOnceAsync(token) != 0 || store.GetSyncState().Pending != 3)
                    throw new InvalidOperationException("Offline records lost");
            }
            using (var store = new ScanStore(cfg.DatabasePath, 2000, "NOREAD"))
            {
                if (!store.PendingBatch(10).Select(x => x.EventId).SequenceEqual(original.Select(x => x.EventId)))
                    throw new InvalidOperationException("Event identities changed after restart");
                using var agent = new SyncAgent(cfg, store);
                if (await agent.SyncOnceAsync(token) != 3 || store.GetSyncState().Synced != 3 || await Count() != before + 3)
                    throw new InvalidOperationException("Soda commit/ACK failed");
                var replay = await MqttBatchTransport.SendAsync(url, key, new UploadEnvelope(cfg.CollectorId, original), token);
                if (replay.AcknowledgedEventIds.Count != 3 || await Count() != before + 3)
                    throw new InvalidOperationException("Replay not idempotent");
                var changed = original[0] with { Barcode = "CHANGED" };
                var conflict = await MqttBatchTransport.SendAsync(url, key, new UploadEnvelope(cfg.CollectorId, new[] { changed }), token);
                if (conflict.Rejected.Count != 1 || conflict.AcknowledgedEventIds.Count != 0 || await Count() != before + 3)
                    throw new InvalidOperationException("Soda failed UUID conflict check");
            }
            Console.WriteLine("MQTT SODA E2E PASS: offline SQLite, restart, commit ACK, replay, conflict");
            return 0;
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
