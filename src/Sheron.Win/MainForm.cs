using Sheron.Core;

namespace Sheron.Win;

/// <summary>Main window. Empty until the home screen is built (Plan step 15).</summary>
internal sealed class MainForm : Form
{
    public MainForm()
    {
        Text = AppInfo.Name;
        BackColor = Color.FromArgb(0x0E, 0x14, 0x1B);
        ClientSize = new Size(1040, 640);
        StartPosition = FormStartPosition.CenterScreen;
    }
}
