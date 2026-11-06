using System.Drawing;
using System.Net;
using System.Windows.Forms;

namespace HF811Collector.Desktop;

internal sealed class DiscoveryDialog : Form
{
    private readonly ComboBox _adapters = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _prefix = new() { Dock = DockStyle.Fill, PlaceholderText = "192.168.16" };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Dock = DockStyle.Fill };
    private readonly Label _status = new() { Text = "選擇網路介面與 Port，按「開始搜尋」。", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(86, 106, 126) };
    private readonly Label _sampleInfo = new() { Text = "尚未取樣；無法透過 TCP 探測讀取設備序號、韌體或完整設定。", Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly ListView _devices = new()
    {
        View = View.Details, FullRowSelect = true, MultiSelect = false,
        Dock = DockStyle.Fill, HideSelection = false, BorderStyle = BorderStyle.FixedSingle
    };
    private readonly Button _scan = new() { Text = "開始搜尋", Dock = DockStyle.Fill };
    private readonly Button _sample = new() { Text = "取樣判斷尾碼", Dock = DockStyle.Fill, Enabled = false };
    private readonly Button _apply = new() { Text = "套用選取的 IP / Port", Dock = DockStyle.Fill, Enabled = false };
    private readonly CheckBox _applyFraming = new()
    {
        Text = "套用取樣推測的資料分隔符（需確認）",
        Dock = DockStyle.Fill, Enabled = false, AutoSize = true
    };
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _sampleCts;
    private string? _framingGuess;

    public DiscoveryCandidate? SelectedCandidate { get; private set; }
    public string? SelectedFraming { get; private set; }

    public DiscoveryDialog(string currentIp, int port)
    {
        Text = "尋找區網掃描器 · HF811 Collector";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(830, 620);
        MinimumSize = new Size(740, 520);
        BackColor = Color.FromArgb(247, 249, 252);
        Font = new Font("Microsoft JhengHei UI", 9.5F);
        _port.Value = Math.Clamp(port, 1, 65535);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(22) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        Controls.Add(root);

        var intro = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        intro.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        intro.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        intro.Controls.Add(Label("尋找可連線的掃描器", 16F, true), 0, 0);
        intro.Controls.Add(Label("僅檢查選定 /24 網段的 TCP Port；開放的 Port 不代表已確認是 HF811。", 9F), 0, 1);
        root.Controls.Add(intro, 0, 0);

        var selectors = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2 };
        selectors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        selectors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26));
        selectors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14));
        selectors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14));
        selectors.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        selectors.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        selectors.Controls.Add(Label("本機網路介面", 9F), 0, 0);
        selectors.Controls.Add(Label("搜尋網段（前三段）", 9F), 1, 0);
        selectors.Controls.Add(Label("TCP Port", 9F), 2, 0);
        selectors.Controls.Add(Label("操作", 9F), 3, 0);
        foreach (Control c in new Control[] { _adapters, _prefix, _port, _scan })
            c.Margin = new Padding(0, 2, 8, 4);
        selectors.Controls.Add(_adapters, 0, 1);
        selectors.Controls.Add(_prefix, 1, 1);
        selectors.Controls.Add(_port, 2, 1);
        selectors.Controls.Add(_scan, 3, 1);
        root.Controls.Add(selectors, 0, 1);
        root.Controls.Add(_status, 0, 2);

        _devices.Columns.Add("候選 IP", 220);
        _devices.Columns.Add("Port", 85);
        _devices.Columns.Add("狀態", 390);
        _devices.SelectedIndexChanged += (_, _) =>
        {
            _sample.Enabled = _devices.SelectedItems.Count == 1 && _scanCts == null;
            _apply.Enabled = _devices.SelectedItems.Count == 1 && _scanCts == null;
            _framingGuess = null;
            _applyFraming.Checked = false;
            _applyFraming.Enabled = false;
            _sampleInfo.Text = "可選擇取樣測試，須讓掃描器實際讀到條碼才能推測尾碼。";
        };
        root.Controls.Add(_devices, 0, 3);

        var details = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _sampleInfo.TextAlign = ContentAlignment.MiddleLeft;
        details.Controls.Add(_sampleInfo, 0, 0);
        details.Controls.Add(_applyFraming, 0, 1);
        root.Controls.Add(details, 0, 4);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        footer.Controls.Add(new Label { Text = "只有點「套用」才會修改 Collector 表單。", Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        footer.Controls.Add(_sample, 1, 0);
        footer.Controls.Add(_apply, 2, 0);
        var cancel = new Button { Text = "關閉", Dock = DockStyle.Fill };
        cancel.Click += (_, _) => Close();
        footer.Controls.Add(cancel, 3, 0);
        root.Controls.Add(footer, 0, 5);

        foreach (var adapter in DeviceDiscovery.GetAdapters()) _adapters.Items.Add(adapter);
        string[] parts = currentIp.Split('.');
        string? currentPrefix = parts.Length == 4 && IPAddress.TryParse(currentIp, out _) ? string.Join(".", parts.Take(3)) : null;
        int selected = -1;
        for (int i = 0; i < _adapters.Items.Count; i++)
            if (_adapters.Items[i] is AdapterNetwork n && n.Prefix == currentPrefix) { selected = i; break; }
        if (_adapters.Items.Count > 0) _adapters.SelectedIndex = selected >= 0 ? selected : 0;
        if (currentPrefix != null && currentPrefix != "127.0.0") _prefix.Text = currentPrefix;
        _adapters.SelectedIndexChanged += (_, _) =>
        {
            if (_adapters.SelectedItem is AdapterNetwork n) _prefix.Text = n.Prefix;
        };
        _scan.Click += async (_, _) => await SearchAsync();
        _sample.Click += async (_, _) => await SampleAsync();
        _apply.Click += (_, _) => ApplyResult();
    }

    private static Label Label(string value, float size, bool bold = false) => new()
    {
        Text = value, Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = Color.FromArgb(32, 51, 74),
        Font = new Font("Microsoft JhengHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private async Task SearchAsync()
    {
        if (_scanCts != null) { _scanCts.Cancel(); return; }
        string prefix;
        try { prefix = DeviceDiscovery.ValidatePrefix(_prefix.Text); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "網段格式錯誤"); return; }

        _devices.Items.Clear();
        _scanCts = new CancellationTokenSource();
        _scan.Text = "取消搜尋";
        _sample.Enabled = false;
        _apply.Enabled = false;
        _sampleInfo.Text = "正在搜尋。僅 TCP 握手，不發送設定或掃描指令。";
        int opened = 0;
        var progress = new Progress<ScanProgress>(p =>
        {
            if (IsDisposed) return;
            if (p.Found != null)
            {
                opened++;
                var item = new ListViewItem(new[] { p.Found.Ip, p.Found.Port.ToString(), "TCP 開放 · 設備型號未驗證" }) { Tag = p.Found };
                _devices.Items.Add(item);
            }
            _status.Text = $"搜尋中：已檢查 {p.Probed} / {p.Total} 位址，找到 {opened} 個 TCP 候選。";
        });
        try
        {
            var found = await DeviceDiscovery.ScanAsync(prefix, (int)_port.Value, progress, _scanCts.Token);
            _status.Text = $"搜尋完成：找到 {found.Count} 個 TCP 候選。若無結果，確認筆電與掃描器位於同一網段，或 TCP Server 已啟用。";
        }
        catch (OperationCanceledException) { _status.Text = "搜尋已取消。"; }
        catch (Exception ex) { _status.Text = "搜尋失敗：" + ex.Message; }
        finally
        {
            _scanCts?.Dispose();
            _scanCts = null;
            _scan.Text = "開始搜尋";
            _sample.Enabled = _devices.SelectedItems.Count == 1;
            _apply.Enabled = _devices.SelectedItems.Count == 1;
        }
    }

    private async Task SampleAsync()
    {
        if (_devices.SelectedItems.Count != 1 ||
            _devices.SelectedItems[0].Tag is not DiscoveryCandidate target) return;
        _sampleCts?.Cancel();
        _sampleCts = new CancellationTokenSource();
        _sample.Enabled = false;
        _status.Text = "正在等待一筆測試條碼，最多 8 秒。請讓掃描器實際讀碼。";
        try
        {
            var result = await DeviceDiscovery.SampleFramingAsync(target.Ip, target.Port, _sampleCts.Token);
            _framingGuess = result.Framing;
            _sampleInfo.Text = result.Description + (result.BytesRead > 0 ? $" · HEX: {result.HexPreview}" : "");
            _applyFraming.Enabled = result.Framing != null;
            _applyFraming.Checked = false;
            _status.Text = result.BytesRead > 0 ? $"已收到 {result.BytesRead} bytes；尾碼僅供參考，請自行核對。" : "取樣期間未收到任何掃描資料。";
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or FormatException)
        {
            _status.Text = "取樣失敗：" + ex.Message;
        }
        catch (OperationCanceledException) { _status.Text = "取樣已取消。"; }
        finally { _sample.Enabled = true; _sampleCts?.Dispose(); _sampleCts = null; }
    }

    private void ApplyResult()
    {
        if (_devices.SelectedItems.Count != 1 ||
            _devices.SelectedItems[0].Tag is not DiscoveryCandidate candidate) return;
        SelectedCandidate = candidate;
        SelectedFraming = _applyFraming.Checked ? _framingGuess : null;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _scanCts?.Cancel();
        _sampleCts?.Cancel();
        base.OnFormClosing(e);
    }
}
