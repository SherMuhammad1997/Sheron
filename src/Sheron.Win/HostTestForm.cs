using System.Diagnostics;
using Sheron.Core.Capture;
using Sheron.Core.Rendezvous;
using Sheron.Core.Security;
using Sheron.Core.Session;
using LinkState = Sheron.Core.Session.LinkState;
using Sheron.Win.Capture;

namespace Sheron.Win;

/// <summary>
/// Developer window for Plan steps 8–9, 16 and 14 on the remote PC: lets one viewer see this screen and use its mouse
/// and keyboard, and shows who is viewing and when they last used the input. Viewers reach it by this PC's ID through
/// the Sheron server (TRD › Connect by ID), or by IP on the same network. The person here must Accept each viewer
/// (TRD › Passwords and consent; PRD decision 4 Nov); the fixed password lets in with nobody asked. Sharing and control
/// last only while this window is open; there is no hidden mode (CLAUDE.md › Hard rules).
/// Open with <c>Sheron.Win.exe --host [--port 47920]</c>. The work is <see cref="ThisPc"/>'s, as in the home window
/// (Plan step 15a); this window only shows its figures.
/// </summary>
internal sealed class HostTestForm : DevForm
{
    /// <summary>Top of the request panel at 96 DPI, below the figures.</summary>
    private const int RequestTop = 254;
    private const int RequestHeight = 76;
    private static readonly Color Panel = Color.FromArgb(0x15, 0x1E, 0x28);
    private static readonly Color PanelHover = Color.FromArgb(0x1C, 0x28, 0x36);

    private readonly ThisPc _pc;
    private readonly string _addresses = LocalAddresses();
    private readonly Font _requestFont = new("Segoe UI Semibold", 11f);
    private readonly Font _requestTextFont = new("Segoe UI", 10f);
    private ConsentRequest? _request;
    private Rectangle _acceptButton;
    private Rectangle _denyButton;
    private Rectangle? _hover;
    private CaptureLoopSnapshot? _captureSnapshot;
    private LinkSnapshot? _linkSnapshot;
    private (TimeSpan At, int Frames, long Bytes) _last;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _framesPerSecond;
    private double _megabitsPerSecond;

    public HostTestForm(int port, bool allowHardwareEncoder = true)
        : base("sharing this screen (developer)", "Sharing this screen · a viewer needs this PC's ID and your OK · close this window to stop",
            new Size(1040, 348), headerHeight: 348, rowsPerColumn: 9, withVideo: false)
    {
        _pc = new ThisPc(port, allowHardwareEncoder);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _pc.ConsentNeeded += ShowRequest;
        _pc.ConsentEnded += HideRequest;
        _pc.Start();
        StartRefresh();
    }

    /// <summary>A viewer asks to connect: the person here is asked, in this window, which comes to the front.</summary>
    private void ShowRequest(ConsentRequest request)
    {
        _request = request;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        Invalidate();
    }

    private void HideRequest()
    {
        if (_pc.Pending is not null) return;
        _request = null;
        Invalidate();
    }

    protected override void ReadFigures()
    {
        _captureSnapshot = _pc.CaptureSnapshot();
        _linkSnapshot = _pc.LinkSnapshot();
        if (_linkSnapshot is { } l)
        {
            var now = _clock.Elapsed;
            double seconds = (now - _last.At).TotalSeconds;
            _framesPerSecond = (l.FramesSent - _last.Frames) / seconds;
            _megabitsPerSecond = (l.BytesSent - _last.Bytes) * 8 / seconds / 1e6;
            _last = (now, l.FramesSent, l.BytesSent);
        }
    }

