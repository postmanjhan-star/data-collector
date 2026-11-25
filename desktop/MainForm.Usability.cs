using System.Drawing;
using System.Windows.Forms;

namespace HF811Collector.Desktop;

internal sealed partial class MainForm
{
    private readonly Label _overviewSync = MLabel("尚未啟動", 12, true, MutedModern);
    private readonly Label _overviewSyncDetail = MLabel("啟動後顯示 SQLite 與伺服器回執", 9, false, MutedModern);

    private void RefreshTransportHint()
    {
        string url = _serverUrl.Text.Trim();
        if (!_applyingSyncSettings)
        {
            int scheme = url.StartsWith("mqtts://", StringComparison.OrdinalIgnoreCase) ? 1 : url.StartsWith("mqtt://", StringComparison.OrdinalIgnoreCase) ? 2 : url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 3 : url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 0 : -1;
            if (scheme >= 0 && _transportChoice.SelectedIndex != scheme)
            {
                _applyingSyncSettings = true;
                _transportChoice.SelectedIndex = scheme;
                _applyingSyncSettings = false;
            }
        }
        bool mqtt = url.StartsWith("mqtts://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("mqtt://", StringComparison.OrdinalIgnoreCase);
        _transportHint.Text = mqtt
            ? $"MQTT 帳號 = Collector ID；{_collectorId.Text.Trim()}；僅 smt/test/v1"
            : "HTTP：輸入完整 /scans/batch URL，跨機器使用 HTTPS";
        _serverKey.PlaceholderText = mqtt ? "留空保留原密碼（DPAPI）" : "留空保留原 API Key（DPAPI）";
    }

    private void TransportChoiceChanged()
    {
        if (_applyingSyncSettings) return;
        string[] prefixes = { "https://", "mqtts://", "mqtt://", "http://" };
        int i = _transportChoice.SelectedIndex;
        if (i < 0 || i >= prefixes.Length) return;
        if (!_serverUrl.Text.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase))
        {
            // Never silently carry an HTTP path or a broker host into a different transport.
            _serverUrl.Text = i == 2 ? "mqtt://127.0.0.1:1883" : i == 3 ? "http://127.0.0.1:18090/api/v1/collector/scans/batch" : prefixes[i];
        }
        RefreshTransportHint();
    }

    private void SetConfigurationEditable(bool editable)
    {
        foreach (Control c in new Control[] { _mode, _host, _device, _port, _framing, _reconnect, _dedup, _idleGap,
            _noRead, _apiPort, _dbPath, _syncEnabled, _collectorId, _serverUrl, _serverKey, _syncInterval, _batchSize,
            _transportChoice, _showSecret })
            c.Enabled = editable;
    }

