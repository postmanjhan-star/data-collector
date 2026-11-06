using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HF811Collector.Desktop;

internal sealed record AdapterNetwork(string Name, string LocalIp, string Prefix)
{
    public override string ToString() => $"{Name}  ·  {LocalIp}  →  {Prefix}.0/24";
}

internal sealed record DiscoveryCandidate(string Ip, int Port)
{
    public override string ToString() => $"{Ip}:{Port}";
}

internal sealed record ScanProgress(int Probed, int Total, DiscoveryCandidate? Found);

internal sealed record SampleResult(string? Framing, string HexPreview, int BytesRead)
{
    public string Description => Framing switch
    {
        "crlf" => "觀察到 CRLF (0D 0A)，可能是條碼尾碼",
        "lf" => "觀察到 LF (0A)，可能是條碼尾碼",
        "cr" => "觀察到 CR (0D)，可能是條碼尾碼",
        _ => BytesRead == 0
            ? "沒有收到條碼資料；請讓掃描器讀取測試條碼後再試"
            : "收到資料但未觀察到明確尾碼，不能自動判定 framing"
    };
}

/// <summary>
/// Read-only opt-in TCP probe. This is NOT the proprietary Honeywell discovery
/// protocol and never claims to identify a scanner from an open port.
/// </summary>
internal static class DeviceDiscovery
{
    public const int DefaultPort = 55256;
    public static IReadOnlyList<AdapterNetwork> GetAdapters()
    {
        var adapters = new List<AdapterNetwork>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var item in nic.GetIPProperties().UnicastAddresses)
            {
                if (item.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var bytes = item.Address.GetAddressBytes();
                if (bytes[0] == 127 || bytes[0] == 0 || bytes[0] >= 224) continue;
                var prefix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}";
                adapters.Add(new AdapterNetwork(nic.Name, item.Address.ToString(), prefix));
            }
        }
        return adapters.GroupBy(x => x.Prefix + "|" + x.LocalIp)
            .Select(x => x.First()).ToArray();
    }

    public static string ValidatePrefix(string prefix)
    {
        string[] parts = prefix.Trim().Split('.');
        if (parts.Length != 3 || !parts.All(x => byte.TryParse(x, out _)))
            throw new ArgumentException("請輸入合法的 IPv4 /24 前三段，例如 192.168.16");
        var b = parts.Select(byte.Parse).ToArray();
        if (b[0] == 0 || b[0] == 127 || b[0] >= 224)
            throw new ArgumentException("此網段不適合掃描，請使用已授權的區網 IPv4 網段");
        return string.Join(".", b);
    }

    public static async Task<IReadOnlyList<DiscoveryCandidate>> ScanAsync(
        string prefix, int port, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        prefix = ValidatePrefix(prefix);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        const int total = 254;
        int tested = 0;
        var candidates = new ConcurrentBag<DiscoveryCandidate>();
        await Parallel.ForEachAsync(Enumerable.Range(1, total),
            new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = cancellationToken },
            async (suffix, ct) =>
            {
                var ip = $"{prefix}.{suffix}";
                DiscoveryCandidate? found = null;
                try
                {
                    using var tcp = new TcpClient(AddressFamily.InterNetwork);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromMilliseconds(450));
                    await tcp.ConnectAsync(IPAddress.Parse(ip), port, deadline.Token);
                    found = new DiscoveryCandidate(ip, port);
                    candidates.Add(found);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (SocketException) { }
                catch (IOException) { }
                finally { progress?.Report(new ScanProgress(Interlocked.Increment(ref tested), total, found)); }
            });
        return candidates.OrderBy(x => IPAddress.Parse(x.Ip).GetAddressBytes(), ByteArrayComparer.Instance).ToArray();
    }

    // Only read bytes; no trigger, configuration commands, or write to the device.
    // Must not run while Collector already owns the scanner TCP session.
    public static async Task<SampleResult> SampleFramingAsync(string ip, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var bytes = new List<byte>(512);
        try
        {
            using var tcp = new TcpClient(AddressFamily.InterNetwork);
            await tcp.ConnectAsync(IPAddress.Parse(ip), port, timeout.Token);
            await using var stream = tcp.GetStream();
            var buffer = new byte[256];
            while (bytes.Count < 1024)
            {
                int read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0) break;
                bytes.AddRange(buffer.AsSpan(0, read).ToArray());
                if (bytes.Contains((byte)10) || bytes.Contains((byte)13)) break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        string? framing = null;
        for (int i = 0; i < bytes.Count; i++)
        {
            if (bytes[i] == 13 && i + 1 < bytes.Count && bytes[i + 1] == 10) { framing = "crlf"; break; }
            if (bytes[i] == 10) { framing = "lf"; break; }
            if (bytes[i] == 13) framing ??= "cr";
        }
        var sample = bytes.Take(72).ToArray();
        return new SampleResult(framing, Convert.ToHexString(sample), bytes.Count);
    }

    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static ByteArrayComparer Instance { get; } = new();
        public int Compare(byte[]? a, byte[]? b)
        {
            if (a is null || b is null) return a is null ? b is null ? 0 : -1 : 1;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return a.Length.CompareTo(b.Length);
        }
    }
}
