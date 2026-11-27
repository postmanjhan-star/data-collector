using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HF811Collector;

// Diagnostic adapter only. No writes, triggers, polling, auto discovery, or guessed register mapping.
public interface IPlcReadAdapter
{
    Task<ushort[]> ReadAsync(string host, int port, byte unit, byte function, ushort address, ushort count, CancellationToken token);
}

public sealed class ModbusTcpReadAdapter : IPlcReadAdapter
{
    public async Task<ushort[]> ReadAsync(string host, int port, byte unit, byte function, ushort address, ushort count, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535 || function is not (3 or 4) ||
            count is < 1 or > 125 || (int)address + count > 65536)
            throw new ArgumentException("Invalid read-only Modbus request");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        var ct = deadline.Token;
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        var stream = client.GetStream();
        byte[] request = new byte[12];
        ushort transaction = (ushort)Random.Shared.Next(1, 65536);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), transaction);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), 6);
        request[6] = unit; request[7] = function;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8, 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10, 2), count);
        await stream.WriteAsync(request, ct);
        byte[] header = new byte[7];
        await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
        if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2)) != transaction ||
            BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2)) != 0 ||
            header[6] != unit || length is < 3 or > 254)
            throw new InvalidDataException("Modbus MBAP mismatch");
        byte[] response = new byte[length - 1];
        await stream.ReadExactlyAsync(response, ct);
        if (response[0] == (function | 0x80) && response.Length == 2)
            throw new InvalidDataException("Modbus exception " + response[1]);
        if (response[0] != function || response.Length != 2 + count * 2 || response[1] != count * 2)
            throw new InvalidDataException("Modbus response length/function mismatch");
        var registers = new ushort[count];
        for (int i = 0; i < count; i++) registers[i] = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2 + i * 2, 2));
        return registers;
    }
}

public sealed record GroupField(string DeviceId, string RawHex, string Text);

public static class IndustrialDiagnostics
{
    private static string Required(string[] args, string key)
    {
        var value = SimulatorRunner.Arg(args, key, "");
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Missing " + key);
        return value;
    }
    public static async Task<int> ReadCommandAsync(string[] args, CancellationToken token)
    {
        var result = await new ModbusTcpReadAdapter().ReadAsync(Required(args, "--host"),
            int.Parse(SimulatorRunner.Arg(args, "--port", "502")), byte.Parse(Required(args, "--unit")),
            byte.Parse(Required(args, "--function")), ushort.Parse(Required(args, "--address")),
            ushort.Parse(Required(args, "--count")), token);
        Console.WriteLine(JsonSerializer.Serialize(new { mode = "read-only-register-diagnostics", registers = result }));
        return 0;
    }

    // The operator supplies a verified ordered layout. This does not infer source identity
    // or split stored production events. Ambiguous or invalid input is rejected.
    public static IReadOnlyList<GroupField> PreviewGroup(byte[] frame, byte[] separator, string[] members)
    {
        if (frame.Length is < 1 or > 4096 || separator.Length is < 1 or > 16 ||
            members.Length is < 2 or > 16 || members.Any(string.IsNullOrWhiteSpace) ||
            members.Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Length)
            throw new ArgumentException("Invalid explicit group layout");
        var values = new List<byte[]>();
        int start = 0;
        for (int i = 0; i <= frame.Length - separator.Length;)
        {
            if (frame.AsSpan(i, separator.Length).SequenceEqual(separator))
            {
                values.Add(frame[start..i]); i += separator.Length; start = i;
            }
            else i++;
        }
        values.Add(frame[start..]);
        if (values.Count != members.Length || values.Any(v => v.Length == 0))
            throw new InvalidDataException("Group field count/identity ambiguous");
        var utf8 = new UTF8Encoding(false, true);
        return values.Select((v, i) => new GroupField(members[i], Convert.ToHexString(v), utf8.GetString(v))).ToList();
    }

    public static int GroupCommand(string[] args)
    {
        var frame = Convert.FromHexString(Required(args, "--frame"));
        var result = PreviewGroup(frame, Convert.FromHexString(Required(args, "--separator")),
            Required(args, "--members").Split(',', StringSplitOptions.TrimEntries));
        Console.WriteLine(JsonSerializer.Serialize(new { mode = "preview-only", raw = Convert.ToHexString(frame), fields = result }));
        return 0;
    }

    internal static async Task SelfTestAsync()
    {
        var parsed = PreviewGroup(Encoding.ASCII.GetBytes("PCB1|NOREAD"), new byte[] { 124 }, new[] { "UP", "DOWN" });
        if (parsed[0].DeviceId != "UP" || parsed[1].Text != "NOREAD")
            throw new InvalidOperationException("Group preview mapping failed");
        bool rejected = false;
        try { PreviewGroup(Encoding.ASCII.GetBytes("PCB1"), new byte[] { 124 }, new[] { "UP", "DOWN" }); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Ambiguous group accepted");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var mock = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(stop.Token);
            var stream = peer.GetStream();
            var request = new byte[12];
            await stream.ReadExactlyAsync(request, stop.Token);
            if (request[7] != 3 || request[9] != 10 || request[11] != 2)
                throw new InvalidDataException("Unexpected diagnostic request");
            byte[] response = { request[0], request[1], 0, 0, 0, 7, request[6], 3, 4, 0x12, 0x34, 0xab, 0xcd };
            await stream.WriteAsync(response.AsMemory(0, 5), stop.Token);
            await stream.WriteAsync(response.AsMemory(5), stop.Token);
        });
        var values = await new ModbusTcpReadAdapter().ReadAsync("127.0.0.1", port, 1, 3, 10, 2, stop.Token);
        await mock;
        if (!values.SequenceEqual(new ushort[] { 0x1234, 0xabcd })) throw new InvalidOperationException("Modbus read failed");
        Console.WriteLine("INDUSTRIAL DIAGNOSTICS PASS: explicit group preview and read-only Modbus TCP");
    }
}
