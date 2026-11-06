using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
string apiKey = Environment.GetEnvironmentVariable("HF811_SERVER_API_KEY") ?? "";
if (apiKey.Length < 16)
    throw new InvalidOperationException("Set HF811_SERVER_API_KEY (16+ characters) before starting receiver");
string dbPath = Environment.GetEnvironmentVariable("HF811_RECEIVER_DB")
    ?? Path.Combine(AppContext.BaseDirectory, "data", "receiver.sqlite3");
builder.Services.AddSingleton(new ReceiverStore(dbPath));
var app = builder.Build();
// Local-only development default. Configure ASPNETCORE_URLS and terminate HTTPS at a trusted
// reverse proxy for deployment; do not expose an unauthenticated HTTP receiver over LAN.
if (Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null
    && Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") is null
    && !args.Contains("--urls"))
    app.Urls.Add("http://127.0.0.1:8090");

bool IsAuthorized(HttpContext ctx)
{
    if (!ctx.Request.Headers.TryGetValue("X-Collector-Key", out var presented)) return false;
    byte[] known = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
    byte[] supplied = SHA256.HashData(Encoding.UTF8.GetBytes(presented.ToString()));
    return CryptographicOperations.FixedTimeEquals(known, supplied);
}

app.MapGet("/health", () => Results.Ok(new { status = "ok", role = "HF811Receiver" }));

app.MapGet("/api/v1/collector/stats", (HttpContext ctx, ReceiverStore store) =>
    IsAuthorized(ctx) ? Results.Ok(store.Stats()) : Results.Unauthorized());

app.MapPost("/api/v1/collector/scans/batch", async (
    HttpContext ctx, ReceiverStore store, CancellationToken ct) =>
{
    if (!IsAuthorized(ctx)) return Results.Unauthorized();
    ScanBatch? batch;
    try
    {
        batch = await ctx.Request.ReadFromJsonAsync<ScanBatch>(cancellationToken: ct);
    }
    catch (JsonException) { return Results.BadRequest(new { error = "Invalid JSON" }); }
    if (batch is null || string.IsNullOrWhiteSpace(batch.CollectorId) ||
        batch.CollectorId.Length > 96 ||
        batch.CollectorId.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_' or '.')) ||
        batch.Events is null || batch.Events.Count is < 1 or > 100 ||
        batch.Events.Any(e => e is null || !Guid.TryParse(e.EventId, out _)))
        return Results.BadRequest(new { error = "Invalid collector_id or event list (limit 100)" });

    var result = store.Accept(batch);
    return Results.Ok(result);
});

await app.RunAsync();

public sealed record ScanBatch(
    [property: JsonPropertyName("collector_id")] string CollectorId,
    [property: JsonPropertyName("events")] List<IncomingScan> Events);

public sealed record IncomingScan(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("barcode")] string Barcode,
    [property: JsonPropertyName("received_at_utc")] string ReceivedAtUtc,
    [property: JsonPropertyName("raw_hex")] string RawHex,
    [property: JsonPropertyName("source_address")] string SourceAddress,
    [property: JsonPropertyName("is_duplicate")] bool IsDuplicate,
    [property: JsonPropertyName("is_no_read")] bool IsNoRead);

