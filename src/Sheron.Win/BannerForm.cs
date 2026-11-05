using System.Runtime.InteropServices;
using Sheron.Win.Ui;

namespace Sheron.Win;

/// <summary>
/// The banner on the remote PC while a viewer is let in (Plan step 15a; Incoming board, part 2; PRD F6, A6;
/// CLAUDE.md › No hidden access): a pill at the top centre of the screen, "Controlled by …", the session time and
/// Disconnect. Always on top, not in the taskbar, never takes the keyboard from the person, and cannot be closed or
/// hidden: only the end of the session (<see cref="End"/>) removes it. Disconnect ends the session at once.
/// </summary>
internal sealed class BannerForm : PaintedForm
{
    private readonly ThisPc _pc;
    private readonly PaintedButton _disconnect;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private bool _ending;

    public BannerForm(ThisPc pc)
    {
        _pc = pc;
        _disconnect = new PaintedButton(ButtonLook.Danger, "Disconnect", pc.EndSharing) { Radius = 999, Fill = Theme.StopShade };
        Buttons.Add(_disconnect);
        SuspendLayout();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Theme.Panel;
        Text = "Sheron — this PC is being controlled";
        ResumeLayout(false);
        _tick.Tick += (_, _) =>
        {
            KeepOnTop();
            Invalidate(TimeArea);
        };
        _tick.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int ToolWindow = 0x80, NoActivate = 0x08000000, TopMostStyle = 0x8;
            var p = base.CreateParams;
            p.ExStyle |= ToolWindow | NoActivate | TopMostStyle;
            return p;
        }
    }

    private string Who => _pc.Viewer.Name;

    private int TextLeft => Px(16 + 8 + 12);

    private int TimeLeftEdge => TextLeft + Draw.Measure("Controlled by ", Sans(14)).Width + Draw.Measure(Who, Sans(14, Weight.SemiBold)).Width + Px(12);

    private Rectangle TimeArea => new(TimeLeftEdge, 0, Draw.Measure("00:00:00", Mono(13)).Width, ClientSize.Height);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Fit();
    }

    /// <summary>Sized to its text, at the top centre of the main screen.</summary>
    private void Fit()
    {
        int width = TimeLeftEdge + Draw.Measure(SessionTime(), Mono(13)).Width + Px(12) + Px(124) + Px(6);
        var size = new Size(width, Px(56));
        var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Bounds = new Rectangle(screen.Left + (screen.Width - size.Width) / 2, screen.Top + Px(16), size.Width, size.Height);
        _disconnect.Bounds = new Rectangle(ClientSize.Width - Px(6) - Px(124), Px(6), Px(124), Px(44));
        _disconnect.Font = Sans(14, Weight.SemiBold);
        using var path = Draw.Round(new RectangleF(0, 0, ClientSize.Width, ClientSize.Height), ClientSize.Height / 2f);
        Region = new Region(path);
        Invalidate();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Fit();
    }

    private string SessionTime()
    {
        var time = DateTime.Now - _pc.SharedSince;
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        Draw.Box(g, ClientRectangle, ClientSize.Height / 2f, Theme.Panel, Theme.Accent, Px(1));
        int middle = ClientSize.Height / 2;
        Draw.Dot(g, new Point(Px(16) + Px(4), middle), Px(8), Theme.Accent);
        var plain = Sans(14);
        string lead = "Controlled by ";
        int at = TextLeft;
        Draw.Text(g, lead, plain, new Rectangle(at, 0, Draw.Measure(lead, plain).Width, ClientSize.Height), Theme.Ink);
        at += Draw.Measure(lead, plain).Width;
        var bold = Sans(14, Weight.SemiBold);
        Draw.Text(g, Who, bold, new Rectangle(at, 0, Draw.Measure(Who, bold).Width, ClientSize.Height), Theme.Ink);
        Draw.Text(g, SessionTime(), Mono(13), TimeArea, Theme.Muted);
        DrawButtons(g);
    }

    /// <summary>Another window may have taken the top: take it back, without taking the keyboard.</summary>
    private void KeepOnTop()
    {
        const uint NoMove = 0x2, NoSize = 0x1, NoActivate = 0x10;
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, NoMove | NoSize | NoActivate);
        if (!Visible) Show();
    }

    /// <summary>The session is over: the only way the banner goes.</summary>
    public void End()
    {
        _ending = true;
        _tick.Stop();
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Alt+F4 and the like do not hide it while the session lasts; closing Windows does.
        if (!_ending && e.CloseReason is CloseReason.UserClosing or CloseReason.None) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
