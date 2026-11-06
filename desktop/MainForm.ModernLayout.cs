using System.Drawing;
using System.Windows.Forms;

namespace HF811Collector.Desktop;

// Dedicated visual tree: the existing Collector/SQLite lifecycle remains in MainForm.cs.
internal sealed partial class MainForm
{
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill };
    private readonly Label _connectionLine = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(46, 70, 96) };
    private readonly Label _networkNote = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(105, 121, 140) };
    private readonly TextBox _overviewLog = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None, Dock = DockStyle.Fill, TabStop = false,
        BackColor = Color.White, ForeColor = Color.FromArgb(63, 80, 103),
        Font = new Font("Consolas", 9.25f)
    };
    private readonly Button _discover = new() { Text = "⌕  搜尋掃描器" };
    private readonly Button _overviewTab = NavButton("概覽");
    private readonly Button _settingsTab = NavButton("設備設定");
    private readonly Button _logsTab = NavButton("運作日誌");
    private Panel? _overviewPage;
    private Panel? _settingsPage;
    private Panel? _logsPage;

    private static readonly Color InkModern = Color.FromArgb(27, 45, 69);
    private static readonly Color MutedModern = Color.FromArgb(105, 123, 144);
    private static readonly Color BlueModern = Color.FromArgb(47, 106, 229);
    private static readonly Color BorderModern = Color.FromArgb(228, 233, 241);
    private static readonly Color PanelModern = Color.White;

    private void BuildModernLayout()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(940, 660);
        Size = new Size(1100, 770);
        BackColor = Color.FromArgb(246, 248, 252);

        SuspendLayout();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            BackColor = BackColor, Padding = new Padding(28, 17, 28, 14)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        Controls.Add(root);

        // Stable grid-based header instead of wrapping FlowLayout: no more overlap or clipped subtitle.
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 194));
        var iconBox = new PictureBox
        {
            Image = (Icon ?? SystemIcons.Application).ToBitmap(), Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom, Margin = new Padding(2, 10, 17, 13)
        };
        header.Controls.Add(iconBox, 0, 0);
        var titles = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, Margin = Padding.Empty };
        titles.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        titles.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        titles.Controls.Add(MLabel("HF811 Collector", 21, true, InkModern), 0, 0);
        titles.Controls.Add(MLabel("產線設備連線  /  條碼資料收集與診斷", 9.5f, false, MutedModern), 0, 1);
        header.Controls.Add(titles, 1, 0);
        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleRight;
        _status.Font = new Font("Microsoft JhengHei UI", 11.5f, FontStyle.Bold);
        header.Controls.Add(_status, 2, 0);
        root.Controls.Add(header, 0, 0);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, Margin = new Padding(0, 4, 0, 9) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        PrepareAction(_start, "▶  啟動收集", BlueModern);
        PrepareAction(_stop, "■  停止", Color.FromArgb(110, 128, 152));
        PrepareAction(_simulate, "◉  模擬掃描", Color.FromArgb(21, 142, 142));
        PrepareAction(_discover, "⌕  搜尋掃描器", Color.White, BlueModern);
        _start.Click += (_, _) => StartCollector();
        _stop.Click += async (_, _) => await StopCollectorAsync();
        _simulate.Click += async (_, _) => await ToggleSimulatorAsync();
        _discover.Click += (_, _) => ShowDiscovery();
        actions.Controls.Add(_start, 0, 0);
        actions.Controls.Add(_stop, 1, 0);
        actions.Controls.Add(_simulate, 2, 0);
        actions.Controls.Add(_discover, 3, 0);
        root.Controls.Add(actions, 0, 1);

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = new Padding(0, 3, 0, 2),
            Margin = Padding.Empty, BackColor = BackColor
        };
        _overviewTab.Click += (_, _) => SwitchPage(0);
        _settingsTab.Click += (_, _) => SwitchPage(1);
        _logsTab.Click += (_, _) => SwitchPage(2);
        nav.Controls.AddRange(new Control[] { _overviewTab, _settingsTab, _logsTab });
        root.Controls.Add(nav, 0, 2);

        _pageHost.Margin = new Padding(0, 6, 0, 4);
        _overviewPage = BuildOverview();
        _settingsPage = BuildSettings();
        _logsPage = BuildLogs();
        _pageHost.Controls.Add(_overviewPage);
        _pageHost.Controls.Add(_settingsPage);
        _pageHost.Controls.Add(_logsPage);
        root.Controls.Add(_pageHost, 0, 3);
        SwitchPage(0);

        var footer = MLabel("系統匣常駐 · 關閉視窗不會停止掃碼 · 設定修改需重新啟動 Collector 才會生效", 9, false, MutedModern);
        footer.Dock = DockStyle.Fill;
        root.Controls.Add(footer, 0, 4);
        ResumeLayout(true);
    }

    private Panel BuildOverview()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(grid);

        var connectionCard = MCard();
        connectionCard.Margin = new Padding(0, 0, 0, 12);
        var connection = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
        connection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        connection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        connection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        connection.Controls.Add(MLabel("目前設備連線", 10, true, InkModern), 0, 0);
        _connectionLine.Font = new Font("Segoe UI", 15.5f, FontStyle.Bold);
        _connectionLine.TextAlign = ContentAlignment.MiddleLeft;
        connection.Controls.Add(_connectionLine, 0, 1);
        _networkNote.Font = new Font("Microsoft JhengHei UI", 9f);
        _networkNote.TextAlign = ContentAlignment.MiddleLeft;
        connection.Controls.Add(_networkNote, 0, 2);
        var link = MLink("搜尋網路設備  →");
        link.Click += (_, _) => ShowDiscovery();
        connection.Controls.Add(link, 1, 1);
        connectionCard.Controls.Add(connection);
        grid.Controls.Add(connectionCard, 0, 0);

        var stats = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        for (int i = 0; i < 4; i++) stats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        stats.Controls.Add(Stat("掃描總筆數", _total, InkModern), 0, 0);
        stats.Controls.Add(Stat("有效資料", _accepted, Color.FromArgb(16, 147, 112)), 1, 0);
        stats.Controls.Add(Stat("重複掃描", _duplicates, InkModern), 2, 0);
        stats.Controls.Add(Stat("讀取失敗", _noReads, Color.FromArgb(201, 124, 48)), 3, 0);
        grid.Controls.Add(stats, 0, 1);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 63));
        var latest = MCard();
        latest.Margin = new Padding(0, 8, 10, 0);
        var latestContent = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4 };
        latestContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        latestContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        latestContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        latestContent.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        latestContent.Controls.Add(MLabel("最新條碼", 12, true, InkModern), 0, 0);
        _lastCode.AutoSize = false;
        _lastCode.Dock = DockStyle.Fill;
        _lastCode.Font = new Font("Consolas", 13f, FontStyle.Bold);
        _lastCode.AutoEllipsis = true;
        _lastCode.TextAlign = ContentAlignment.MiddleLeft;
        latestContent.Controls.Add(_lastCode, 0, 1);
        latestContent.Controls.Add(MLabel("資料保存在 SQLite，可從資料夾查閱完整紀錄。", 9.5f, false, MutedModern), 0, 2);
        var folder = MLink("開啟資料與日誌資料夾  →");
        folder.Click += (_, _) => OpenDataFolder();
        latestContent.Controls.Add(folder, 0, 3);
        latest.Controls.Add(latestContent);
        bottom.Controls.Add(latest, 0, 0);

        var logs = MCard();
        logs.Margin = new Padding(10, 8, 0, 0);
        var logLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var logTitle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        logTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        logTitle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        logTitle.Controls.Add(MLabel("即時事件", 12, true, InkModern), 0, 0);
        var moreLogs = MLink("完整日誌 →");
        moreLogs.Click += (_, _) => SwitchPage(2);
        logTitle.Controls.Add(moreLogs, 1, 0);
        logLayout.Controls.Add(logTitle, 0, 0);
        _overviewLog.BackColor = PanelModern;
        logLayout.Controls.Add(_overviewLog, 0, 1);
        logs.Controls.Add(logLayout);
        bottom.Controls.Add(logs, 1, 0);
        grid.Controls.Add(bottom, 0, 2);
        UpdateConnectionLine();
        return page;
    }

    private Panel BuildSettings()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, AutoScroll = true };
        var content = new TableLayoutPanel { Dock = DockStyle.Top, RowCount = 3, ColumnCount = 2, Height = 562 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 484));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));

        var left = SettingsCard("連線與設備", "連線目標");
        left.Margin = new Padding(0, 0, 10, 8);
        var leftGrid = SettingsGrid();
        AddModernField(leftGrid, "連線模式", _mode);
        AddModernField(leftGrid, "Scanner IP / Host", _host);
        AddModernField(leftGrid, "TCP Port", _port);
        AddModernField(leftGrid, "設備代號", _device);
        var discover = MButton("搜尋區網中的 TCP 裝置", BlueModern, true);
        discover.Click += (_, _) => ShowDiscovery();
        AddModernField(leftGrid, "自動尋找 IP", discover);
        left.Controls.Add(leftGrid, 0, 1);
        content.Controls.Add(left, 0, 0);

        var right = SettingsCard("資料格式與儲存", "收集器參數");
        right.Margin = new Padding(10, 0, 0, 8);
        var rightGrid = SettingsGrid();
        AddModernField(rightGrid, "資料分隔符", _framing);
        AddModernField(rightGrid, "重連間隔 (ms)", _reconnect);
        AddModernField(rightGrid, "去重時間 (ms)", _dedup);
        AddModernField(rightGrid, "Idle Gap (ms)", _idleGap);
        AddModernField(rightGrid, "NOREAD 值", _noRead);
        AddModernField(rightGrid, "本機 API Port", _apiPort);
        AddModernField(rightGrid, "SQLite 路徑", _dbPath);
        right.Controls.Add(rightGrid, 0, 1);
        content.Controls.Add(right, 1, 0);

        var save = MButton("儲存設定", BlueModern);
        save.Width = 155;
        save.Dock = DockStyle.Left;
        save.Click += (_, _) => SaveSettings();
        content.Controls.Add(save, 0, 1);
        var note = MLabel("注意：TCP 搜尋不能識別設備型號，也不會修改 Honeywell 韌體設定。", 9, false, MutedModern);
        content.SetColumnSpan(note, 2);
        content.Controls.Add(note, 0, 2);
        page.Controls.Add(content);
        return page;
    }

    private Panel BuildLogs()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
        var card = MCard();
        card.Dock = DockStyle.Fill;
        page.Controls.Add(card);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var head = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        head.Controls.Add(MLabel("運作日誌   ·   畫面保留最近 160 行", 12, true, InkModern), 0, 0);
        var clear = MLink("清除畫面");
        clear.Click += (_, _) => { _logs.Clear(); _overviewLog.Clear(); };
        head.Controls.Add(clear, 1, 0);
        var folder = MLink("開啟日誌資料夾 →");
        folder.Click += (_, _) => OpenDataFolder();
        head.Controls.Add(folder, 2, 0);
        grid.Controls.Add(head, 0, 0);
        _logs.BackColor = PanelModern;
        _logs.Dock = DockStyle.Fill;
        grid.Controls.Add(_logs, 0, 1);
        card.Controls.Add(grid);
        return page;
    }

    private void SwitchPage(int page)
    {
        if (_overviewPage == null || _settingsPage == null || _logsPage == null) return;
        _overviewPage.Visible = page == 0;
        _settingsPage.Visible = page == 1;
        _logsPage.Visible = page == 2;
        foreach (var (button, index) in new[] { (_overviewTab, 0), (_settingsTab, 1), (_logsTab, 2) })
        {
            button.BackColor = page == index ? Color.White : BackColor;
            button.ForeColor = page == index ? BlueModern : MutedModern;
            button.Font = new Font("Microsoft JhengHei UI", 10, page == index ? FontStyle.Bold : FontStyle.Regular);
        }
        if (page == 0) _overviewPage.BringToFront();
        if (page == 1) _settingsPage.BringToFront();
        if (page == 2) _logsPage.BringToFront();
        UpdateConnectionLine();
    }

    private void UpdateConnectionLine()
    {
        _connectionLine.Text = $"{_host.Text.Trim()}  :  {_port.Value}";
        _networkNote.Text = _mode.SelectedItem?.ToString() == "listen"
            ? "TCP Client 模式 · Collector 在本機監聽，等待掃描器主動連入"
            : "TCP Server 模式 · Collector 主動連線至掃描器";
    }

    private void ShowDiscovery()
    {
        if (_collectorCts != null || _simulatorCts != null)
        {
            MessageBox.Show(this, "搜尋前請先停止 Collector 與模擬器，以免探索連線影響正式收碼。",
                "停止收碼後再搜尋", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new DiscoveryDialog(_host.Text.Trim(), (int)_port.Value);
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedCandidate is { } result)
        {
            _mode.SelectedItem = "connect";
            _host.Text = result.Ip;
            _port.Value = result.Port;
            if (dialog.SelectedFraming != null) _framing.SelectedItem = dialog.SelectedFraming;
            UpdateConnectionLine();
            SwitchPage(1);
            AppendLog($"已選擇 TCP 候選 {result.Ip}:{result.Port}（尚未確認設備型號，請核對 HF811）。");
            MessageBox.Show(this,
                $"已將候選 IP {result.Ip} 與 Port {result.Port} 填入表單。\n請確認設備與尾碼設定，再按「儲存設定」。",
                "套用候選設備", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private static TableLayoutPanel SettingsGrid()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Margin = Padding.Empty
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        return grid;
    }

    private static TableLayoutPanel SettingsCard(string title, string subtitle)
    {
        var card = new TableLayoutPanel
        {
            BackColor = Color.White, Dock = DockStyle.Fill, Padding = new Padding(22, 17, 22, 15),
            ColumnCount = 1, RowCount = 2, CellBorderStyle = TableLayoutPanelCellBorderStyle.None
        };
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
        heading.Controls.Add(MLabel(title, 12, true, InkModern), 0, 0);
        heading.Controls.Add(MLabel(subtitle, 8.5f, false, MutedModern), 0, 1);
        card.Controls.Add(heading, 0, 0);
        return card;
    }

    private static void AddModernField(TableLayoutPanel grid, string label, Control field)
    {
        int index = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
        var caption = MLabel(label, 9.5f, false, InkModern);
        caption.Margin = Padding.Empty;
        grid.Controls.Add(caption, 0, index);
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(4, 7, 2, 7);
        if (field is TextBox t) { t.Font = new Font("Segoe UI", 10); t.BorderStyle = BorderStyle.FixedSingle; }
        if (field is ComboBox cb) { cb.FlatStyle = FlatStyle.Flat; cb.Font = new Font("Segoe UI", 10); }
        if (field is NumericUpDown num) num.Font = new Font("Segoe UI", 10);
        grid.Controls.Add(field, 1, index);
    }

    private static Panel MCard() => new()
    {
        BackColor = PanelModern, Dock = DockStyle.Fill, Padding = new Padding(20, 15, 20, 15)
    };

    private static Panel Stat(string caption, Label value, Color color)
    {
        var p = MCard();
        p.Margin = new Padding(0, 0, 12, 8);
        p.Padding = new Padding(14, 14, 12, 12);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        value.AutoSize = false;
        value.Dock = DockStyle.Fill;
        value.TextAlign = ContentAlignment.MiddleLeft;
        value.Font = new Font("Segoe UI", 25, FontStyle.Bold);
        value.ForeColor = color;
        content.Controls.Add(value, 0, 0);
        content.Controls.Add(MLabel(caption, 10, false, MutedModern), 0, 1);
        p.Controls.Add(content);
        return p;
    }

    private static Label MLabel(string text, float size, bool bold, Color color) => new()
    {
        Text = text, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true, ForeColor = color, BackColor = Color.Transparent,
        Font = new Font("Microsoft JhengHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
    };

    private static Button NavButton(string title) => new()
    {
        Text = title, Width = 142, Height = 42, FlatStyle = FlatStyle.Flat,
        BackColor = Color.White, ForeColor = MutedModern, Cursor = Cursors.Hand,
        Margin = new Padding(0, 0, 7, 0), FlatAppearance = { BorderSize = 0 }
    };

    private static Button MLink(string title) => new()
    {
        Text = title, FlatStyle = FlatStyle.Flat, ForeColor = BlueModern,
        BackColor = Color.White, Dock = DockStyle.Fill, Cursor = Cursors.Hand,
        TextAlign = ContentAlignment.MiddleRight, FlatAppearance = { BorderSize = 0 }
    };

    private static Button MButton(string title, Color color, bool outline = false) => new()
    {
        Text = title, BackColor = outline ? Color.White : color,
        ForeColor = outline ? color : Color.White, Dock = DockStyle.Fill,
        FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
        Font = new Font("Microsoft JhengHei UI", 9.5f, FontStyle.Bold),
        FlatAppearance = { BorderSize = outline ? 1 : 0, BorderColor = color }
    };

    private static void PrepareAction(Button button, string label, Color bg, Color? ink = null)
    {
        button.Text = label;
        button.BackColor = bg;
        button.ForeColor = ink ?? Color.White;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = ink.HasValue ? 1 : 0;
        button.FlatAppearance.BorderColor = BorderModern;
        button.Font = new Font("Microsoft JhengHei UI", 10, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 0, 9, 0);
        button.Padding = new Padding(5, 3, 5, 3);
    }
}
