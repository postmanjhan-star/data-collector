using System.Drawing;
using System.Windows.Forms;

namespace HF811Collector.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool smokeTest = args.Any(x => string.Equals(x, "--selftest", StringComparison.OrdinalIgnoreCase));
        try
        {
            ApplicationConfiguration.Initialize();
            if (smokeTest)
            {
                string template = Path.Combine(AppContext.BaseDirectory, "config.default.json");
                if (!File.Exists(template)) throw new FileNotFoundException("Missing installer config template", template);
                var settings = System.Text.Json.JsonSerializer.Deserialize<CollectorOptions>(File.ReadAllText(template))
                    ?? throw new InvalidDataException("Invalid configuration template");
                settings.Validate();
                using var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon is null) throw new InvalidOperationException("Desktop icon not embedded");
                return 0;
            }

            using var mutex = new Mutex(false, "HF811Collector-Desktop-GUI", out bool isFirst);
            if (!isFirst)
            {
                MessageBox.Show("HF811 Collector 已在系統匣執行。\n請點右下角圖示開啟主視窗。",
                    "HF811 Collector", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            bool background = args.Any(x => x is "--background" or "--minimized");
            Application.Run(new MainForm(background));
            return 0;
        }
        catch (Exception ex)
        {
            if (!smokeTest)
                MessageBox.Show(ex.ToString(), "HF811 Collector 啟動失敗",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
