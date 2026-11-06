using System.Drawing;
using System.Windows.Forms;
using HF811Collector;

namespace HF811Collector.Desktop;

internal sealed partial class MainForm
{
    private readonly CheckBox _syncEnabled = new()
    {
        Text = "啟用背景批次同步", Checked = false, Dock = DockStyle.Fill,
        ForeColor = Color.FromArgb(37, 60, 87)
    };
    private readonly TextBox _collectorId = new() { Dock = DockStyle.Fill };
    private readonly TextBox _serverUrl = new() { Dock = DockStyle.Fill };
    private readonly TextBox _serverKey = new()
    {
        Dock = DockStyle.Fill, UseSystemPasswordChar = true,
        PlaceholderText = "留空代表保留已儲存的 API 金鑰"
    };
    private readonly NumericUpDown _syncInterval = new WheelSafeNumericUpDown
    {
        Minimum = 1, Maximum = 3600, Value = 5, Dock = DockStyle.Fill
    };
    private readonly NumericUpDown _batchSize = new WheelSafeNumericUpDown
    {
        Minimum = 1, Maximum = 100, Value = 50, Dock = DockStyle.Fill
    };
    private readonly Label _serverKeyState = new()
    {
        Dock = DockStyle.Fill, ForeColor = Color.FromArgb(101, 120, 142),
        Text = "未設定憑證"
    };
    private readonly Label _syncPending = new()
    {
        Dock = DockStyle.Fill, Text = "—", Font = new Font("Segoe UI", 22, FontStyle.Bold)
    };
    private readonly Label _syncSynced = new()
    {
        Dock = DockStyle.Fill, Text = "—", Font = new Font("Segoe UI", 22, FontStyle.Bold)
    };
    private readonly Label _syncRejected = new()
    {
        Dock = DockStyle.Fill, Text = "—", Font = new Font("Segoe UI", 22, FontStyle.Bold)
    };
    private readonly Label _syncStatusText = new()
    {
        Dock = DockStyle.Fill, Text = "Collector 尚未啟動",
        AutoEllipsis = true, ForeColor = Color.FromArgb(103, 123, 144)
    };
    private readonly Label _syncLastSuccess = new()
    {
        Dock = DockStyle.Fill, Text = "最近成功同步：—",
        ForeColor = Color.FromArgb(103, 123, 144)
    };
    private readonly Label _syncErrorText = new()
    {
        Dock = DockStyle.Fill, Text = "", ForeColor = Color.FromArgb(173, 86, 48),
        AutoEllipsis = true
    };
    private readonly Button _syncNow = new() { Text = "立即同步", Enabled = false };
    private readonly Button _syncTab = NavButton("伺服器同步");
    private Panel? _syncPage;

