using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using HF811Collector;

namespace HF811Collector.Desktop;

internal sealed partial class MainForm : Form
{
    private static readonly Color BackgroundColor = Color.FromArgb(244, 247, 251);
    private static readonly Color Ink = Color.FromArgb(31, 48, 72);
    private static readonly Color Muted = Color.FromArgb(103, 122, 143);
    private static readonly Color Accent = Color.FromArgb(39, 112, 230);
    private static readonly Color Success = Color.FromArgb(23, 147, 115);
    private static readonly Color Warning = Color.FromArgb(179, 107, 22);

    private readonly TextBox _host = TextInput();
    private readonly TextBox _device = TextInput();
    private readonly TextBox _noRead = TextInput();
    private readonly TextBox _dbPath = TextInput();
    private readonly ComboBox _mode = Choice("connect", "listen");
    private readonly ComboBox _framing = Choice("lf", "crlf", "cr", "idle");
    private readonly NumericUpDown _port = Number(65535, 1);
    private readonly NumericUpDown _apiPort = Number(65535, 1);
    private readonly NumericUpDown _dedup = Number(600000);
    private readonly NumericUpDown _reconnect = Number(600000, 1);
    private readonly NumericUpDown _idleGap = Number(600000, 1);
    private readonly Button _start = ActionButton("▶  啟動 Collector", Accent);
    private readonly Button _stop = ActionButton("■  停止", Color.FromArgb(100, 116, 139));
    private readonly Button _simulate = ActionButton("◉  開始模擬掃描", Color.FromArgb(19, 137, 139));
    private readonly Label _status = TextLabel("● 未啟動", 11, true, Muted);
    private readonly Label _lastCode = TextLabel("尚無掃描紀錄", 11, true, Ink);
    private readonly Label _total = TextLabel("0", 22, true, Ink);
    private readonly Label _accepted = TextLabel("0", 22, true, Success);
    private readonly Label _duplicates = TextLabel("0", 22, true, Ink);
    private readonly Label _noReads = TextLabel("0", 22, true, Ink);
    private readonly TextBox _logs = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(248, 250, 253),
        ForeColor = Ink,
        Font = new Font("Consolas", 9F),
        Dock = DockStyle.Fill,
        TabStop = false
    };

    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly bool _startHidden;

    private CancellationTokenSource? _collectorCts;
    private Task? _collectorTask;
    private ScanStore? _store;
    private Mutex? _collectorMutex;
    private CancellationTokenSource? _simulatorCts;
    private Task? _simulatorTask;
    private bool _stopping;
    private bool _closingForReal;
    private bool _hintShown;
    private int _apiRunningPort = 8088;

    public MainForm(bool startHidden)
    {
        _startHidden = startHidden;
        Text = "HF811 Collector · 產線條碼接收";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(830, 660);
        Size = new Size(1030, 750);
        BackColor = BackgroundColor;
        Font = new Font("Microsoft JhengHei UI", 9.5F);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        BuildLayout();
        TryLoadSettings();

        var menu = new ContextMenuStrip();
        menu.Items.Add("顯示主視窗", null, (_, _) => RestoreWindow());
        menu.Items.Add("啟動 Collector", null, (_, _) => StartCollector());
        menu.Items.Add("停止 Collector", null, async (_, _) => await StopCollectorAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("開啟資料與日誌", null, (_, _) => OpenDataFolder());
        menu.Items.Add("檢視狀態 API", null, (_, _) => OpenStats());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("結束程式", null, async (_, _) => await ExitApplicationAsync());

        _tray = new NotifyIcon
        {
            Icon = Icon,
            Visible = true,
            Text = "HF811 Collector · 未啟動",
            ContextMenuStrip = menu
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) RestoreWindow(); };
        _tray.DoubleClick += (_, _) => RestoreWindow();

        AppLog.MessageWritten += OnDiagnostic;
        AppLog.Initialize(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HF811Collector", "logs"));

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshCounters();
        _timer.Start();
        UpdateButtons();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_startHidden)
        {
            BeginInvoke((Action)(() =>
            {
                Hide();
                StartCollector();
            }));
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closingForReal)
        {
            e.Cancel = true;
            Hide();
            if (!_hintShown)
            {
                _tray.ShowBalloonTip(2300, "HF811 Collector",
                    "主視窗已隱藏。Collector 仍在背景運作，點選右下角圖示可重新開啟。",
                    ToolTipIcon.Info);
                _hintShown = true;
            }
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        AppLog.MessageWritten -= OnDiagnostic;
        _timer.Stop();
        _timer.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    private void BuildLayout() => BuildModernLayout();

    private static Panel Card() => new()
    {
        Dock = DockStyle.Fill, BackColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        Padding = new Padding(18, 14, 18, 14)
    };

    private static Label TextLabel(string value, float size, bool bold, Color color) => new()
    {
        Text = value, ForeColor = color,
        Font = new Font("Microsoft JhengHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        AutoSize = true, Margin = new Padding(0, 4, 0, 4)
    };

    private static TextBox TextInput() => new()
    {
        BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White,
        Font = new Font("Segoe UI", 10F), Margin = new Padding(3, 5, 3, 5)
    };

    private static ComboBox Choice(params string[] items)
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Font = new Font("Segoe UI", 10F) };
        combo.Items.AddRange(items);
        return combo;
    }

    private static NumericUpDown Number(int maximum, int minimum = 0) => new WheelSafeNumericUpDown()
    {
        Minimum = minimum, Maximum = maximum, ThousandsSeparator = false,
        Font = new Font("Segoe UI", 10F), TextAlign = HorizontalAlignment.Right
    };

    private static Button ActionButton(string label, Color color, EventHandler? click = null)
    {
        var button = new Button
        {
            Text = label, BackColor = color, ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 7, 10, 7), Margin = new Padding(0, 0, 8, 0),
            Cursor = Cursors.Hand, Font = new Font("Microsoft JhengHei UI", 9.5F, FontStyle.Bold)
        };
        button.FlatAppearance.BorderSize = 0;
        if (click != null) button.Click += click;
        return button;
    }

    private static Panel Counter(string caption, Label value)
    {
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(248, 250, 253), Margin = new Padding(2, 0, 5, 9) };
        var stack = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        value.Dock = DockStyle.Fill;
        value.TextAlign = ContentAlignment.BottomCenter;
        stack.Controls.Add(value, 0, 0);
        var text = TextLabel(caption, 9, false, Muted);
        text.Dock = DockStyle.Fill;
        text.TextAlign = ContentAlignment.TopCenter;
        stack.Controls.Add(text, 0, 1);
        card.Controls.Add(stack);
        return card;
    }

    private static void AddField(TableLayoutPanel layout, string caption, Control field)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        var label = TextLabel(caption, 9, false, Ink);
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(label, 0, row);
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(3, 6, 2, 6);
        layout.Controls.Add(field, 1, row);
    }

    private void TryLoadSettings()
    {
        try { Apply(ConfigFile.Load()); }
        catch (Exception ex)
        {
            Apply(new CollectorOptions());
            _logs.AppendText("無法讀取既有設定：" + ex.Message + Environment.NewLine +
                "已在介面載入預設參數，請確認後儲存。" + Environment.NewLine);
        }
    }

    private void Apply(CollectorOptions cfg)
    {
        _mode.SelectedItem = cfg.Mode;
        _framing.SelectedItem = cfg.Framing;
        _host.Text = cfg.Host;
        _device.Text = cfg.DeviceId;
        _noRead.Text = cfg.NoReadValue;
        _dbPath.Text = cfg.DatabasePath;
        _port.Value = Math.Clamp(cfg.Port, 1, 65535);
        _apiPort.Value = Math.Clamp(cfg.ApiPort, 1, 65535);
        _dedup.Value = Math.Clamp(cfg.DedupWindowMs, 0, 600000);
        _reconnect.Value = Math.Clamp(cfg.ReconnectMs, 1, 600000);
        _idleGap.Value = Math.Clamp(cfg.IdleGapMs, 1, 600000);
        UpdateConnectionLine();
    }

    private CollectorOptions ReadForm() => new()
    {
        Mode = _mode.SelectedItem?.ToString() ?? "connect",
        Host = _host.Text.Trim(),
        Port = (int)_port.Value,
        DeviceId = _device.Text.Trim(),
        Framing = _framing.SelectedItem?.ToString() ?? "lf",
        IdleGapMs = (int)_idleGap.Value,
        ReconnectMs = (int)_reconnect.Value,
        DedupWindowMs = (int)_dedup.Value,
        NoReadValue = _noRead.Text,
        ApiPort = (int)_apiPort.Value,
        DatabasePath = _dbPath.Text.Trim()
    };

    private bool SaveSettings(bool notify = true)
    {
        try
        {
            ConfigFile.Save(ReadForm());
            if (notify)
                MessageBox.Show(_collectorCts is null
                    ? "已儲存設定。"
                    : "設定已儲存；正在執行的 Collector 需停止後重新啟動才會生效。",
                    "HF811 Collector", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        catch (Exception ex)
        {
            ReportError("無法儲存設定", ex);
            return false;
        }
    }

    private void StartCollector()
    {
        if (_collectorCts != null || _stopping) return;
        if (!SaveSettings(notify: false)) return;

        try
        {
            var cfg = ConfigFile.Load().ToCore();
            _collectorMutex = new Mutex(false, "HF811Collector-Collector", out bool first);
            if (!first)
            {
                _collectorMutex.Dispose();
                _collectorMutex = null;
                MessageBox.Show("另一個 Collector 已在此 Windows 工作階段執行。\n請先關閉舊版 Console Collector。",
                    "已在執行", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _store = new ScanStore(cfg.DatabasePath, cfg.DedupWindowMs, cfg.NoReadValue);
            _apiRunningPort = cfg.ApiPort;
            _collectorCts = new CancellationTokenSource();
            var task = RunWorkersAsync(cfg, _store, _collectorCts);
            _collectorTask = task;
            SetStatus("正在連線", Warning);
            AppendLog("Collector 已啟動，正在等待掃描器資料。");
            UpdateButtons();
            _ = WatchCollectorAsync(task);
        }
        catch (Exception ex)
        {
            _collectorCts?.Dispose();
            _collectorCts = null;
            _store?.Dispose();
            _store = null;
            _collectorMutex?.Dispose();
            _collectorMutex = null;
            ReportError("無法啟動 Collector", ex);
            SetStatus("啟動失敗", Warning);
            UpdateButtons();
        }
    }

    private static async Task RunWorkersAsync(Settings cfg, ScanStore store, CancellationTokenSource cts)
    {
        Task api = LocalApi.RunAsync(store, cfg.ApiPort, cts.Token);
        Task receiver = CollectorRunner.RunAsync(cfg, store, cts.Token);
        try
        {
            var finished = await Task.WhenAny(api, receiver);
            if (!cts.IsCancellationRequested)
            {
                await finished;
                throw new InvalidOperationException("Collector 工作程序意外結束");
            }
        }
        finally
        {
            cts.Cancel();
            try { await Task.WhenAll(api, receiver); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        }
    }

    private async Task WatchCollectorAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SafeUi(() => ReportError("Collector 工作程序失敗", ex)); }
        SafeUi(() => { _ = StopCollectorAsync(); });
    }

    private async Task StopCollectorAsync()
    {
        if (_stopping || _collectorCts is null) return;
        _stopping = true;
        UpdateButtons();
        var cts = _collectorCts;
        cts.Cancel();
        try { if (_collectorTask != null) await _collectorTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppendLog("停止期間錯誤：" + ex.Message); }
        finally
        {
            _collectorTask = null;
            _store?.Dispose();
            _store = null;
            _collectorMutex?.Dispose();
            _collectorMutex = null;
            _collectorCts = null;
            cts.Dispose();
            _stopping = false;
            SetStatus("已停止", Muted);
            UpdateButtons();
        }
    }

    private async Task ToggleSimulatorAsync()
    {
        if (_simulatorCts != null) { await StopSimulatorAsync(); return; }
        if (_host.Text.Trim() != "127.0.0.1" || _mode.SelectedItem?.ToString() != "connect")
        {
            MessageBox.Show("本機模擬掃描器使用 127.0.0.1。\n請先將連線模式設為 connect、Host 設為 127.0.0.1，再儲存設定。",
                "模擬模式設定", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _simulatorCts = new CancellationTokenSource();
        string[] args =
        {
            "--mode", "server", "--host", "127.0.0.1",
            "--port", ((int)_port.Value).ToString(),
            "--interval-ms", "200", "--repeat-every", "5",
            "--no-read-every", "10", "--delimiter", _framing.SelectedItem?.ToString() is "cr" or "crlf" or "lf"
                ? _framing.SelectedItem!.ToString()! : "lf"
        };
        var task = SimulatorRunner.RunAsync(args, _simulatorCts.Token);
        _simulatorTask = task;
        AppendLog("模擬器已啟動：持續送出 PCB 條碼，每 5 筆含重複，每 10 筆含 NOREAD。");
        UpdateButtons();
        _ = WatchSimulatorAsync(task);
    }

    private async Task WatchSimulatorAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SafeUi(() => ReportError("模擬掃描器失敗（Port 可能已被占用）", ex)); }
        SafeUi(() => { _ = StopSimulatorAsync(); });
    }

    private async Task StopSimulatorAsync()
    {
        if (_simulatorCts is null) return;
        var cts = _simulatorCts;
        _simulatorCts = null;
        cts.Cancel();
        try { if (_simulatorTask != null) await _simulatorTask; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppendLog("模擬器停止：" + ex.Message); }
        _simulatorTask = null;
        cts.Dispose();
        AppendLog("模擬器已停止。");
        UpdateButtons();
    }

    private void RefreshCounters()
    {
        if (_store is null) return;
        try
        {
            using var stats = JsonDocument.Parse(JsonSerializer.Serialize(_store.Stats()));
            var r = stats.RootElement;
            _total.Text = r.GetProperty("total").GetInt64().ToString("N0");
            _accepted.Text = r.GetProperty("accepted").GetInt64().ToString("N0");
            _duplicates.Text = r.GetProperty("duplicates").GetInt64().ToString("N0");
            _noReads.Text = r.GetProperty("no_reads").GetInt64().ToString("N0");
            using var entries = JsonDocument.Parse(JsonSerializer.Serialize(_store.Events(1)));
            var rows = entries.RootElement.GetProperty("events");
            if (rows.GetArrayLength() > 0)
                _lastCode.Text = rows[0].GetProperty("barcode").GetString() ?? "—";
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void OnDiagnostic(string message)
    {
        SafeUi(() =>
        {
            AppendLog(message);
            if (message.Contains("Scanner connected", StringComparison.Ordinal))
                SetStatus("已連線", Success);
            else if (message.Contains("Connecting to", StringComparison.Ordinal) ||
                     message.Contains("Listening on", StringComparison.Ordinal))
                SetStatus("正在連線", Warning);
            else if (message.Contains("Connection error", StringComparison.Ordinal) ||
                     message.Contains("Scanner disconnected", StringComparison.Ordinal))
                SetStatus("等待重連", Warning);
            else if (message.Contains("[Collector] Connected:", StringComparison.Ordinal))
                SetStatus("已連線", Success);
        });
    }

    private void SafeUi(Action callback)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke(callback);
            else callback();
        }
        catch (InvalidOperationException) { }
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = "● " + text;
        _status.ForeColor = color;
        _tray.Text = "HF811 Collector · " + text;
    }

    private void AppendLog(string message)
    {
        var lines = _logs.Lines.ToList();
        lines.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
        if (lines.Count > 160) lines.RemoveRange(0, lines.Count - 160);
        _logs.Lines = lines.ToArray();
        _logs.SelectionStart = _logs.TextLength;
        _logs.ScrollToCaret();
        var preview = _overviewLog.Lines.ToList();
        preview.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
        if (preview.Count > 30) preview.RemoveRange(0, preview.Count - 30);
        _overviewLog.Lines = preview.ToArray();
        _overviewLog.SelectionStart = _overviewLog.TextLength;
        _overviewLog.ScrollToCaret();
    }

    private void UpdateButtons()
    {
        bool started = _collectorCts != null;
        _start.Enabled = !started && !_stopping;
        _stop.Enabled = started && !_stopping;
        _simulate.Text = _simulatorCts == null ? "◉  開始模擬掃描" : "■  停止模擬";
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OpenDataFolder()
    {
        try
        {
            string folder = Path.GetDirectoryName(ConfigFile.EnsureDefault())!;
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception ex) { ReportError("無法開啟資料夾", ex); }
    }

    private void OpenStats()
    {
        try
        {
            int port = _collectorCts != null ? _apiRunningPort : (int)_apiPort.Value;
            Process.Start(new ProcessStartInfo("http://127.0.0.1:" + port + "/stats") { UseShellExecute = true });
        }
        catch (Exception ex) { ReportError("無法開啟狀態 API", ex); }
    }

    private void ReportError(string label, Exception ex)
    {
        AppendLog(label + "：" + ex.Message);
        if (!Visible)
            _tray.ShowBalloonTip(2600, "HF811 Collector", label + "：" + ex.Message, ToolTipIcon.Warning);
        else
            MessageBox.Show(label + "：\n" + ex.Message, "HF811 Collector",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private async Task ExitApplicationAsync()
    {
        if (_closingForReal) return;
        _closingForReal = true;
        _tray.Visible = false;
        await StopSimulatorAsync();
        await StopCollectorAsync();
        Close();
    }
}
