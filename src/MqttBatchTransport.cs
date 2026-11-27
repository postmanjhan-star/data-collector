using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace HF811Collector;

// Shadow/test only. Keep the existing SQLite receipt state machine; MQTT PUBACK
// is NOT the application receipt. Broker ACL must bind the collector's identity.
public static class MqttBatchTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool IsMqtt(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "mqtt" or "mqtts";

    public static void Validate(string url, string collectorId)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("mqtt" or "mqtts") || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath is not ("" or "/") ||
            uri.Port == 0 || !Regex.IsMatch(collectorId, @"\A[A-Za-z0-9_.-]{1,96}\z"))
            throw new ArgumentException("Use mqtts://host:8883 (test only); collector ID must be ASCII");
        if (uri.Scheme == "mqtt" && !uri.IsLoopback)
            throw new ArgumentException("Plain MQTT is permitted only on loopback");
    }

    private static MqttClientOptions Options(string url, string id, string key, bool probe)
    {
        Validate(url, id);
        var uri = new Uri(url);
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(probe ? "hf811-probe-" + Guid.NewGuid().ToString("N") : "hf811-shadow-" + id)
            .WithTcpServer(uri.Host, uri.Port > 0 ? uri.Port : uri.Scheme == "mqtts" ? 8883 : 1883)
            .WithCredentials(id, key).WithCleanSession();
        if (uri.Scheme == "mqtts") builder.WithTlsOptions(o => o.UseTls());
        return builder.Build();
    }

    private static string Root(string id) => $"smt/test/v1/collectors/{id}";
    private static async Task SubscribeAsync(IMqttClient client, MqttFactory factory, string topic, CancellationToken token)
    {
        var response = await client.SubscribeAsync(factory.CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(topic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build(), token);
        if (response.Items.Count != 1 || response.Items.Any(i => (int)i.ResultCode != 1))
            throw new InvalidOperationException("Broker did not grant QoS 1 ACK subscription");
    }

    public static async Task ProbeAsync(string url, string id, string key, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();
        await client.ConnectAsync(Options(url, id, key, true), deadline.Token);
        await SubscribeAsync(client, factory, Root(id) + "/ack", deadline.Token);
    }

    private sealed record Ack(
        [property: JsonPropertyName("collector_id")] string CollectorId,
        [property: JsonPropertyName("payload_sha256")] string PayloadSha256,
        [property: JsonPropertyName("receipt")] UploadReceipt Receipt);

    // Legacy one-shot callers remain compatible; SyncAgent owns a reusable connection.
    public static async Task<UploadReceipt> SendAsync(string url, string key, UploadEnvelope envelope, CancellationToken token)
    {
        using var session = new Session(url, envelope.CollectorId);
        return await session.SendAsync(key, envelope, token);
    }

    public sealed class Session : IDisposable
    {
        private readonly string _url, _id, _root;
        private readonly MqttFactory _factory = new();
        private readonly SemaphoreSlim _serial = new(1, 1);
        private readonly object _gate = new();
        private IMqttClient? _client;
        private string? _key;
        private string? _expectedHash;
        private TaskCompletionSource<UploadReceipt>? _pending;
        private bool _subscribed;
        public int ConnectionCount { get; private set; }
        public bool IsConnected => _client?.IsConnected == true && _subscribed;

        public Session(string url, string id)
        {
            Validate(url, id);
            _url = url; _id = id; _root = Root(id);
        }

        private Task OnMessage(MqttApplicationMessageReceivedEventArgs e)
        {
            if (e.ApplicationMessage.Topic != _root + "/ack" || e.ApplicationMessage.Retain ||
                e.ApplicationMessage.QualityOfServiceLevel != MqttQualityOfServiceLevel.AtLeastOnce ||
                e.ApplicationMessage.PayloadSegment.Count > 1024 * 1024) return Task.CompletedTask;
            try
            {
                var ack = JsonSerializer.Deserialize<Ack>(e.ApplicationMessage.PayloadSegment.ToArray(), Json);
                lock (_gate)
                    if (ack?.CollectorId == _id && ack.PayloadSha256 == _expectedHash && ack.Receipt is not null)
                        _pending?.TrySetResult(ack.Receipt);
            }
            catch (JsonException) { }
            return Task.CompletedTask;
        }

        private void Reset()
        {
            var old = _client;
            _client = null;
            _subscribed = false;
            _key = null;
            if (old is null) return;
            old.ApplicationMessageReceivedAsync -= OnMessage;
            // Bound shutdown; never block indefinitely waiting for a dead network.
            try
            {
                if (old.IsConnected)
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    old.DisconnectAsync(new MqttClientDisconnectOptions(), stop.Token).GetAwaiter().GetResult();
                }
            }
            catch { /* network may already be gone */ }
            old.Dispose();
        }

        private async Task<IMqttClient> EnsureAsync(string key, CancellationToken ct)
        {
            if (IsConnected && _key == key) return _client!;
            Reset();
            var client = _factory.CreateMqttClient();
            _client = client;
            client.ApplicationMessageReceivedAsync += OnMessage;
            client.DisconnectedAsync += _ =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_client, client))
                    {
                        _subscribed = false;
                        _pending?.TrySetException(new IOException("MQTT disconnected before receipt"));
                    }
                }
                return Task.CompletedTask;
            };
            await client.ConnectAsync(Options(_url, _id, key, false), ct);
            await SubscribeAsync(client, _factory, _root + "/ack", ct);
            _key = key;
            _subscribed = true;
            ConnectionCount++;
            return client;
        }

        public async Task<UploadReceipt> SendAsync(string key, UploadEnvelope envelope, CancellationToken token)
        {
            if (envelope.CollectorId != _id) throw new ArgumentException("Collector identity changed");
            await _serial.WaitAsync(token);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                var ct = deadline.Token;
                var client = await EnsureAsync(key, ct);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
                var completion = new TaskCompletionSource<UploadReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate)
                {
                    _expectedHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    _pending = completion;
                }
                var result = await client.PublishAsync(new MqttApplicationMessageBuilder()
                    .WithTopic(_root + "/scans").WithPayload(bytes)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(false).Build(), ct);
                if (!result.IsSuccess) throw new InvalidOperationException("MQTT publish failed");
                return await completion.Task.WaitAsync(ct);
            }
            catch { Reset(); throw; }
            finally
            {
                lock (_gate) { _expectedHash = null; _pending = null; }
                _serial.Release();
            }
        }

        public void Dispose() { Reset(); _serial.Dispose(); }
    }
}
