using System.Text.Json;

namespace HF811Collector.Desktop;

internal sealed class CollectorOptions
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

    public void Validate()
    {
        if (Mode is not ("connect" or "listen")) throw new ArgumentException("Mode 必須為 connect 或 listen");
        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("請輸入掃描器 IP / Host");
        if (Port is < 1 or > 65535 || ApiPort is < 1 or > 65535) throw new ArgumentException("Port 範圍為 1–65535");
        if (Mode == "listen" && !System.Net.IPAddress.TryParse(Host, out _))
            throw new ArgumentException("Listen 模式必須使用本機 IP 位址");
        if (Framing is not ("lf" or "crlf" or "cr" or "idle")) throw new ArgumentException("不支援的資料分隔符");
        if (IdleGapMs <= 0 || ReconnectMs <= 0 || DedupWindowMs < 0)
            throw new ArgumentException("逾時及去重設定必須為有效的正整數");
        if (string.IsNullOrWhiteSpace(DeviceId)) throw new ArgumentException("請輸入設備代號");
        if (string.IsNullOrWhiteSpace(NoReadValue)) throw new ArgumentException("請輸入 NOREAD 代表值");
        HF811Collector.ServerSyncSettings.Validate(SyncEnabled, CollectorId, ServerUrl,
            SyncIntervalSeconds, SyncBatchSize);
    }

    public HF811Collector.Settings ToCore() => new()
    {
        Mode = Mode,
        Host = Host,
        Port = Port,
        DeviceId = DeviceId,
        Framing = Framing,
        IdleGapMs = IdleGapMs,
        ReconnectMs = ReconnectMs,
        DedupWindowMs = DedupWindowMs,
        NoReadValue = NoReadValue,
        ApiPort = ApiPort,
        SyncEnabled = SyncEnabled,
        CollectorId = CollectorId,
        ServerUrl = ServerUrl,
        SyncIntervalSeconds = SyncIntervalSeconds,
        SyncBatchSize = SyncBatchSize,
        DatabasePath = string.IsNullOrWhiteSpace(DatabasePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HF811Collector", "data", "scans.sqlite3")
            : DatabasePath
    };
}

internal static class ConfigFile
{
    public static string PathName => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HF811Collector", "config.json");

    public static string EnsureDefault()
    {
        string path = PathName;
        if (File.Exists(path)) return path;
        string template = System.IO.Path.Combine(AppContext.BaseDirectory, "config.default.json");
        if (!File.Exists(template)) throw new FileNotFoundException("安裝目錄缺少 config.default.json", template);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(template, temporary);
            try { File.Move(temporary, path); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }

    public static CollectorOptions Load()
    {
        var value = JsonSerializer.Deserialize<CollectorOptions>(
            File.ReadAllText(EnsureDefault()),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("config.json 為空");
        value.Validate();
        return value;
    }

    public static void Save(CollectorOptions settings)
    {
        settings.Validate();
        string target = EnsureDefault();
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            File.WriteAllText(temporary, json, new System.Text.UTF8Encoding(false));
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