    private Panel BuildSyncPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, AutoScroll = true };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 500, ColumnCount = 2, RowCount = 2
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 442));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        var settings = SettingsCard("伺服器連線設定", "HTTPS 批次上傳 · 失敗自動補傳");
        settings.Margin = new Padding(0, 0, 12, 7);
        var controls = SettingsGrid();
        AddModernField(controls, "自動上傳", _syncEnabled);
        AddModernField(controls, "Collector ID", _collectorId);
        AddModernField(controls, "接收端 API URL", _serverUrl);
        AddModernField(controls, "API Key（DPAPI）", _serverKey);
        AddModernField(controls, "憑證狀態", _serverKeyState);
        AddModernField(controls, "同步間隔（秒）", _syncInterval);
        AddModernField(controls, "每批最大筆數", _batchSize);
        settings.Controls.Add(controls, 0, 1);
        root.Controls.Add(settings, 0, 0);

        var status = SettingsCard("同步狀態", "僅統計伺服器已確認的紀錄");
        status.Margin = new Padding(12, 0, 0, 7);
        var stateContent = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6 };
        stateContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        stateContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
        stateContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        stateContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        stateContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        stateContent.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var counters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        for (int i = 0; i < 3; i++) counters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        counters.Controls.Add(SyncCounter("待上傳", _syncPending, Color.FromArgb(181, 110, 31)), 0, 0);
        counters.Controls.Add(SyncCounter("已同步", _syncSynced, Color.FromArgb(24, 145, 105)), 1, 0);
        counters.Controls.Add(SyncCounter("遭拒收", _syncRejected, Color.FromArgb(178, 71, 67)), 2, 0);
        stateContent.Controls.Add(counters, 0, 0);
        _syncStatusText.Font = new Font("Microsoft JhengHei UI", 10.5f, FontStyle.Bold);
        stateContent.Controls.Add(_syncStatusText, 0, 1);
        stateContent.Controls.Add(_syncLastSuccess, 0, 2);
        stateContent.Controls.Add(_syncErrorText, 0, 3);
        var controlsRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        controlsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        controlsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        _syncNow.Dock = DockStyle.Fill;
        _syncNow.BackColor = BlueModern;
        _syncNow.ForeColor = Color.White;
        _syncNow.FlatStyle = FlatStyle.Flat;
        _syncNow.FlatAppearance.BorderSize = 0;
        _syncNow.Click += async (_, _) => await ManualSyncAsync();
        controlsRow.Controls.Add(_syncNow, 0, 0);
        var health = MButton("測試伺服器連線", BlueModern, true);
        health.Click += async (_, _) => await TestReceiverAsync();
        controlsRow.Controls.Add(health, 1, 0);
        stateContent.Controls.Add(controlsRow, 0, 4);
        status.Controls.Add(stateContent, 0, 1);
        root.Controls.Add(status, 1, 0);

        var bottom = MLabel("離線時先寫入本機 SQLite；只有伺服器回傳 ACK 後才標記已同步。重複／NOREAD 也會保留為原始事件。", 9, false, MutedModern);
        root.SetColumnSpan(bottom, 2);
        root.Controls.Add(bottom, 0, 1);
        page.Controls.Add(root);
        _syncEnabled.CheckedChanged += (_, _) => UpdateButtons();
        return page;
    }

    private static Panel SyncCounter(string caption, Label value, Color ink)
    {
        var p = MCard();
        p.Margin = new Padding(0, 0, 6, 5);
        p.Padding = new Padding(8, 9, 8, 9);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        value.TextAlign = ContentAlignment.MiddleCenter;
        value.ForeColor = ink;
        grid.Controls.Add(value, 0, 0);
        var t = MLabel(caption, 9, false, MutedModern);
        t.TextAlign = ContentAlignment.MiddleCenter;
        grid.Controls.Add(t, 0, 1);
        p.Controls.Add(grid);
        return p;
    }

    private void ApplySyncSettings(CollectorOptions cfg)
    {
        _syncEnabled.Checked = cfg.SyncEnabled;
        _collectorId.Text = cfg.CollectorId;
        _serverUrl.Text = cfg.ServerUrl;
        _syncInterval.Value = Math.Clamp(cfg.SyncIntervalSeconds, 1, 3600);
        _batchSize.Value = Math.Clamp(cfg.SyncBatchSize, 1, 100);
        _serverKey.Clear(); // Never expose persisted secrets as plain text in the UI.
        RefreshSecretStatus();
    }

    private void RefreshSecretStatus()
    {
        _serverKeyState.Text = SyncSecrets.HasKey ? "已設定 · Windows DPAPI 加密" : "尚未設定 API Key";
    }

    private void RefreshSyncDisplay()
    {
        if (_store is null) return;
        var state = _store.GetSyncState();
        _syncPending.Text = state.Pending.ToString("N0");
        _syncSynced.Text = state.Synced.ToString("N0");
        _syncRejected.Text = state.Rejected.ToString("N0");
        _syncLastSuccess.Text = "最近成功同步：" +
            (state.LastSyncedAtUtc is null ? "—" : state.LastSyncedAtUtc);
        if (_syncAgent is null || !_syncAgent.Enabled)
        {
            _syncStatusText.Text = "背景同步未啟用";
            _syncErrorText.Text = "";
        }
        else if (!string.IsNullOrEmpty(_syncAgent.LastError))
        {
            _syncStatusText.Text = "同步遇到錯誤，待上傳資料保留";
            _syncErrorText.Text = _syncAgent.LastError;
        }
        else
        {
            _syncStatusText.Text = "背景同步已啟用";
            _syncErrorText.Text = "";
        }
    }

    private async Task ManualSyncAsync()
    {
        if (_syncAgent is null || _collectorCts is null) return;
        _syncNow.Enabled = false;
        try
        {
            int count = await _syncAgent.SyncOnceAsync(_collectorCts.Token);
            RefreshSyncDisplay();
            AppendLog($"手動同步完成：伺服器確認 {count} 筆。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ReportError("手動同步失敗", ex); }
        finally { UpdateButtons(); }
    }

    private async Task TestReceiverAsync()
    {
        try
        {
            var value = ReadForm();
            ServerSyncSettings.Validate(true, value.CollectorId, value.ServerUrl,
                value.SyncIntervalSeconds, value.SyncBatchSize);
            var endpoint = new Uri(value.ServerUrl);
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(6) };
            using var response = await http.GetAsync(new Uri(endpoint.GetLeftPart(UriPartial.Authority) + "/health"));
            MessageBox.Show(this, response.IsSuccessStatusCode
                ? "伺服器健康檢查連線成功。\n注意：此測試不代表 API Key 或批次接收權限已驗證。"
                : $"伺服器回覆 HTTP {(int)response.StatusCode}",
                "Server Sync · 連線測試", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { ReportError("無法連到伺服器", ex); }
    }
}
