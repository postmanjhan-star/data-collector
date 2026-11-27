using System.Diagnostics;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;

namespace HF811Collector;

// Empty profile list preserves legacy single-scanner configuration and wire schema.
public sealed class ScannerProfile
{
    public string DeviceId { get; set; } = "";
    public string Transport { get; set; } = "tcp-connect";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 55256;
    public string ExpectedPeerIp { get; set; } = "";
    public string Framing { get; set; } = "lf";
    public int IdleGapMs { get; set; } = 100;
    public int ReconnectMs { get; set; } = 1000;
    public int BaudRate { get; set; } = 115200;
    public int DataBits { get; set; } = 8;
    public Parity Parity { get; set; } = Parity.None;
    public StopBits StopBits { get; set; } = StopBits.One;
    public Handshake Handshake { get; set; } = Handshake.None;

    public static void ValidateAll(IReadOnlyList<ScannerProfile> profiles, int apiPort)
    {
        if (profiles == null || profiles.Count > 16) throw new ArgumentException("ScannerProfiles must contain 0..16 devices");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var listenerPorts = new HashSet<int>();
        foreach (var s in profiles)
        {
            if (s == null || string.IsNullOrWhiteSpace(s.DeviceId) || s.DeviceId.Length > 96 || !ids.Add(s.DeviceId.Trim()))
                throw new ArgumentException("Each scanner needs a unique DeviceId (1..96 characters)");
            if (string.IsNullOrWhiteSpace(s.Host) || s.Framing is not ("lf" or "cr" or "crlf" or "idle") ||
                s.IdleGapMs is < 1 or > 600000 || s.ReconnectMs is < 100 or > 600000)
                throw new ArgumentException("Invalid scanner endpoint/framing/timeouts: " + s.DeviceId);
            switch (s.Transport)
            {
                case "tcp-connect":
                    if (s.Port is < 1 or > 65535 || !endpoints.Add("tcp:" + s.Host.Trim() + ":" + s.Port))
                        throw new ArgumentException("Duplicate or invalid TCP scanner endpoint");
                    break;
                case "tcp-listen":
                    if (s.Port is < 1 or > 65535 || s.Port == apiPort || !listenerPorts.Add(s.Port) ||
                        !IPAddress.TryParse(s.Host, out var bind) ||
                        (!IPAddress.IsLoopback(bind) && string.IsNullOrWhiteSpace(s.ExpectedPeerIp)) ||
                        (!string.IsNullOrWhiteSpace(s.ExpectedPeerIp) && !IPAddress.TryParse(s.ExpectedPeerIp, out _)))
                        throw new ArgumentException("Listener requires unique local port, bind IP and expected remote scanner IP (except loopback)");
                    break;
                case "serial":
                    if (!endpoints.Add("serial:" + s.Host.Trim()) || s.BaudRate is < 300 or > 3000000 ||
                        s.DataBits is < 5 or > 8 || !Enum.IsDefined(s.Parity) || !Enum.IsDefined(s.StopBits) ||
                        s.StopBits == StopBits.None || !Enum.IsDefined(s.Handshake))
                        throw new ArgumentException("Duplicate or invalid serial scanner settings");
                    break;
                default: throw new ArgumentException("Unknown scanner transport: " + s.Transport);
            }
        }
    }

    internal Settings ToEndpoint() => new()
    {
        Mode = Transport == "tcp-listen" ? "listen" : "connect",
        Host = Host.Trim(), Port = Port, DeviceId = DeviceId.Trim(),
        Framing = Framing, IdleGapMs = IdleGapMs, ReconnectMs = ReconnectMs
    };
}