public sealed record RejectedScan(
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record BatchReceipt(
    [property: JsonPropertyName("acknowledged_event_ids")] List<string> AcknowledgedEventIds,
    [property: JsonPropertyName("rejected")] List<RejectedScan> Rejected);

/// <summary>Single-node reference receiver. Raw events do NOT imply MES pass validation.</summary>
public sealed class ReceiverStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();

    public ReceiverStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path), Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS scan_events(
              event_id TEXT PRIMARY KEY,
              collector_id TEXT NOT NULL,
              device_id TEXT NOT NULL,
              barcode TEXT NOT NULL,
              received_at_utc TEXT NOT NULL,
              raw_hex TEXT NOT NULL,
              source_address TEXT NOT NULL,
              is_duplicate INTEGER NOT NULL,
              is_no_read INTEGER NOT NULL,
              ingested_at_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_receiver_collector_received
              ON scan_events(collector_id, received_at_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    public BatchReceipt Accept(ScanBatch batch)
    {
        var ack = new List<string>();
        var rejected = new List<RejectedScan>();
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var e in batch.Events)
            {
                string? error = Validate(e);
                if (error is not null)
                {
                    rejected.Add(new RejectedScan(e.EventId, error));
                    continue;
                }

                using var select = _db.CreateCommand();
                select.Transaction = tx;
                select.CommandText = """
                    SELECT collector_id,device_id,barcode,received_at_utc,raw_hex,source_address,is_duplicate,is_no_read
                    FROM scan_events WHERE event_id=$id
                    """;
                select.Parameters.AddWithValue("$id", e.EventId);
                using (var row = select.ExecuteReader())
                {
                    if (row.Read())
                    {
                        bool same = row.GetString(0) == batch.CollectorId &&
                            row.GetString(1) == e.DeviceId &&
                            row.GetString(2) == e.Barcode &&
                            row.GetString(3) == e.ReceivedAtUtc &&
                            row.GetString(4) == e.RawHex &&
                            row.GetString(5) == e.SourceAddress &&
                            (row.GetInt64(6) != 0) == e.IsDuplicate &&
                            (row.GetInt64(7) != 0) == e.IsNoRead;
                        if (same) ack.Add(e.EventId);
                        else rejected.Add(new RejectedScan(e.EventId, "event_id already exists with different content"));
                        continue;
                    }
                }

                using var insert = _db.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT INTO scan_events(event_id,collector_id,device_id,barcode,received_at_utc,raw_hex,
                        source_address,is_duplicate,is_no_read,ingested_at_utc)
                    VALUES ($id,$collector,$device,$barcode,$received,$hex,$source,$duplicate,$no_read,$ingested)
                    """;
                insert.Parameters.AddWithValue("$id", e.EventId);
                insert.Parameters.AddWithValue("$collector", batch.CollectorId);
                insert.Parameters.AddWithValue("$device", e.DeviceId);
                insert.Parameters.AddWithValue("$barcode", e.Barcode);
                insert.Parameters.AddWithValue("$received", e.ReceivedAtUtc);
                insert.Parameters.AddWithValue("$hex", e.RawHex);
                insert.Parameters.AddWithValue("$source", e.SourceAddress);
                insert.Parameters.AddWithValue("$duplicate", e.IsDuplicate ? 1 : 0);
                insert.Parameters.AddWithValue("$no_read", e.IsNoRead ? 1 : 0);
                insert.Parameters.AddWithValue("$ingested", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                insert.ExecuteNonQuery();
                ack.Add(e.EventId);
            }
            tx.Commit(); // The receipt is sent only AFTER durable DB commit.
        }
        return new BatchReceipt(ack, rejected);
    }

    private static string? Validate(IncomingScan e)
    {
        if (string.IsNullOrWhiteSpace(e.DeviceId) || e.DeviceId.Length > 128)
            return "Invalid device_id";
        if (e.Barcode is null || e.Barcode.Length > 4096)
            return "Invalid barcode length";
        if (string.IsNullOrWhiteSpace(e.ReceivedAtUtc) || e.ReceivedAtUtc.Length > 64 ||
            !DateTimeOffset.TryParse(e.ReceivedAtUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out _))
            return "Invalid received_at_utc";
        if (e.RawHex is null || e.RawHex.Length > 8192 ||
            (e.RawHex.Length % 2 != 0) || !e.RawHex.All(Uri.IsHexDigit))
            return "Invalid raw_hex";
        if (e.SourceAddress is null || e.SourceAddress.Length > 256)
            return "Invalid source_address";
        return null;
    }

    public object Stats()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*), COUNT(DISTINCT collector_id) FROM scan_events
                """;
            using var row = cmd.ExecuteReader();
            row.Read();
            return new { total = row.GetInt64(0), collectors = row.GetInt64(1) };
        }
    }

    public void Dispose() => _db.Dispose();
}
