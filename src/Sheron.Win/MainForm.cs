using System.Net;
using Sheron.Core;
using Sheron.Core.Session;

namespace Sheron.Win;

/// <summary>
/// Start window until the home screen is built (Plan step 15; PRD decision 1 Nov): "Share this screen" or "View a PC"
/// by IP address. The choices are drawn on the form; the IP box is the one real control (CLAUDE.md › Windows UI).
/// </summary>
internal sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(0x0E, 0x14, 0x1B);
    private static readonly Color Panel = Color.FromArgb(0x15, 0x1E, 0x28);
    private static readonly Color PanelHover = Color.FromArgb(0x1C, 0x28, 0x36);
    private static readonly Color Line = Color.FromArgb(0x24, 0x31, 0x42);
    private static readonly Color Ink = Color.FromArgb(0xE6, 0xED, 0xF3);
    private static readonly Color Muted = Color.FromArgb(0x9F, 0xB0, 0xC3);
    private static readonly Color Accent = Color.FromArgb(0xF2, 0xA9, 0x3B);
    private static readonly Color Bad = Color.FromArgb(0xE5, 0x53, 0x4B);
    private const TextFormatFlags Wrapped = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;

    private readonly TextBox _address = new() { BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "IP address, such as 192.168.1.20" };
    private readonly Font _titleFont = new("Segoe UI Semibold", 15f);
    private readonly Font _buttonFont = new("Segoe UI Semibold", 12f);
    private readonly Font _textFont = new("Segoe UI", 10f);
    private readonly Font _addressFont = new("Consolas", 12f);
    private Rectangle _shareButton;
    private Rectangle _viewButton;
    private Rectangle? _hover;
    private string? _problem;

    public MainForm()
    {
        // Sizes are at 96 DPI; ResumeLayout scales them to the screen's DPI.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = AppInfo.Name;
        BackColor = Background;
        ClientSize = new Size(560, 400);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        _address.Font = _addressFont;
        _address.BackColor = Panel;
        _address.ForeColor = Ink;
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            View();
        };
        Controls.Add(_address);
        ResumeLayout(false);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int margin = LogicalToDeviceUnits(28);
        int width = ClientSize.Width - 2 * margin;
        _shareButton = new Rectangle(margin, LogicalToDeviceUnits(96), width, LogicalToDeviceUnits(84));
        int addressTop = LogicalToDeviceUnits(232);
        int buttonWidth = LogicalToDeviceUnits(120);
        int gap = LogicalToDeviceUnits(12);
        _address.Bounds = new Rectangle(margin, addressTop, width - buttonWidth - gap, _address.PreferredHeight);
        _viewButton = new Rectangle(margin + width - buttonWidth, addressTop, buttonWidth, _address.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        int margin = LogicalToDeviceUnits(28);
        int width = ClientSize.Width - 2 * margin;

        TextRenderer.DrawText(g, AppInfo.Name, _titleFont, new Point(margin, LogicalToDeviceUnits(20)), Accent);
        TextRenderer.DrawText(g, "Test version · same network only · no password yet", _textFont,
            new Point(margin, LogicalToDeviceUnits(56)), Muted);

        DrawButton(g, _shareButton, "Share this screen");
        TextRenderer.DrawText(g, "Use this on the PC to be viewed. A window shows who is viewing; close it to stop.", _textFont,
            Rectangle.Inflate(new Rectangle(_shareButton.Left, _shareButton.Top + _shareButton.Height / 2, _shareButton.Width, _shareButton.Height / 2), -LogicalToDeviceUnits(16), 0),
            Muted, Wrapped);

        TextRenderer.DrawText(g, "View a PC: type the IP address its sharing window shows", _textFont,
            new Point(margin, _address.Top - LogicalToDeviceUnits(28)), Ink);
        DrawButton(g, _viewButton, "View");

        if (_problem is not null)
            TextRenderer.DrawText(g, _problem, _textFont,
                new Rectangle(margin, _address.Bottom + LogicalToDeviceUnits(14), width, LogicalToDeviceUnits(60)), Bad, Wrapped);
    }

    private void DrawButton(Graphics g, Rectangle area, string label)
    {
        using (var fill = new SolidBrush(_hover == area ? PanelHover : Panel)) g.FillRectangle(fill, area);
        using (var border = new Pen(Line)) g.DrawRectangle(border, area.X, area.Y, area.Width - 1, area.Height - 1);
        // A tall button has its label in the top half and a line of help below.
        var labelArea = area.Height > LogicalToDeviceUnits(60)
            ? new Rectangle(area.Left + LogicalToDeviceUnits(16), area.Top + LogicalToDeviceUnits(10), area.Width, area.Height / 2)
            : area;
        var flags = area.Height > LogicalToDeviceUnits(60)
            ? TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            : TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(g, label, _buttonFont, labelArea, Ink, flags);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Rectangle? over = _shareButton.Contains(e.Location) ? _shareButton : _viewButton.Contains(e.Location) ? _viewButton : null;
        if (over == _hover) return;
        _hover = over;
        Cursor = over is null ? Cursors.Default : Cursors.Hand;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        if (_shareButton.Contains(e.Location)) Open(new HostTestForm(Link.DefaultPort));
        else if (_viewButton.Contains(e.Location)) View();
    }

    private void View()
    {
        if (!IPEndPoint.TryParse(_address.Text.Trim(), out var remote) || remote.Address.Equals(IPAddress.Any))
        {
            _problem = $"\"{_address.Text.Trim()}\" is not an IP address. Type it as the sharing window shows it, such as 192.168.1.20.";
            Invalidate();
            return;
        }
        if (remote.Port == 0) remote.Port = Link.DefaultPort;
        Open(new ViewerTestForm(remote));
    }

    /// <summary>Shows the chosen window in place of this one; closing it ends Sheron.</summary>
    private void Open(Form next)
    {
        next.FormClosed += (_, _) => Close();
        next.Show();
        Hide();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _buttonFont.Dispose();
            _textFont.Dispose();
            _addressFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
