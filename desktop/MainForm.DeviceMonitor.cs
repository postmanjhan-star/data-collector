using System.Drawing;
using System.Windows.Forms;
using HF811Collector;

namespace HF811Collector.Desktop;

internal sealed partial class MainForm
{
    private void ShowDeviceMonitor()
    {
        using var dialog = new Form { Text = "設備即時狀態與統計", Size = new Size(1000, 460), MinimumSize = new Size(650, 320),
            StartPosition = FormStartPosition.CenterParent, Font = Font };
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells };
        foreach (var column in new[] { "設備 ID", "連線狀態", "總筆數", "有效", "重複", "NOREAD", "最後收碼 UTC" })
            grid.Columns.Add(column, column);
        void Refresh()
        {
            grid.Rows.Clear();
            var rows = _store?.DeviceSnapshots();
            if (rows != null)
                foreach (var v in rows) grid.Rows.Add(v.DeviceId, v.State switch
                {
                    "connected" => "已連線", "listening" => "等待設備", "connecting" => "連線中",
                    "retrying" => "等待重連", "starting" => "啟動中", _ => "未啟動"
                }, v.Total, v.Accepted, v.Duplicates, v.NoReads, v.LastScanUtc);
            else
                foreach (var id in _scannerProfiles.Count > 0 ? _scannerProfiles.Select(s => s.DeviceId) : new[] { _device.Text })
                    grid.Rows.Add(id, "未啟動", "—", "—", "—", "—", "—");
        }
        using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => Refresh();
        dialog.Controls.Add(grid);
        Refresh(); timer.Start(); dialog.ShowDialog(this); timer.Stop();
    }

    private Task StartMultiSimulatorAsync()
    {
        // Prevent simulator mode from starting a collector against real devices.
        if (_scannerProfiles.Any(s => s.Transport != "tcp-connect" || s.Host != "127.0.0.1" || s.Framing == "idle")
            || !Path.GetFileName(_dbPath.Text).StartsWith("simulation-", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "請先按準備離線測試並選擇上下雙掃。模擬僅允許本機 TCP 與獨立 simulation SQLite。");
            return Task.CompletedTask;
        }
        if (_syncEnabled.Checked && MessageBox.Show(this, "模擬資料將上傳，請確認是隔離測試環境。繼續？", "模擬上傳", MessageBoxButtons.YesNo) != DialogResult.Yes)
            return Task.CompletedTask;
        var cts = new CancellationTokenSource();
        _simulatorCts = cts;
        _simulatorTask = RunMultiAsync(cts);
        AppendLog($"已啟動 {_scannerProfiles.Count} 台本機模擬掃描器（每 5 筆重複、每 10 筆 NOREAD）。");
        UpdateButtons();
        _ = WatchSimulatorAsync(_simulatorTask);
        return Task.CompletedTask;
    }

    private async Task RunMultiAsync(CancellationTokenSource cts)
    {
        var tasks = _scannerProfiles.Select(s => SimulatorRunner.RunAsync(new[] {
            "--mode", "server", "--host", "127.0.0.1", "--port", s.Port.ToString(),
            "--interval-ms", "500", "--repeat-every", "5", "--no-read-every", "10", "--delimiter", s.Framing
        }, cts.Token)).ToArray();
        try { await await Task.WhenAny(tasks); }
        finally { cts.Cancel(); try { await Task.WhenAll(tasks); } catch (OperationCanceledException) when (cts.IsCancellationRequested) { } }
    }
}
