namespace HF811Collector;

public static class TriggerCommand
{
    public static byte[] Parse(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
        var value = hex.Replace(" ", "").Trim();
        if (value.Length > 256 || value.Length % 2 != 0) throw new ArgumentException("Trigger HEX must contain 1..128 bytes");
        return Convert.FromHexString(value);
    }
}

public sealed partial class ScanStore
{
    private sealed record TriggerEndpoint(Stream Stream, byte[] On, byte[] Off, CancellationToken Lifetime)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
    }
    private readonly Dictionary<string, TriggerEndpoint> _triggers = new(StringComparer.Ordinal);

    internal IDisposable RegisterTrigger(string id, Stream stream, string on, string off, CancellationToken token)
    {
        var endpoint = new TriggerEndpoint(stream, TriggerCommand.Parse(on), TriggerCommand.Parse(off), token);
        if (endpoint.On.Length != 0 || endpoint.Off.Length != 0)
            lock (_gate) _triggers[id] = endpoint;
        return new TriggerLease(() => {
            lock (_gate)
                if (_triggers.TryGetValue(id, out var current) && ReferenceEquals(current, endpoint))
                    _triggers.Remove(id);
        });
    }
    private sealed class TriggerLease(Action close) : IDisposable { public void Dispose() => close(); }

    public bool CanTrigger(string id, bool on)
    {
        lock (_gate)
            return _triggers.TryGetValue(id, out var endpoint) && !endpoint.Lifetime.IsCancellationRequested &&
                   (on ? endpoint.On : endpoint.Off).Length > 0;
    }

    public async Task TriggerAsync(string id, bool on, CancellationToken token)
    {
        TriggerEndpoint endpoint;
        lock (_gate)
        {
            if (!_triggers.TryGetValue(id, out var current)) throw new InvalidOperationException("No active configured trigger channel");
            endpoint = current;
        }
        byte[] bytes = on ? endpoint.On : endpoint.Off;
        if (bytes.Length == 0) throw new InvalidOperationException("Trigger command not configured");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, endpoint.Lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        await endpoint.Gate.WaitAsync(timeout.Token);
        try
        {
            lock (_gate)
                if (!_triggers.TryGetValue(id, out var current) || !ReferenceEquals(current, endpoint))
                    throw new InvalidOperationException("Scanner connection changed; do not automatically retry trigger");
            await endpoint.Stream.WriteAsync(bytes, timeout.Token);
        }
        finally { endpoint.Gate.Release(); }
    }
}
