using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HF811Collector;

public static class ServerSyncSettings
{
    public static void Validate(bool enabled, string collectorId, string serverUrl, int intervalSeconds, int batchSize)
    {
        if (intervalSeconds is < 1 or > 3600 || batchSize is < 1 or > 100)
            throw new ArgumentException("Sync interval must be 1..3600 seconds and batch size 1..100");
        if (string.IsNullOrWhiteSpace(collectorId) || collectorId.Length > 96 ||
            collectorId.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("CollectorId must be 1..96 letters, digits, dashes, dots or underscores");
        if (!enabled && string.IsNullOrWhiteSpace(serverUrl)) return;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Fragment) ||
            !string.IsNullOrEmpty(url.Query))
            throw new ArgumentException("Invalid ServerUrl; provide a full HTTPS batch endpoint");
        // HTTP is allowed ONLY for in-process / same-PC development testing.
        if (url.Scheme == Uri.UriSchemeHttp && url.Host is not ("127.0.0.1" or "localhost" or "[::1]" or "::1"))
            throw new ArgumentException("ServerUrl requires HTTPS except for localhost testing");
    }
}

/// <summary>DPAPI user-scope encrypted secret. The key is deliberately absent from config.json.</summary>
public static class SyncSecrets
{
    public static string SecretFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HF811Collector", "server-key.dpapi");

    public static void Save(string apiKey)
    {
        string normalized = apiKey.Trim();
        if (normalized.Length < 16 || normalized.Length > 512)
            throw new ArgumentException("API key must be 16..512 non-whitespace characters");
        var raw = Encoding.UTF8.GetBytes(normalized);
        string temp = SecretFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] encrypted = ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(SecretFile)!);
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, SecretFile, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static bool HasKey => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HF811_SYNC_API_KEY"))
        || File.Exists(SecretFile);

    public static string Read()
    {
        // Secret injection is useful for ephemeral CI and controlled deployment environments.
        string? env = Environment.GetEnvironmentVariable("HF811_SYNC_API_KEY");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        if (!File.Exists(SecretFile))
            throw new InvalidOperationException("Server API key has not been configured. Open Server Sync settings.");
        byte[] secret = ProtectedData.Unprotect(File.ReadAllBytes(SecretFile), null,
            DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(secret); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
}

public sealed record UploadEnvelope(
    [property: JsonPropertyName("collector_id")] string CollectorId,
    [property: JsonPropertyName("events")] IReadOnlyList<UploadEvent> Events);

/// <summary>
/// At-least-once sender with durable SQLite outbox and a server-receipt commit.
/// Never deletes scans, never changes event_id on retry, and never acknowledges on timeout.
/// </summary>
public sealed class SyncAgent : IDisposable
{
    private readonly Settings _cfg;
    private readonly ScanStore _store;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private int _failures;
    public string? LastError { get; private set; }
    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public DateTimeOffset? LastAttemptUtc { get; private set; }
    public bool Enabled => _cfg.SyncEnabled;

    public SyncAgent(Settings cfg, ScanStore store)
    {
        _cfg = cfg;
        _store = store;
        ServerSyncSettings.Validate(cfg.SyncEnabled, cfg.CollectorId, cfg.ServerUrl,
            cfg.SyncIntervalSeconds, cfg.SyncBatchSize);
    }

    public async Task RunAsync(CancellationToken token)
    {
        if (!Enabled) { await Task.Delay(Timeout.Infinite, token); return; }
        while (!token.IsCancellationRequested)
        {
            await SyncOnceAsync(token);
            int multiplier = 1 << Math.Min(6, _failures);
            int seconds = Math.Min(300, _cfg.SyncIntervalSeconds * multiplier);
            await Task.Delay(TimeSpan.FromSeconds(seconds), token);
        }
    }

    public async Task<int> SyncOnceAsync(CancellationToken token)
    {
        if (!Enabled) return 0;
        await _mutex.WaitAsync(token);
        try
        {
            var batch = _store.PendingBatch(_cfg.SyncBatchSize);
            if (batch.Count == 0) return 0;
            LastAttemptUtc = DateTimeOffset.UtcNow;
            try
            {
                string key = SyncSecrets.Read();
                if (key.Length < 16) throw new InvalidOperationException("Server API key is invalid");
                using var request = new HttpRequestMessage(HttpMethod.Post, _cfg.ServerUrl);
                request.Headers.Add("X-Collector-Key", key);
                request.Content = JsonContent.Create(new UploadEnvelope(_cfg.CollectorId, batch));
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Server returned HTTP {(int)response.StatusCode}; records remain pending");
                await using var body = await response.Content.ReadAsStreamAsync(token);
                var receipt = await JsonSerializer.DeserializeAsync<UploadReceipt>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, token);
                if (receipt is null) throw new InvalidDataException("Server returned empty receipt");
                _store.AcceptReceipt(batch, receipt);
                int accepted = receipt.AcknowledgedEventIds?.Count ?? 0;
                LastError = null;
                LastSuccessUtc = DateTimeOffset.UtcNow;
                _failures = 0;
                AppLog.WriteLine($"[Sync] ACK {accepted}/{batch.Count} scans, rejected {receipt.Rejected?.Count ?? 0}");
                return accepted;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _failures = Math.Min(6, _failures + 1);
                // Never log server URL credentials, API key, HTTP body, or barcode payload.
                LastError = ex is HttpRequestException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                _store.RecordUploadFailure(batch.Select(x => x.EventId), LastError);
                AppLog.WriteLine($"[Sync] Failed; {batch.Count} scans remain pending. {LastError}");
                return 0;
            }
        }
        finally { _mutex.Release(); }
    }

    public void Dispose()
    {
        _http.Dispose();
        _mutex.Dispose();
    }
}
