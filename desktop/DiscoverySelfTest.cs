using System.Net;
using System.Net.Sockets;

namespace HF811Collector.Desktop;

internal static class DiscoverySelfTest
{
    public static void Run()
    {
        if (DeviceDiscovery.ValidatePrefix("192.168.016") != "192.168.16")
            throw new InvalidOperationException("Private subnet normalization failed");
        if (DeviceDiscovery.ValidatePrefix("169.254.7") != "169.254.7")
            throw new InvalidOperationException("Link-local subnet validation failed");
        foreach (string notAllowed in new[] { "8.8.8", "127.0.0", "192.168", "foo.bar.x", "225.1.2" })
        {
            try
            {
                DeviceDiscovery.ValidatePrefix(notAllowed);
                throw new InvalidOperationException("Unsafe subnet accepted: " + notAllowed);
            }
            catch (ArgumentException) { }
        }

        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        int port = ((IPEndPoint)server.LocalEndpoint).Port;
        Task writer = Task.Run(async () =>
        {
            using var client = await server.AcceptTcpClientAsync();
            await client.GetStream().WriteAsync("PCB-TEST\r\n"u8.ToArray());
        });
        var sample = DeviceDiscovery.SampleFramingAsync("127.0.0.1", port, CancellationToken.None)
            .GetAwaiter().GetResult();
        writer.GetAwaiter().GetResult();
        if (sample.Framing != "crlf" || sample.BytesRead < 10)
            throw new InvalidOperationException("Read-only TCP framing sampling failed");
    }
}
