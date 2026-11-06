using System.Windows.Forms;

namespace HF811Collector.Desktop;

/// <summary>Prevents accidental scrolling of production timeouts while the settings panel scrolls.</summary>
internal sealed class WheelSafeNumericUpDown : NumericUpDown
{
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Deliberately do not change the setting via mouse wheel.
        // Operators must use keyboard input or the numeric spinner.
    }
}
