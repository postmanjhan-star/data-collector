using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HF811Collector;

/// <summary>Daily plain-text diagnostics. SQLite remains the authoritative scan history.</summary>
public static class AppLog
{
    public static event Action<string>? MessageWritten;
    private static readonly object Gate = new();
    private static string? _logDirectory;
    private static string? _currentDate;
    private static StreamWriter? _writer;

    public static void Initialize(string logDirectory)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(logDirectory);
            _logDirectory = logDirectory;
            // Keep the last 14 days of operational logs; do not delete scan history.
            foreach (string file in Directory.EnumerateFiles(logDirectory, "collector-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14)) File.Delete(file);
                }
                catch (IOException) { /* best-effort cleanup */ }
                catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
            }
        }
        WriteLine($"[Collector] Starting at {DateTimeOffset.Now:O}");
    }

    public static void WriteLine(string message)
    {
        lock (Gate)
        {
            Console.WriteLine(message);
            if (_logDirectory is null)
            {
                MessageWritten?.Invoke(message);
                return;
            }
            try
            {
                string date = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (_writer is null || date != _currentDate)
                {
                    _writer?.Dispose();
                    _currentDate = date;
                    _writer = new StreamWriter(new FileStream(
                        Path.Combine(_logDirectory, $"collector-{date}.log"),
                        FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                        { AutoFlush = true };
                }
                _writer.WriteLine($"{DateTimeOffset.Now:O} {message}");
            }
            catch (IOException e) { Console.Error.WriteLine($"[Log] {e.Message}"); }
            catch (UnauthorizedAccessException e) { Console.Error.WriteLine($"[Log] {e.Message}"); }
            // Subscribers must marshal to their own UI SynchronizationContext.
            MessageWritten?.Invoke(message);
        }
    }
}

public sealed class Settings
{
    public string Mode { get; set; } = "connect";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 55256;
    public string DeviceId { get; set; } = "SMT-LINE-01-HF811";
    public string Framing { get; set; } = "lf";
    public int IdleGapMs { get; set; } = 100;
    public int ReconnectMs { get; set; } = 1000;
    public int DedupWindowMs { get; set; } = 2000;
    public string NoReadValue { get; set; } = "NOREAD";
    public int ApiPort { get; set; } = 8088;
    public string DatabasePath { get; set; } = "";
    public bool SyncEnabled { get; set; } = false;
    public string CollectorId { get; set; } = "SMT-LINE01-PC01";
    public string ServerUrl { get; set; } = "";
    public int SyncIntervalSeconds { get; set; } = 5;
    public int SyncBatchSize { get; set; } = 50;

    public static Settings Load(string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("Configuration not found", file);
        var cfg = JsonSerializer.Deserialize<Settings>(File.ReadAllText(file),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Settings();
        if (cfg.Port is < 1 or > 65535 || cfg.ApiPort is < 1 or > 65535)
            throw new ArgumentException("Port / ApiPort must be 1..65535");
        if (cfg.IdleGapMs <= 0 || cfg.ReconnectMs <= 0 || cfg.DedupWindowMs < 0)
            throw new ArgumentException("Invalid timeout / dedup values");
        if (cfg.Framing is not ("lf" or "cr" or "crlf" or "idle"))
            throw new ArgumentException("Framing must be lf, cr, crlf, or idle");
        if (cfg.Mode is not ("connect" or "listen"))
            throw new ArgumentException("Mode must be connect or listen");
        if (string.IsNullOrWhiteSpace(cfg.DeviceId))
            throw new ArgumentException("DeviceId is required");
        ServerSyncSettings.Validate(cfg.SyncEnabled, cfg.CollectorId, cfg.ServerUrl,
            cfg.SyncIntervalSeconds, cfg.SyncBatchSize);
        if (string.IsNullOrWhiteSpace(cfg.DatabasePath))
            cfg.DatabasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HF811Collector", "data", "scans.sqlite3");
        return cfg;
    }
}

public sealed class BarcodeFramer
{
    const int MaxBytes = 4096;
    readonly byte[] _delimiter;
    readonly List<byte> _pending = new();
    public BarcodeFramer(string framing)
    {
        _delimiter = framing switch
        {
            "lf" => new byte[] { 10 },
            "cr" => new byte[] { 13 },
            "crlf" => new byte[] { 13, 10 },
            _ => throw new ArgumentException("Delimiter framing required")
        };
    }
    public List<byte[]> Feed(ReadOnlySpan<byte> bytes)
    {
        // Never assume TCP packet boundaries are barcode boundaries.
        foreach (byte b in bytes) _pending.Add(b);
        var result = new List<byte[]>();
        while (true)
        {
            int i = FindDelimiter();
            if (i < 0) break;
            if (i > MaxBytes) throw new InvalidDataException("Barcode exceeds 4096 bytes");
            if (i > 0) result.Add(_pending.GetRange(0, i).ToArray());
            _pending.RemoveRange(0, i + _delimiter.Length);
        }
        if (_pending.Count > MaxBytes + _delimiter.Length - 1)
            throw new InvalidDataException("Unterminated barcode exceeds 4096 bytes");
        return result;
    }
    int FindDelimiter()
    {
        for (int i = 0; i <= _pending.Count - _delimiter.Length; i++)
        {
            bool found = true;
            for (int j = 0; j < _delimiter.Length; j++)
                if (_pending[i + j] != _delimiter[j]) { found = false; break; }
            if (found) return i;
        }
        return -1;
    }
    public int PendingLength => _pending.Count;
    public static byte[] GetDelimiter(string key) => key switch
    {
        "lf" => new byte[] { 10 },
        "cr" => new byte[] { 13 },
        "crlf" => new byte[] { 13, 10 },
        "none" => Array.Empty<byte>(),
        _ => throw new ArgumentException("Invalid delimiter")
    };
}

public sealed partial class ScanStore : IDisposable
{
    readonly SqliteConnection _db;
    readonly object _gate = new();
    readonly Dictionary<(string, string), long> _recent = new();
    long _count;
    readonly int _dedupMs;
    readonly string _noRead;
    public ScanStore(string path, int dedupWindowMs, string noRead)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS scans (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              received_at_utc TEXT NOT NULL,
              device_id TEXT NOT NULL,
              barcode TEXT NOT NULL,
              raw_hex TEXT NOT NULL,
              source_address TEXT NOT NULL,
              is_duplicate INTEGER NOT NULL,
              is_no_read INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_scans_received ON scans(received_at_utc);
            CREATE INDEX IF NOT EXISTS idx_scans_barcode ON scans(barcode);
            """;
        cmd.ExecuteNonQuery();
        UpgradeSyncSchema();
        _dedupMs = dedupWindowMs;
        _noRead = noRead;
    }
    public void Save(byte[] raw, string device, string source)
    {
        lock (_gate)
        {
            string barcode = Encoding.UTF8.GetString(raw);
            bool noRead = barcode == _noRead;
            long now = Environment.TickCount64;
            var key = (device, barcode);
            bool dupe = !noRead && _dedupMs > 0 && _recent.TryGetValue(key, out long prev)
                && now - prev < _dedupMs;
            if (!noRead) _recent[key] = now;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO scans(event_id,received_at_utc,device_id,barcode,raw_hex,source_address,is_duplicate,is_no_read)
                VALUES($event,$time,$device,$barcode,$hex,$source,$duplicate,$noRead)
                """;
            cmd.Parameters.AddWithValue("$event", Guid.NewGuid().ToString("D"));
            cmd.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$device", device);
            cmd.Parameters.AddWithValue("$barcode", barcode);
            cmd.Parameters.AddWithValue("$hex", Convert.ToHexString(raw).ToLowerInvariant());
            cmd.Parameters.AddWithValue("$source", source);
            cmd.Parameters.AddWithValue("$duplicate", dupe ? 1 : 0);
            cmd.Parameters.AddWithValue("$noRead", noRead ? 1 : 0);
            cmd.ExecuteNonQuery();
            _count++;
            if (_count % 1000 == 0)
            {
                foreach (var stale in _recent.Where(x => now - x.Value > _dedupMs).Select(x => x.Key).ToArray())
                    _recent.Remove(stale);
            }
            AppLog.WriteLine($"[{DateTime.Now:HH:mm:ss}] {(noRead ? "NO_READ" : dupe ? "DUPLICATE" : "ACCEPTED"),-9} {device} {barcode} ({source})");
        }
    }
    public object Stats()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*),COALESCE(SUM(is_duplicate),0),COALESCE(SUM(is_no_read),0),
                    COALESCE(SUM(CASE WHEN is_duplicate=0 AND is_no_read=0 THEN 1 ELSE 0 END),0) FROM scans
                """;
            using var r = cmd.ExecuteReader();
            r.Read();
            return new { total = r.GetInt64(0), duplicates = r.GetInt64(1), no_reads = r.GetInt64(2), accepted = r.GetInt64(3) };
        }
    }
    public object Events(int limit)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT id,received_at_utc,device_id,barcode,raw_hex,source_address,is_duplicate,is_no_read FROM scans ORDER BY id DESC LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", limit);
            using var r = cmd.ExecuteReader();
            var events = new List<object>();
            while (r.Read())
                events.Add(new { id = r.GetInt64(0), received_at_utc = r.GetString(1),
                    device_id = r.GetString(2), barcode = r.GetString(3), raw_hex = r.GetString(4),
                    source_address = r.GetString(5), is_duplicate = r.GetInt64(6), is_no_read = r.GetInt64(7) });
            return new { events };
        }
    }
    public void Dispose() => _db.Dispose();
}

public static class CollectorRunner
{
    public static async Task RunAsync(Settings cfg, ScanStore store, CancellationToken token)
    {
        if (cfg.Framing == "idle")
            AppLog.WriteLine("WARNING: idle framing is heuristic; configure LF/CRLF suffix before production use.");
        if (cfg.Mode == "listen")
        {
            IPAddress addr = IPAddress.Parse(cfg.Host);
            var server = new TcpListener(addr, cfg.Port);
            server.Start();
            AppLog.WriteLine($"[Collector] Listening on {addr}:{cfg.Port}");
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await server.AcceptTcpClientAsync(token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            string source = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                            AppLog.WriteLine($"[Collector] Connected: {source}");
                            try { await ReadAsync(client.GetStream(), cfg, store, source, token); }
                            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
                            { AppLog.WriteLine($"[Collector] Client error: {ex.Message}"); }
                        }
                    }, token);
                }
            }
            finally { server.Stop(); }
        }
        else
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var tcp = new TcpClient();
                    AppLog.WriteLine($"[Collector] Connecting to {cfg.Host}:{cfg.Port}");
                    await tcp.ConnectAsync(cfg.Host, cfg.Port, token);
                    AppLog.WriteLine("[Collector] Scanner connected");
                    await ReadAsync(tcp.GetStream(), cfg, store, $"{cfg.Host}:{cfg.Port}", token);
                    AppLog.WriteLine("[Collector] Scanner disconnected");
                }
                catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
                { AppLog.WriteLine($"[Collector] Connection error: {ex.Message}"); }
                await Task.Delay(cfg.ReconnectMs, token);
            }
        }
    }
    static async Task ReadAsync(NetworkStream stream, Settings cfg, ScanStore store, string source, CancellationToken token)
    {
        var buffer = new byte[2048];
        if (cfg.Framing == "idle")
        {
            using var pending = new MemoryStream();
            while (!token.IsCancellationRequested)
            {
                int n;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(cfg.IdleGapMs);
                    n = await stream.ReadAsync(buffer, timeout.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    if (pending.Length > 0) { store.Save(pending.ToArray(), cfg.DeviceId, source); pending.SetLength(0); }
                    continue;
                }
                if (n == 0) break;
                pending.Write(buffer, 0, n);
                if (pending.Length > 4096) throw new InvalidDataException("Idle frame exceeds 4096 bytes");
            }
            if (pending.Length > 0) store.Save(pending.ToArray(), cfg.DeviceId, source);
        }
        else
        {
            var framer = new BarcodeFramer(cfg.Framing);
            int n;
            while ((n = await stream.ReadAsync(buffer, token)) > 0)
                foreach (var frame in framer.Feed(buffer.AsSpan(0, n)))
                    store.Save(frame, cfg.DeviceId, source);
            if (framer.PendingLength > 0)
                AppLog.WriteLine($"[Collector] Dropped {framer.PendingLength} unterminated bytes on disconnect");
        }
    }
}

public static class SimulatorRunner
{
    public static async Task RunAsync(string[] args, CancellationToken token)
    {
        string mode = Arg(args, "--mode", "server");
        string host = Arg(args, "--host", "127.0.0.1");
        int port = int.Parse(Arg(args, "--port", "55256"));
        int interval = int.Parse(Arg(args, "--interval-ms", "200"));
        int count = int.Parse(Arg(args, "--count", "0"));
        int every = int.Parse(Arg(args, "--repeat-every", "5"));
        int noReadEvery = int.Parse(Arg(args, "--no-read-every", "0"));
        int disconnectEvery = int.Parse(Arg(args, "--disconnect-every", "0"));
        string suffix = Arg(args, "--delimiter", "lf");
        if (mode is not ("server" or "client") || port is < 1 or > 65535 || interval < 0 || count < 0 || every < 0 || noReadEvery < 0 || disconnectEvery < 0)
            throw new ArgumentException("Invalid simulator argument");
        var delimiter = BarcodeFramer.GetDelimiter(suffix);
        int sent = 0;
        string prev = "";
        async Task SendAsync(NetworkStream stream)
        {
            int sessionSent = 0;
            while (!token.IsCancellationRequested && (count == 0 || sent < count))
            {
                sent++;
                string code = noReadEvery > 0 && sent % noReadEvery == 0 ? "NOREAD"
                    : every > 0 && sent % every == 0 && prev != "" ? prev
                    : $"PCB{sent:0000000000}";
                if (code != "NOREAD") prev = code;
                byte[] packet = Encoding.ASCII.GetBytes(code).Concat(delimiter).ToArray();
                await stream.WriteAsync(packet, token);
                Console.WriteLine($"[Simulator] TX #{sent}: {code}");
                sessionSent++;
                if (disconnectEvery > 0 && sessionSent >= disconnectEvery) break;
                if (count > 0 && sent >= count) break;
                await Task.Delay(interval, token);
            }
        }
        if (mode == "server")
        {
            var server = new TcpListener(IPAddress.Parse(host), port);
            server.Start();
            Console.WriteLine($"[Simulator] TCP Server {host}:{port}");
            try
            {
                while (!token.IsCancellationRequested && (count == 0 || sent < count))
                {
                    using var client = await server.AcceptTcpClientAsync(token);
                    try { await SendAsync(client.GetStream()); }
                    catch (Exception ex) when (ex is IOException or SocketException)
                    { Console.WriteLine($"[Simulator] Disconnected: {ex.Message}"); }
                }
            }
            finally { server.Stop(); }
        }
        else
        {
            while (!token.IsCancellationRequested && (count == 0 || sent < count))
            {
                try
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(host, port, token);
                    await SendAsync(tcp.GetStream());
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                { Console.WriteLine($"[Simulator] Reconnect: {ex.Message}"); }
                await Task.Delay(1000, token);
            }
        }
    }
    public static string Arg(string[] args, string name, string fallback)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0) return fallback;
        if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {name}");
        return args[index + 1];
    }
}

public static class LocalApi
{
    public static async Task RunAsync(ScanStore store, int port, CancellationToken token)
    {
        // Listen on loopback only. No firewall rule or HTTP.sys URL ACL is required.
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        AppLog.WriteLine($"[API] http://127.0.0.1:{port}/stats | /events?limit=20 | /health");
        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient tcp = await listener.AcceptTcpClientAsync(token);
                _ = Task.Run(async () =>
                {
                    using (tcp)
                    {
                        try
                        {
                            tcp.ReceiveTimeout = 5000;
                            using var stream = tcp.GetStream();
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(5));
                            var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
                            string? start = await reader.ReadLineAsync(timeout.Token);
                            if (start is null || start.Length > 2048) return;
                            for (int i = 0; i < 100; i++)
                            {
                                var line = await reader.ReadLineAsync(timeout.Token);
                                if (line is null || line.Length == 0) break;
                                if (line.Length > 4096) return;
                            }
                            string[] tokens = start.Split(' ', 3);
                            int status = 200;
                            object body;
                            if (tokens.Length < 2 || tokens[0] != "GET")
                            { status = 405; body = new { error = "Only GET is supported" }; }
                            else
                            {
                                var uri = new Uri("http://localhost" + tokens[1]);
                                string path = uri.AbsolutePath;
                                if (path == "/health") body = new { status = "ok" };
                                else if (path == "/stats") body = store.Stats();
                                else if (path == "/events")
                                {
                                    int limit = 20;
                                    foreach (var p in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                                        if (p.StartsWith("limit=", StringComparison.OrdinalIgnoreCase)
                                            && int.TryParse(p[6..], out int parsed)) limit = parsed;
                                    body = store.Events(Math.Clamp(limit, 1, 500));
                                }
                                else { status = 404; body = new { error = "Not Found" }; }
                            }
                            var json = JsonSerializer.Serialize(body);
                            var payload = Encoding.UTF8.GetBytes(json);
                            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\n\r\n");
                            await stream.WriteAsync(head, token);
                            await stream.WriteAsync(payload, token);
                        }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException or UriFormatException or ArgumentException)
                        { /* Drop malformed or aborted HTTP requests */ }
                    }
                }, token);
            }
        }
        finally { listener.Stop(); }
    }
}

public static class SelfTest
{
    public static int Run()
    {
        var f = new BarcodeFramer("crlf");
        if (f.Feed(Encoding.ASCII.GetBytes("PCB001\r")).Count != 0)
            throw new Exception("CRLF split test part 1 failed");
        var frames = f.Feed(Encoding.ASCII.GetBytes("\nPCB002\r\n"));
        if (frames.Count != 2 || Encoding.ASCII.GetString(frames[0]) != "PCB001"
            || Encoding.ASCII.GetString(frames[1]) != "PCB002")
            throw new Exception("TCP framing split / coalesced test failed");
        var folder = Path.Combine(Path.GetTempPath(), "HF811-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using (var store = new ScanStore(Path.Combine(folder, "test.sqlite3"), 5000, "NOREAD"))
            {
                store.Save(Encoding.UTF8.GetBytes("PCB001"), "TEST", "127.0.0.1");
                store.Save(Encoding.UTF8.GetBytes("PCB001"), "TEST", "127.0.0.1");
                store.Save(Encoding.UTF8.GetBytes("NOREAD"), "TEST", "127.0.0.1");
                using var stats = JsonDocument.Parse(JsonSerializer.Serialize(store.Stats()));
                var root = stats.RootElement;
                if (root.GetProperty("total").GetInt64() != 3 || root.GetProperty("accepted").GetInt64() != 1
                    || root.GetProperty("duplicates").GetInt64() != 1 || root.GetProperty("no_reads").GetInt64() != 1)
                    throw new Exception("SQLite / dedup / no-read test failed");
            }
            SyncSelfTest.Run();
            Console.WriteLine("SELFTEST PASS: TCP framing, SQLite, duplicate marking and NOREAD.");
            return 0;
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { /* SQLite WAL cleanup may be delayed by Windows AV */ }
        }
    }
}

public static class ConfigBootstrap
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HF811Collector", "config.json");

    // All-users setup installs under Program Files, but each Windows account owns its
    // own configuration and SQLite database. Never write user data from elevated Setup.
    public static string EnsureDefault()
    {
        string configPath = DefaultPath;
        if (File.Exists(configPath)) return configPath;
        string template = Path.Combine(AppContext.BaseDirectory, "config.default.json");
        if (!File.Exists(template))
            throw new FileNotFoundException("Bundled default configuration not found", template);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        // Write to a temporary file, then rename without overwriting an existing config.
        // This prevents interrupted first-run initialization leaving a truncated JSON file.
        string tempPath = configPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(template, tempPath);
            try { File.Move(tempPath, configPath); }
            catch (IOException) when (File.Exists(configPath))
            {
                // A concurrent Collector startup already created the config.
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        return configPath;
    }

    public static string EnsureWhenDefault(string configPath)
    {
        if (string.Equals(Path.GetFullPath(configPath), Path.GetFullPath(DefaultPath),
            StringComparison.OrdinalIgnoreCase))
            EnsureDefault();
        return configPath;
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            Console.WriteLine("""
                HF811 Collector Lab (C#)
                  HF811Collector.exe collector --config "C:\path\config.json"
                  HF811Collector.exe init-config
                  HF811Collector.exe edit-config
                  HF811Collector.exe open-data
                  HF811Collector.exe simulator [--mode server|client] [--host 127.0.0.1]
                    [--port 55256] [--interval-ms 200] [--count 0]
                    [--repeat-every 5] [--no-read-every 0] [--disconnect-every 0]
                    [--delimiter lf|cr|crlf|none]
                """);
            return 0;
        }
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        try
        {
            if (args[0] == "selftest") return SelfTest.Run();
            if (args[0] == "sync-e2e") return await SyncIntegrationTest.RunAsync(args, stop.Token);
            if (args[0] == "init-config")
            {
                Console.WriteLine(ConfigBootstrap.EnsureDefault());
                return 0;
            }
            if (args[0] == "edit-config")
            {
                string path = ConfigBootstrap.EnsureDefault();
                var editor = new ProcessStartInfo("notepad.exe") { UseShellExecute = false };
                editor.ArgumentList.Add(path);
                Process.Start(editor);
                return 0;
            }
            if (args[0] == "open-data")
            {
                ConfigBootstrap.EnsureDefault();
                string folder = Path.GetDirectoryName(ConfigBootstrap.DefaultPath)!;
                var explorer = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                explorer.ArgumentList.Add(folder);
                Process.Start(explorer);
                return 0;
            }
            if (args[0] == "simulator")
            {
                await SimulatorRunner.RunAsync(args, stop.Token);
                return 0;
            }
            if (args[0] != "collector") throw new ArgumentException("Use collector or simulator");
            // Same name as Inno Setup AppMutex. Protect against duplicate startup/manual launches.
            using var singleInstance = new Mutex(false, "HF811Collector-Collector", out bool firstInstance);
            if (!firstInstance)
            {
                Console.Error.WriteLine("Collector is already running in this Windows session.");
                return 3;
            }
            string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HF811Collector");
            AppLog.Initialize(Path.Combine(dataDir, "logs"));
            string configPath = SimulatorRunner.Arg(args, "--config", Path.Combine(dataDir, "config.json"));
            ConfigBootstrap.EnsureWhenDefault(configPath);
            Settings cfg = Settings.Load(configPath);
            using var store = new ScanStore(cfg.DatabasePath, cfg.DedupWindowMs, cfg.NoReadValue);
            var api = LocalApi.RunAsync(store, cfg.ApiPort, stop.Token);
            var collector = CollectorRunner.RunAsync(cfg, store, stop.Token);
            using var sync = new SyncAgent(cfg, store);
            var syncTask = sync.RunAsync(stop.Token);
            // If any worker exits unexpectedly, stop the other workers instead of hiding a fault.
            var finished = await Task.WhenAny(api, collector, syncTask);
            if (!stop.IsCancellationRequested)
            {
                AppLog.WriteLine("[Collector] A worker terminated; shutting down.");
                stop.Cancel();
            }
            try { await Task.WhenAll(api, collector, syncTask); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            // Preserve failures from a completed worker (a fault must not look successful).
            if (finished.IsFaulted) await finished;
            return 0;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { return 0; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex}");
            if (args.Length > 0 && args[0] == "collector") AppLog.WriteLine($"FATAL: {ex}");
            return 1;
        }
    }
}
