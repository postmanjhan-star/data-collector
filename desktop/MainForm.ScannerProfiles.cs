using System.ComponentModel;
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using HF811Collector;

namespace HF811Collector.Desktop;

internal sealed partial class MainForm
{
    private List<ScannerProfile> _scannerProfiles = new();
    private readonly Button _editScannerProfiles = MButton("設定掃描器清單", BlueModern, true);

    private void RefreshScannerProfiles()
    {
        _editScannerProfiles.Text = _scannerProfiles.Count == 0 ? "設定掃描器清單" : $"設定掃描器（{_scannerProfiles.Count} 台）";
        UpdateConnectionLine();
    }

    private void EditScannerProfiles()
    {
        if (_collectorCts != null || _simulatorCts != null || _stopping) return;
        using var dialog = new Form
        {
            Text = "掃描器清單 · 多設備 / 串列", StartPosition = FormStartPosition.CenterParent,
            Size = new Size(1000, 620), MinimumSize = new Size(750, 480),
            Font = Font, AutoScaleMode = AutoScaleMode.Dpi
        };
        var profiles = new BindingList<ScannerProfile>(JsonSerializer.Deserialize<List<ScannerProfile>>(
            JsonSerializer.Serialize(_scannerProfiles))!);
        var binding = new BindingSource { DataSource = profiles };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        var hint = new Label
        {
            Dock = DockStyle.Fill, Text = "清單有設備時取代原本單一 TCP 設定；清空清單恢復舊模式。\n上下掃請使用不同 DeviceId，例如 SC2-UPPER / SC2-LOWER。Serial 的 Host 填 COM 埠；TCP Listen 填本機 IP、獨立 Port 與來源 IP。"
        };
        layout.Controls.Add(hint, 0, 0);
        var grid = new DataGridView { Dock = DockStyle.Fill, DataSource = binding, AutoGenerateColumns = false,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false, RowHeadersVisible = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
        void TextColumn(string property, string caption, int width) => grid.Columns.Add(new DataGridViewTextBoxColumn
        { DataPropertyName = property, HeaderText = caption, Width = width });
        void ChoiceColumn(string property, string caption, object[] values, int width) => grid.Columns.Add(new DataGridViewComboBoxColumn
        { DataPropertyName = property, HeaderText = caption, DataSource = values, Width = width });
        TextColumn(nameof(ScannerProfile.DeviceId), "設備 ID", 160);
        ChoiceColumn(nameof(ScannerProfile.Transport), "傳輸", new object[] { "tcp-connect", "tcp-listen", "serial" }, 135);
        TextColumn(nameof(ScannerProfile.Host), "IP / COM", 145);
        TextColumn(nameof(ScannerProfile.Port), "TCP Port", 95);
        TextColumn(nameof(ScannerProfile.ExpectedPeerIp), "來源 IP（Listen）", 140);
        ChoiceColumn(nameof(ScannerProfile.Framing), "分隔", new object[] { "lf", "crlf", "cr", "idle" }, 90);
        TextColumn(nameof(ScannerProfile.IdleGapMs), "Idle ms", 90);
        TextColumn(nameof(ScannerProfile.ReconnectMs), "重連 ms", 90);
        TextColumn(nameof(ScannerProfile.BaudRate), "Baud", 90);
        TextColumn(nameof(ScannerProfile.DataBits), "Data bits", 90);
        ChoiceColumn(nameof(ScannerProfile.Parity), "Parity", Enum.GetValues<System.IO.Ports.Parity>().Cast<object>().ToArray(), 100);
        ChoiceColumn(nameof(ScannerProfile.StopBits), "Stop bits", new object[] { System.IO.Ports.StopBits.One, System.IO.Ports.StopBits.Two, System.IO.Ports.StopBits.OnePointFive }, 110);
        ChoiceColumn(nameof(ScannerProfile.Handshake), "Handshake", Enum.GetValues<System.IO.Ports.Handshake>().Cast<object>().ToArray(), 140);
        layout.Controls.Add(grid, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        Button AddButton(string text, Action click)
        {
            var button = new Button { Text = text, AutoSize = true, Padding = new Padding(9, 5, 9, 5) };
            button.Click += (_, _) => click();
            buttons.Controls.Add(button);
            return button;
        }
        AddButton("新增設備", () => { profiles.Add(new ScannerProfile { DeviceId = "SCANNER-" + Guid.NewGuid().ToString("N")[..6] }); });
        AddButton("刪除選取", () => { if (grid.CurrentRow?.DataBoundItem is ScannerProfile current) profiles.Remove(current); });
        AddButton("上下掃範本", () =>
        {
            if (profiles.Count != 0) { MessageBox.Show(dialog, "請先移除現有清單，才可套用範本。"); return; }
            profiles.Add(new ScannerProfile { DeviceId = "SC2-UPPER", Host = "127.0.0.1", Port = 55256 });
            profiles.Add(new ScannerProfile { DeviceId = "SC2-LOWER", Host = "127.0.0.1", Port = 55257 });
        });
        AddButton("套用", () =>
        {
            try
            {
                if (!grid.EndEdit()) throw new ArgumentException("請修正無效的欄位內容");
                binding.EndEdit();
                ScannerProfile.ValidateAll(profiles.ToList(), (int)_apiPort.Value);
                _scannerProfiles = profiles.ToList();
                RefreshScannerProfiles();
                UpdateButtons();
                dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "設定錯誤"); }
        });
        AddButton("取消", () => dialog.DialogResult = DialogResult.Cancel);
        layout.Controls.Add(buttons, 0, 2);
        dialog.Controls.Add(layout);
        dialog.ShowDialog(this);
    }
}
