using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace HF811Collector;

/// <summary>Opt-in ephemeral end-to-end test. Never called by production Collector.</summary>
public static class SyncIntegrationTest
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        string url = SimulatorRunner.Arg(args, "--url", "");
        string key = Environment.GetEnvironmentVariable("HF811_SYNC_API_KEY") ?? "";
        if (key.Length < 16) throw new ArgumentException("sync-e2e requires HF811_SYNC_API_KEY env var");
        ServerSyncSettings.Validate(true, "E2E-TEST", url, 5, 10);
        string folder = Path.Combine(Path.GetTempPath(), "hf811-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var config = new Settings
            {
                SyncEnabled = true,
                ServerUrl = url,
                CollectorId = "E2E-" + Guid.NewGuid().ToString("N"),
                SyncBatchSize = 10,
                SyncIntervalSeconds = 5,
                DatabasePath = Path.Combine(folder, "scans.sqlite3")
            };
            using var store = new ScanStore(config.DatabasePath, 2000, "NOREAD");
            store.Save(Encoding.ASCII.GetBytes("PCB001"), "TEST", "loopback");
            store.Save(Encoding.ASCII.GetBytes("PCB001"), "TEST", "loopback");
            store.Save(Encoding.ASCII.GetBytes("NOREAD"), "TEST", "loopback");
            var original = store.PendingBatch(10);
            if (original.Count != 3) throw new InvalidOperationException("Expected three pending scan events");

            // Simulate a receiver/network outage. All events must stay pending with stable IDs.
            var offlineConfig = new Settings
            {
                SyncEnabled = true, CollectorId = config.CollectorId,
                ServerUrl = "http://127.0.0.1:1/api/v1/collector/scans/batch",
                SyncBatchSize = 10, SyncIntervalSeconds = 5,
                DatabasePath = config.DatabasePath
            };
            using (var offline = new SyncAgent(offlineConfig, store))
            {
                if (await offline.SyncOnceAsync(cancellationToken) != 0 ||
                    store.GetSyncState().Pending != 3 ||
                    store.PendingBatch(10).Select(x => x.EventId).SequenceEqual(original.Select(x => x.EventId)) is false)
                    throw new InvalidOperationException("Offline upload modified pending event identities");
            }
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("X-Collector-Key", key);
            var statsUri = new Uri(new Uri(url).GetLeftPart(UriPartial.Authority) + "/api/v1/collector/stats");
            async Task<long> ServerCount()
            {
                using var response = await client.GetAsync(statsUri, cancellationToken);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                return document.RootElement.GetProperty("total").GetInt64();
            }
            long before = await ServerCount();
            using var sync = new SyncAgent(config, store);
            int ack = await sync.SyncOnceAsync(cancellationToken);
            if (ack != 3 || store.GetSyncState().Pending != 0 ||
                store.GetSyncState().Synced != 3)
                throw new InvalidOperationException("SyncAgent did not commit expected receipt");
            if (await ServerCount() != before + 3)
                throw new InvalidOperationException("Receiver did not durably insert three scans");

            // Simulate a lost client-side ACK: resend the EXACT event IDs. Receiver must
            // ACK them again without duplicating server rows.
            using var replay = await client.PostAsJsonAsync(url, new UploadEnvelope(config.CollectorId, original), cancellationToken);
            replay.EnsureSuccessStatusCode();
            var repeated = await replay.Content.ReadFromJsonAsync<UploadReceipt>(cancellationToken);
            if (repeated?.AcknowledgedEventIds.Count != 3 || await ServerCount() != before + 3)
                throw new InvalidOperationException("Receiver idempotent replay failed");

            using var unauthorized = new HttpClient();
            using var denied = await unauthorized.PostAsJsonAsync(url,
                new UploadEnvelope(config.CollectorId, original), cancellationToken);
            if (denied.StatusCode != System.Net.HttpStatusCode.Unauthorized)
                throw new InvalidOperationException("Receiver accepted batch without authentication");
            Console.WriteLine("SYNC E2E PASS: batch accepted, SQLite ACK, idempotent replay, unauthorized rejected.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (IOException) { }
        }
    }
}