public static class ScannerProfilesRunner
{
    public static async Task RunAsync(Settings config, ScanStore store, CancellationToken token)
    {
        ScannerProfile.ValidateAll(config.ScannerProfiles, config.ApiPort);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var workers = config.ScannerProfiles.Select(profile => Task.Run(async () =>
        {
            store.SetDeviceState(profile.DeviceId.Trim(), "starting");
            var endpoint = profile.ToEndpoint();
            if (profile.Transport == "serial") await Task.Run(() => RunSerial(profile, store, stop.Token), stop.Token);
            else if (profile.Transport == "tcp-listen") await RunListener(profile, endpoint, store, stop.Token);
            else await CollectorRunner.RunAsync(endpoint, store, stop.Token);
        }, stop.Token)).ToArray();
        if (workers.Length == 0) throw new ArgumentException("No scanner profiles");
        try
        {
            var first = await Task.WhenAny(workers);
            await first;
            if (!stop.IsCancellationRequested) throw new InvalidOperationException("Scanner worker exited unexpectedly");
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(workers); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static async Task RunListener(ScannerProfile profile, Settings endpoint, ScanStore store, CancellationToken token)
    {
        var server = new TcpListener(IPAddress.Parse(endpoint.Host), endpoint.Port);
        server.Start();
        try
        {
            store.SetDeviceState(endpoint.DeviceId, "listening");
            AppLog.WriteLine($"[Scanner {endpoint.DeviceId}] Listening on {endpoint.Host}:{endpoint.Port}");
            while (!token.IsCancellationRequested)
            {
                using var client = await server.AcceptTcpClientAsync(token);
                var peer = (IPEndPoint?)client.Client.RemoteEndPoint;
                if (!string.IsNullOrWhiteSpace(profile.ExpectedPeerIp) &&
                    !Equals(peer?.Address, IPAddress.Parse(profile.ExpectedPeerIp)))
                {
                    AppLog.WriteLine($"[Scanner {endpoint.DeviceId}] Unexpected peer rejected");
                    continue;
                }
                store.SetDeviceState(endpoint.DeviceId, "connected");
                AppLog.WriteLine($"[Scanner {endpoint.DeviceId}] Connected: {peer}");
                try { await CollectorRunner.ReadAsync(client.GetStream(), endpoint, store, peer?.ToString() ?? "unknown", token); }
                catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
                { AppLog.WriteLine($"[Scanner {endpoint.DeviceId}] Read error: {ex.Message}"); }
                finally { store.SetDeviceState(endpoint.DeviceId, "listening"); }
            }
        }
        finally { server.Stop(); }
    }

    private static void RunSerial(ScannerProfile profile, ScanStore store, CancellationToken token)
    {
        var buffer = new byte[2048];
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var port = new SerialPort(profile.Host.Trim(), profile.BaudRate, profile.Parity, profile.DataBits, profile.StopBits)
                { Handshake = profile.Handshake, ReadTimeout = Math.Clamp(profile.IdleGapMs, 1, 100) };
                store.SetDeviceState(profile.DeviceId.Trim(), "connecting");
                port.Open();
                store.SetDeviceState(profile.DeviceId.Trim(), "connected");
                AppLog.WriteLine($"[Scanner {profile.DeviceId}] Serial open: {port.PortName}");
                var framer = profile.Framing == "idle" ? null : new BarcodeFramer(profile.Framing);
                using var pending = new MemoryStream();
                var lastData = Stopwatch.StartNew();
                void Save(byte[] raw) => store.Save(raw, profile.DeviceId.Trim(), "serial:" + port.PortName);
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        int n = port.Read(buffer, 0, buffer.Length);
                        if (n <= 0) continue;
                        lastData.Restart();
                        if (framer != null)
                        {
                            foreach (var frame in framer.Feed(buffer.AsSpan(0, n))) Save(frame);
                        }
                        else
                        {
                            pending.Write(buffer, 0, n);
                            if (pending.Length > 4096) throw new InvalidDataException("Serial idle frame exceeds 4096 bytes");
                        }
                    }
                    catch (TimeoutException)
                    {
                        if (framer == null && pending.Length > 0 && lastData.ElapsedMilliseconds >= profile.IdleGapMs)
                        { Save(pending.ToArray()); pending.SetLength(0); }
                    }
                }
                if ((framer?.PendingLength ?? pending.Length) > 0)
                    AppLog.WriteLine($"[Scanner {profile.DeviceId}] Incomplete serial frame on stop");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            { store.SetDeviceState(profile.DeviceId.Trim(), "retrying"); AppLog.WriteLine($"[Scanner {profile.DeviceId}] Serial error: {ex.Message}"); }
            if (token.WaitHandle.WaitOne(profile.ReconnectMs)) break;
        }
    }
}