    protected override IEnumerable<(string Label, string Value, Color? Color)> Rows()
    {
        var l = _linkSnapshot;
        var c = _captureSnapshot;
        bool connected = l?.State == LinkState.Connected;
        bool viewed = connected && _pc.IsShared;
        var locked = _pc.Gate?.LockedFor ?? TimeSpan.Zero;
        (string Text, Color? Color) status = _pc.Problem is { } problem ? (problem, Bad)
            : locked > TimeSpan.Zero ? ($"Locked for {Math.Ceiling(locked.TotalSeconds)} s after 3 wrong passwords", Bad)
            : _request is not null ? ("A viewer is asking: Accept or Deny below", Accent)
            : viewed ? ("Being viewed", Accent)
            : connected ? ("A viewer connected · checking the password", null)
            : ($"Waiting for a viewer on UDP {_pc.Port}", null);

        // Left column: who, and how to reach this PC.
        yield return ("Status", status.Text, status.Color);
        yield return ("This PC's ID", _pc.Id is { } id ? RendezvousWire.Show(id) : "–", Accent);
        yield return ("Server", _pc.ServerDetail, _pc.Server is ServerState.Offline or ServerState.WrongServer ? Bad : null);
        yield return ("This PC", $"{_addresses} · port {_pc.Port}", null);
        yield return ("This PC's key", _pc.Key is { } key ? KeyFingerprint.Show(key.Fingerprint) : "–", null);
        yield return ("Viewer", connected ? $"{_pc.Viewer.Name} · {_pc.Viewer.Where}" : "–", null);
        yield return ("Viewer's key", connected && l!.PeerFingerprint is { } fingerprint ? KeyFingerprint.Show(fingerprint) : "–", null);
        yield return ("Path", connected ? ServerConfig.PathOf(l!.Peer) : "–", connected ? Accent : null);

        // Right column: how much.
        yield return ("Unattended", _pc.Gate?.FixedPassword is null ? "off · every viewer must be accepted here" : "on · the fixed password lets in with nobody asked", null);
        yield return ("Encoder", c?.Encoder ?? "–", null);
        yield return ("Graphics", c?.Adapter ?? "–", null);
        yield return ("Capture", c is null ? "–" : $"{c.Stats.FramesPerSecond} fps · {c.Stats.AverageFrameMs:0.00} ms", null);
        yield return ("Sent", viewed ? $"{_framesPerSecond:0} frames/s · {_megabitsPerSecond:0.0} Mbit/s" : "–", null);
        yield return ("Round trip", l?.RoundTrip is { } rtt && viewed ? $"{rtt.TotalMilliseconds:0} ms" : "–", null);
        string quality = _pc.Quality();
        yield return ("Quality", !viewed ? "–"
            : l!.LastReport is { } r ? $"{quality} · loss {l.PacketLoss:P1} · queue {r.QueueMs} ms · jitter {r.JitterMs} ms" : quality, null);
        long lastInput = _pc.Input.LastInputAt;
        yield return ("Control", !viewed ? "–"
            : lastInput == 0 ? "not used yet"
            : $"last input {Stopwatch.GetElapsedTime(lastInput).TotalSeconds:0.0} s ago"
                + (_pc.Input.Blocked > 0 ? $" · {_pc.Input.Blocked} blocked by Windows" : ""), viewed && lastInput != 0 ? Accent : null);
        yield return ("Sheron CPU", $"{CpuPercent:0.0} % of this PC (last 3 s)", null);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int margin = LogicalToDeviceUnits(20);
        int buttonWidth = LogicalToDeviceUnits(110);
        int buttonHeight = LogicalToDeviceUnits(36);
        int top = LogicalToDeviceUnits(RequestTop) + (LogicalToDeviceUnits(RequestHeight) - buttonHeight) / 2;
        int right = ClientSize.Width - margin - LogicalToDeviceUnits(16);
        _denyButton = new Rectangle(right - buttonWidth, top, buttonWidth, buttonHeight);
        _acceptButton = new Rectangle(_denyButton.Left - LogicalToDeviceUnits(12) - buttonWidth, top, buttonWidth, buttonHeight);
    }

    /// <summary>The request, drawn below the figures (CLAUDE.md › Windows UI: no extra native controls).</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        int margin = LogicalToDeviceUnits(20);
        var panel = new Rectangle(margin, LogicalToDeviceUnits(RequestTop), ClientSize.Width - 2 * margin, LogicalToDeviceUnits(RequestHeight));
        if (_request is not { } request)
        {
            TextRenderer.DrawText(g, "Before anyone sees this screen, you are asked here: Accept or Deny.", _requestTextFont,
                new Point(panel.Left, panel.Top + LogicalToDeviceUnits(8)), Muted);
            return;
        }

        using (var fill = new SolidBrush(Panel)) g.FillRectangle(fill, panel);
        using (var border = new Pen(Accent, LogicalToDeviceUnits(2))) g.DrawRectangle(border, panel);
        int textLeft = panel.Left + LogicalToDeviceUnits(16);
        TextRenderer.DrawText(g, "A viewer asks to see and control this screen", _requestFont,
            new Point(textLeft, panel.Top + LogicalToDeviceUnits(12)), Value);
        string who = request.ViewerFingerprint is { } key ? $"{request.Viewer} · key {KeyFingerprint.Show(key)}" : $"{request.Viewer}";
        TextRenderer.DrawText(g, $"{who} · refused by itself in {Math.Ceiling(request.TimeLeft.TotalSeconds)} s", _requestTextFont,
            new Rectangle(textLeft, panel.Top + LogicalToDeviceUnits(40), _acceptButton.Left - textLeft - LogicalToDeviceUnits(12), LogicalToDeviceUnits(24)),
            Muted, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        DrawButton(g, _acceptButton, "Accept", Good);
        DrawButton(g, _denyButton, "Deny", Bad);
    }

    private void DrawButton(Graphics g, Rectangle area, string label, Color edge)
    {
        using (var fill = new SolidBrush(_hover == area ? PanelHover : Background)) g.FillRectangle(fill, area);
        using (var border = new Pen(edge, LogicalToDeviceUnits(2))) g.DrawRectangle(border, area);
        TextRenderer.DrawText(g, label, _requestFont, area, Value, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Rectangle? over = _request is null ? null
            : _acceptButton.Contains(e.Location) ? _acceptButton
            : _denyButton.Contains(e.Location) ? _denyButton
            : null;
        if (over == _hover) return;
        _hover = over;
        Cursor = over is null ? Cursors.Default : Cursors.Hand;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left || _request is not { } request) return;
        if (_acceptButton.Contains(e.Location)) request.Accept();
        else if (_denyButton.Contains(e.Location)) request.Deny();
    }

    /// <summary>This PC's IPv4 addresses for the viewer to type, the network other PCs are on first.</summary>
    private static string LocalAddresses()
    {
        var addresses = LocalNetwork.IPv4Addresses();
        return addresses.Count == 0 ? "no network" : string.Join(", ", addresses);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pc.Dispose();
            _requestFont.Dispose();
            _requestTextFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
