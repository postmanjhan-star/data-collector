using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HF811Collector;

internal static class ScannerProfilesSelfTest
{
    internal static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hf811-multi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var store = new ScanStore(Path.Combine(folder, "multi.sqlite3"), 5000, "NOREAD");
            using var upper = new TcpListener(IPAddress.Loopback, 0);
            upper.Start();
            int upperPort = ((IPEndPoint)upper.LocalEndpoint).Port;
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            int lowerPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var config = new Settings
            {
                ScannerProfiles = new()
                {
                    new() { DeviceId = "SC2-UPPER", Port = upperPort, Framing = "crlf", TriggerOnHex = "54524947474552" },
                    new() { DeviceId = "SC2-LOWER", Transport = "tcp-listen", Port = lowerPort, ExpectedPeerIp = "127.0.0.1" }
                }
            };
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var worker = ScannerProfilesRunner.RunAsync(config, store, stop.Token);
            try
            {
                using var upperConnection = await upper.AcceptTcpClientAsync(stop.Token);
                using var lower = new TcpClient();
                while (true)
                {
                    try { await lower.ConnectAsync(IPAddress.Loopback, lowerPort, stop.Token); break; }
                    catch (SocketException) { await Task.Delay(50, stop.Token); }
                }
                while (!store.CanTrigger("SC2-UPPER", true)) await Task.Delay(20, stop.Token);
                if (store.CanTrigger("SC2-LOWER", true)) throw new InvalidOperationException("Unconfigured trigger enabled");
                await store.TriggerAsync("SC2-UPPER", true, stop.Token);
                byte[] triggerBytes = new byte[7];
                await upperConnection.GetStream().ReadExactlyAsync(triggerBytes, stop.Token);
                if (Encoding.ASCII.GetString(triggerBytes) != "TRIGGER") throw new InvalidOperationException("Wrong configured trigger bytes");
                await upperConnection.GetStream().WriteAsync(Encoding.ASCII.GetBytes("PCB001\r"), stop.Token);
                await upperConnection.GetStream().WriteAsync(Encoding.ASCII.GetBytes("\nPCB001\r\n"), stop.Token);
                await lower.GetStream().WriteAsync(Encoding.ASCII.GetBytes("PCB001\nNOREAD\n"), stop.Token);
                while (true)
                {
                    using var stats = JsonDocument.Parse(JsonSerializer.Serialize(store.Stats()));
                    if (stats.RootElement.GetProperty("total").GetInt64() == 4)
                    {
                        if (stats.RootElement.GetProperty("accepted").GetInt64() != 2 ||
                            stats.RootElement.GetProperty("duplicates").GetInt64() != 1 ||
                            stats.RootElement.GetProperty("no_reads").GetInt64() != 1)
                            throw new InvalidOperationException("Multi-scanner dedup isolation failed");
                        break;
                    }
                    await Task.Delay(50, stop.Token);
                }
                var snapshots = store.DeviceSnapshots();
                if (snapshots.Count != 2 || snapshots.Any(s => s.State != "connected" || s.Total != 2))
                    throw new InvalidOperationException("Device status/statistics not independent");
                var events = store.PendingBatch(10);
                if (events.Count(e => e.DeviceId == "SC2-UPPER") != 2 ||
                    events.Count(e => e.DeviceId == "SC2-LOWER") != 2)
                    throw new InvalidOperationException("Scanner identity lost");
                config.ScannerProfiles[1].DeviceId = "SC2-UPPER";
                bool rejected = false;
                try { ScannerProfile.ValidateAll(config.ScannerProfiles, config.ApiPort); }
                catch (ArgumentException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Duplicate device ID accepted");
                Console.WriteLine("MULTI SCANNER PASS: concurrent TCP connect/listen, separate identity, framing, dedup, NOREAD");
            }
            finally
            {
                stop.Cancel();
                try { await worker; } catch (OperationCanceledException) { }
            }
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }
}
