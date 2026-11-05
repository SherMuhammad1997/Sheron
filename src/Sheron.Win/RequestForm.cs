using System.Runtime.InteropServices;
using Sheron.Core.Session;
using Sheron.Win.Ui;

namespace Sheron.Win;

/// <summary>
/// The incoming request on the remote PC (Plan step 15a; Incoming board, part 1; PRD F3, A3): a small window on top of
/// everything, in the middle of the screen. Accept or Deny; with no answer it denies by itself when the time is up
/// (<see cref="HostAccess"/> does that; this window only counts down). Closing it is Deny.
/// </summary>
internal sealed class RequestForm : PaintedForm
{
    private readonly ConsentRequest _request;
    private readonly string _where;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };
    private bool _answered;

    public RequestForm(ConsentRequest request, string where)
    {
        _request = request;
        _where = where;
        Buttons.Add(new PaintedButton(ButtonLook.Primary, "Accept", () => Answer(accept: true)));
        Buttons.Add(new PaintedButton(ButtonLook.Outline, "Deny", () => Answer(accept: false)));
        SuspendLayout();
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        Text = $"Sheron — {Who} wants to control this PC";
        KeyPreview = true;
        ResumeLayout(false);
        SizeAt96(400, 252);
        _tick.Tick += (_, _) =>
        {
            if (_request.TimeLeft <= TimeSpan.Zero) Close();
            else Invalidate(Countdown);
        };
        _tick.Start();
    }

    private string Who => _request.ViewerName.Length > 0 ? _request.ViewerName : "A viewer";

    private Rectangle Countdown => Px(24, 204, 352, 24);

    protected override CreateParams CreateParams
    {
        get
        {
            const int DropShadow = 0x20000;
            var p = base.CreateParams;
            p.ClassStyle |= DropShadow;
            return p;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RoundCorners(Handle);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Buttons.Count < 2) return;   // not made yet
        Buttons[0].Bounds = Px(24, 144, 170, 44);
        Buttons[1].Bounds = Px(206, 144, 170, 44);
        Buttons[0].Font = Sans(15, Weight.Bold);
        Buttons[1].Font = Sans(15);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        using (var fill = new SolidBrush(Theme.Panel)) g.FillRectangle(fill, ClientRectangle);
        using (var edge = new Pen(Theme.Edge)) g.DrawRectangle(edge, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        var icon = Px(24, 24, 44, 44);
        Draw.Dot(g, new Point(icon.Left + icon.Width / 2, icon.Top + icon.Height / 2), icon.Width, Theme.AccentShade);
        Draw.ScreenIcon(g, Rectangle.Inflate(icon, -Px(11), -Px(11)), Theme.Accent);

        // "<name> wants to control this PC": the name in bold, cut short if it does not fit.
        int left = Px(82), right = ClientSize.Width - Px(24);
        var bold = Sans(17, Weight.SemiBold);
        var plain = Sans(17);
        const string rest = " wants to control this PC";
        int restWidth = Draw.Measure(rest, plain).Width;
        int nameWidth = Math.Min(Draw.Measure(Who, bold).Width, right - left - restWidth);
        var line = new Rectangle(left, Px(24), nameWidth, Px(24));
        Draw.Text(g, Who, bold, line, Theme.Ink, Draw.LeftMiddle);
        Draw.Text(g, rest, plain, new Rectangle(line.Right, line.Top, right - line.Right, line.Height), Theme.Ink, Draw.LeftMiddle);
        Draw.Text(g, _where, Mono(13), Px(82, 50, 294, 18), Theme.Muted);

        Draw.Text(g, "They will see your screen and use your mouse and keyboard.", Sans(14), Px(24, 88, 352, 44), Theme.Muted, Draw.Wrapped);
        DrawButtons(g);
        int seconds = (int)Math.Ceiling(_request.TimeLeft.TotalSeconds);
        Draw.Text(g, $"Denies by itself in {seconds} s", Sans(13), Countdown, Theme.Muted, Draw.Centred);
    }

    private void Answer(bool accept)
    {
        if (_answered) return;
        _answered = true;
        if (accept) _request.Accept();
        else _request.Deny();
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Escape denies; nothing accepts by a key, so a stray Enter cannot let anyone in.
        if (e.KeyCode == Keys.Escape) Answer(accept: false);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        // Closed with Alt+F4: Deny. Closed because the request ended (answered elsewhere, timed out): nothing to do.
        if (e.CloseReason == CloseReason.UserClosing && !_answered && _request.TimeLeft > TimeSpan.Zero)
        {
            _answered = true;
            _request.Deny();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Windows 11 rounds the corners of a borderless window when asked; Windows 10 keeps them square.</summary>
    internal static void RoundCorners(IntPtr window)
    {
        const int CornerPreference = 33, Round = 2;
        int value = Round;
        try
        {
            DwmSetWindowAttribute(window, CornerPreference, ref value, sizeof(int));
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