    private void BrowseDatabase()
    {
        if (_collectorCts != null || _simulatorCts != null) return;
        using var dialog = new SaveFileDialog { Title = "選擇 SQLite 資料庫（不會刪除或覆寫現有資料）",
            Filter = "SQLite database (*.sqlite3)|*.sqlite3|All files (*.*)|*.*",
            DefaultExt = "sqlite3", OverwritePrompt = false, CheckPathExists = true, FileName = "scans.sqlite3" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _dbPath.Text = dialog.FileName;
    }

    private void PrepareOfflineTest()
    {
        if (_collectorCts != null || _simulatorCts != null || _stopping)
        {
            MessageBox.Show(this, "先停止收集及模擬器，才能準備測試設定。");
            return;
        }
        if (MessageBox.Show(this, "將目前表單改為離線模擬設定，使用新的 SQLite 並停用背景上傳。原資料庫不變；請先備份原設定，確認後再啟動收集。", "準備離線測試", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _mode.SelectedItem = "connect";
        _host.Text = "127.0.0.1";
        _port.Value = 55256;
        _framing.SelectedItem = "lf";
        _device.Text = "SIMULATOR";
        _dbPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HF811Collector", "data", "simulation-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        _syncEnabled.Checked = false;
        _serverKey.Clear();
        _collectorId.Text = "SIM-" + Guid.NewGuid().ToString("N");
        UpdateConnectionLine();
        RefreshTransportHint();
        SwitchPage(1);
        AppendLog("已準備離線模擬表單；請確認設定，再開始模擬掃描及啟動收集。");
    }

    internal static void RunLayoutSelfTest()
    {
        using var form = new MainForm(false, layoutTest: true);
        try
        {
            form.ShowInTaskbar = false;
            form.Show();
            form.Size = new Size(1100, 740);
            form.PerformLayout();
            Application.DoEvents();
            form.SwitchPage(3);
            form._transportChoice.SelectedIndex = 2;
            if (form._serverUrl.Text != "mqtt://127.0.0.1:1883") throw new InvalidOperationException("MQTT preset failed");
            form._serverUrl.Text = "mqtts://broker.example:8883";
            if (form._transportChoice.SelectedIndex != 1) throw new InvalidOperationException("Pasted transport did not update selection");
            form._showSecret.Checked = true;
            if (form._serverKey.UseSystemPasswordChar) throw new InvalidOperationException("Password visibility toggle failed");
            form._showSecret.Checked = false;
            if (!form._serverKey.UseSystemPasswordChar) throw new InvalidOperationException("Password masking failed");
            form.SetConfigurationEditable(false);
            if (form._host.Enabled || form._serverUrl.Enabled || form._transportChoice.Enabled) throw new InvalidOperationException("Settings lock failed");
            form.SetConfigurationEditable(true);
            if (!form._host.Enabled || !form._serverUrl.Enabled) throw new InvalidOperationException("Settings unlock failed");
            foreach (Size size in new[] { new Size(1100, 740), new Size(1024, 640), new Size(900, 620) })
            {
                form.Size = size;
                foreach (int page in new[] { 0, 1, 2, 3 })
                {
                    form.SwitchPage(page);
                    form.PerformLayout();
                    Application.DoEvents();
                    form.AssertTextFits("AppHeading", "AppSubtitle");
                    if (page == 0) form.AssertTextFits("ScannerConnectionNote", "OverviewSyncDetail");
                    if (page == 3) form.AssertTextFits("CredentialState", "ShowSecret", "TransportHelp", "SyncWarning");
                    string? output = Environment.GetEnvironmentVariable("HF811_LAYOUT_TEST_OUTPUT");
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        Directory.CreateDirectory(output);
                        using var bitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                        bitmap.Save(Path.Combine(output, $"layout-{size.Width}x{size.Height}-page{page}.png"),
                            System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
            }
            form.Hide();
        }
        finally { form._timer.Dispose(); form._tray.Dispose(); }
    }

    private void AssertTextFits(params string[] names)
    {
        foreach (string name in names)
        {
            var control = Controls.Find(name, true).Single();
            // Measure real text at runner DPI and current width, including wrapped lines.
            int availableWidth = control.ClientSize.Width - control.Padding.Horizontal;
            if (control is CheckBox) availableWidth -= Math.Max(18, control.Font.Height);
            if (availableWidth <= 0 || control.ClientSize.Height <= 0)
                throw new InvalidOperationException($"Layout has zero-sized control: {name}");
            var required = TextRenderer.MeasureText(control.Text, control.Font,
                new Size(availableWidth, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            if (required.Height > control.ClientSize.Height - control.Padding.Vertical)
                throw new InvalidOperationException($"Clipped text: {name}, required {required.Height}, actual {control.ClientSize.Height}, DPI {DeviceDpi}");
        }
    }

    private void CopyDiagnosticLog()
    {
        if (string.IsNullOrWhiteSpace(_logs.Text)) return;
        try
        {
            Clipboard.SetText(_logs.Text);
            MessageBox.Show(this, "已複製目前畫面日誌。分享前請確認不包含內部設備資訊。", "複製日誌");
        }
        catch (Exception ex) { ReportError("複製日誌失敗", ex); }
    }
}
