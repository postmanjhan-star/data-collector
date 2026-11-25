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

    public static async Task<UploadReceipt> SendAsync(string url, string key, UploadEnvelope envelope, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = deadline.Token;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var root = Root(envelope.CollectorId);
        var factory = new MqttFactory();
        using var client = factory.CreateMqttClient();
        var completion = new TaskCompletionSource<UploadReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += e =>
        {
            if (e.ApplicationMessage.Topic != root + "/ack" || e.ApplicationMessage.Retain ||
                e.ApplicationMessage.PayloadSegment.Count > 1024 * 1024) return Task.CompletedTask;
            try
            {
                var ack = JsonSerializer.Deserialize<Ack>(e.ApplicationMessage.PayloadSegment.ToArray(), Json);
                if (ack?.CollectorId == envelope.CollectorId && ack.PayloadSha256 == sha && ack.Receipt is not null)
                    completion.TrySetResult(ack.Receipt);
            }
            catch (JsonException) { }
            return Task.CompletedTask;
        };
        await client.ConnectAsync(Options(url, envelope.CollectorId, key, false), ct);
        await SubscribeAsync(client, factory, root + "/ack", ct);
        var result = await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(root + "/scans").WithPayload(bytes)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).WithRetainFlag(false).Build(), ct);
        if (!result.IsSuccess) throw new InvalidOperationException("MQTT publish failed");
        return await completion.Task.WaitAsync(ct);
    }
}
