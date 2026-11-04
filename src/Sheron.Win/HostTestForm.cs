using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Sheron.Core.Capture;
using Sheron.Core.Net;
using Sheron.Core.Rendezvous;
using Sheron.Core.Security;
using Sheron.Core.Session;
using Sheron.Core.Video;
using LinkState = Sheron.Core.Session.LinkState;
using Sheron.Win.Capture;
using Sheron.Win.Encode;
using Sheron.Win.Input;
using Sheron.Win.Security;

namespace Sheron.Win;

/// <summary>
/// Developer window for Plan steps 8–9, 16 and 14 on the remote PC: lets one viewer see this screen and use its mouse
/// and keyboard, and shows who is viewing and when they last used the input. Viewers reach it by this PC's ID through
/// the Sheron server (TRD › Connect by ID), or by IP on the same network. A viewer needs the one-time password shown
/// here, and the person here must Accept (TRD › Passwords and consent); the fixed password lets in with nobody asked.
/// Sharing and control last only while this window is open; there is no hidden mode (CLAUDE.md › Hard rules).
/// Open with <c>Sheron.Win.exe --host [--port 47920]</c>.
/// </summary>
internal sealed class HostTestForm : DevForm
{
    /// <summary>Top of the request panel at 96 DPI, below the figures.</summary>
    private const int RequestTop = 278;
    private const int RequestHeight = 76;
    private static readonly Color Panel = Color.FromArgb(0x15, 0x1E, 0x28);
    private static readonly Color PanelHover = Color.FromArgb(0x1C, 0x28, 0x36);

    private readonly int _port;
    private readonly bool _allowHardwareEncoder;
    private readonly string _addresses = LocalAddresses();
    private readonly Font _requestFont = new("Segoe UI Semibold", 11f);
    private readonly Font _requestTextFont = new("Segoe UI", 10f);
    private DeviceKey? _key;
    private AccessGate? _gate;
    private HostAccess? _access;
    private ConsentRequest? _request;
    private Rectangle _acceptButton;
    private Rectangle _denyButton;
    private Rectangle? _hover;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly InputInjector _input = new();
    private Link? _link;
    private string? _linkError;
    // Automatic quality (Plan step 10).
    private readonly object _qualityLock = new();
    private QualityController _quality = new();
    // Connect by ID (Plan step 14): one UDP socket shared by the session and the connector.
    private readonly CancellationTokenSource _closing = new();
    private UdpTransport? _udp;
    private PacketMux? _mux;
    private volatile RendezvousClient? _client;
    private volatile HostRendezvous? _rendezvous;
    private volatile string _server = "–";
    private volatile bool _serverBad;
    private (uint Id, IPEndPoint From)? _lastCaller;
    private CaptureLoop? _capture;
    private CaptureLoopSnapshot? _captureSnapshot;
    private LinkSnapshot? _linkSnapshot;
    private (TimeSpan At, int Frames, long Bytes) _last;
    private double _framesPerSecond;
    private double _megabitsPerSecond;

    public HostTestForm(int port, bool allowHardwareEncoder = true)
        : base("sharing this screen (developer)", "Sharing this screen · a viewer needs the one-time password and your OK · close this window to stop",
            new Size(1040, 372), headerHeight: 372, rowsPerColumn: 9, withVideo: false)
    {
        _port = port;
        _allowHardwareEncoder = allowHardwareEncoder;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            var secrets = SecretStore.ThisPc();
            _key = DeviceKey.Import(secrets.DevicePrivateKey);
            _gate = new AccessGate(secrets.FixedPassword);
            _udp = new UdpTransport(_port);
            _mux = new PacketMux(_udp);
            _link = Link.Host(_key, _mux.Session);
            _access = new HostAccess(_link, _gate);
            // Capture runs only once the viewer is let in, so its first frame is a keyframe and nothing shows before.
            _access.LetIn += () => OnUiThread(StartSharing);
            _access.ConsentNeeded += request => OnUiThread(() => ShowRequest(request));
            _access.ConsentEnded += () => OnUiThread(HideRequest);
            // Nothing the viewer held stays pressed once it is gone (TRD › Input path › Stuck keys).
            // The viewer's reports drive automatic quality, once it is let in and sharing.
            _link.ReportReceived += report =>
            {
                if (_access?.IsLetIn == true) OnReport(report);
            };
            _link.Disconnected += () =>
            {
                _input.ReleaseAll();
                OnUiThread(StopSharing);
            };
            // Only messages from a viewer who is let in come through.
            _access.ControlReceived += message =>
            {
                if (ControlMessage.TypeOf(message) == ControlType.KeyFrameRequest) Volatile.Read(ref _capture)?.RequestKeyFrame();
                else _input.Apply(message);
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _linkError = $"Cannot read this PC's Sheron secrets: {error.Message}";
        }
        catch (SocketException error)
        {
            _linkError = $"Cannot use UDP port {_port}: {error.Message}";
        }
        StartRefresh();
        if (_mux is not null) _ = KeepRegistered(_key!, _mux);
    }

    /// <summary>
    /// Registers with the Sheron server and stays registered, so viewers find this PC by its ID; a lost link is tried
    /// again every 10 s (TRD › Connect by ID).
    /// </summary>
    private async Task KeepRegistered(DeviceKey key, PacketMux mux)
    {
        while (!_closing.IsCancellationRequested)
        {
            RendezvousClient? client = null;
            HostRendezvous? rendezvous = null;
            try
            {
                _server = $"connecting to {ServerConfig.Host} …";
                _serverBad = false;
                var lan = LocalNetwork.IPv4Addresses()
                    .Select(ip => new PeerAddress(AddressKind.Lan, new IPEndPoint(ip, _udp!.LocalEndPoint.Port))).ToList();
                client = await RendezvousClient.ConnectAsync(ServerConfig.Host, ServerConfig.Port, ServerConfig.Pin, key, lan, _closing.Token);
                _closing.Token.ThrowIfCancellationRequested();
                var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Disconnected += () => gone.TrySetResult();
                rendezvous = new HostRendezvous(client, mux, ServerConfig.UdpEndPoint);
                rendezvous.PathFound += (caller, _, from) => _lastCaller = (caller.Id, from);
                _client = client;
                _rendezvous = rendezvous;
                bool udp = await rendezvous.CheckAddressAsync(_closing.Token);
                _server = udp
                    ? $"registered · UDP works, seen as {rendezvous.Connector.PublicUdp}"
                    : "registered · UDP blocked here: viewers come over TCP 443";
                await gone.Task.WaitAsync(_closing.Token);
                _server = "link to the server lost · trying again in 10 s";
                _serverBad = true;
            }
            catch (AuthenticationException)
            {
                _server = "the server's pin does not match: not our server";
                _serverBad = true;
            }
            catch (Exception error) when (error is SocketException or IOException or InvalidDataException)
            {
                _server = $"cannot reach the server ({error.Message}) · trying again in 10 s";
                _serverBad = true;
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
            {
                return;   // the window closed
            }
            finally
            {
                _rendezvous = null;
                _client = null;
                rendezvous?.Dispose();
                if (client is not null) await client.DisposeAsync();
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _closing.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>A viewer with the one-time password: the person here is asked, in this window, which comes to the front.</summary>
    private void ShowRequest(ConsentRequest request)
    {
        _request = request;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        Invalidate();
    }

    private void HideRequest()
    {
        if (_access?.Pending is not null) return;
        _request = null;
        Invalidate();
    }

    private void StartSharing()
    {
        StopSharing();
        if (_link is null) return;
        // Each viewer starts at the best level; the viewer's reports and this PC's CPU move it (TRD › Automatic quality).
        lock (_qualityLock) _quality = new QualityController();
        _capture = new CaptureLoop(_allowHardwareEncoder, new LinkSender(_link));
    }

    /// <summary>On the link's thread, every 250 ms while a viewer is let in.</summary>
    private void OnReport(LinkReport report)
    {
        lock (_qualityLock)
            if (_quality.OnReport(report, _clock.Elapsed)) Volatile.Read(ref _capture)?.SetQuality(_quality.Current);
    }

    private void StopSharing()
    {
        _capture?.Dispose();
        _capture = null;
        _captureSnapshot = null;
    }

    protected override void ReadFigures()
    {
        // The CPU guard: Sheron's own share of this PC (TRD › Automatic quality).
        if (_capture is { } capture)
            lock (_qualityLock)
                if (_quality.OnCpu(CpuPercent, _clock.Elapsed)) capture.SetQuality(_quality.Current);
        _captureSnapshot = _capture?.Snapshot();
        _linkSnapshot = _link?.Snapshot();
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
        bool viewed = connected && _access?.IsLetIn == true;
        var locked = _gate?.LockedFor ?? TimeSpan.Zero;
        (string Text, Color? Color) status = _linkError is not null ? (_linkError, Bad)
            : locked > TimeSpan.Zero ? ($"Locked for {Math.Ceiling(locked.TotalSeconds)} s after 3 wrong passwords", Bad)
            : _request is not null ? ("A viewer is asking: Accept or Deny below", Accent)
            : viewed ? ("Being viewed", Accent)
            : connected ? ("A viewer connected · checking the password", null)
            : ($"Waiting for a viewer on UDP {_port}", null);

        var client = _client;
        var caller = _lastCaller;

        // Left column: who, and how to reach this PC.
        yield return ("Status", status.Text, status.Color);
        yield return ("This PC's ID", client is { Id: not 0 } ? RendezvousWire.Show(client.Id) : "–", Accent);
        yield return ("One-time password", _gate is null ? "–" : OneTimePassword.Show(_gate.OneTimePassword), Accent);
        yield return ("Server", _server, _serverBad ? Bad : null);
        yield return ("This PC", $"{_addresses} · port {_port}", null);
        yield return ("This PC's key", _key is null ? "–" : KeyFingerprint.Show(_key.Fingerprint), null);
        yield return ("Viewer", !connected ? "–"
            : caller is { } known && known.From.Equals(l!.Peer) ? $"ID {RendezvousWire.Show(known.Id)}" : "by IP address", null);
        yield return ("Viewer's key", connected && l!.PeerFingerprint is { } fingerprint ? KeyFingerprint.Show(fingerprint) : "–", null);
        yield return ("Path", connected ? ServerConfig.PathOf(l!.Peer) : "–", connected ? Accent : null);

        // Right column: how much.
        yield return ("Unattended", _gate?.FixedPassword is null ? "off · every viewer must be accepted here" : "on · the fixed password lets in with nobody asked", null);
        yield return ("Encoder", c?.Encoder ?? "–", null);
        yield return ("Graphics", c?.Adapter ?? "–", null);
        yield return ("Capture", c is null ? "–" : $"{c.Stats.FramesPerSecond} fps · {c.Stats.AverageFrameMs:0.00} ms", null);
        yield return ("Sent", viewed ? $"{_framesPerSecond:0} frames/s · {_megabitsPerSecond:0.0} Mbit/s" : "–", null);
        yield return ("Round trip", l?.RoundTrip is { } rtt && viewed ? $"{rtt.TotalMilliseconds:0} ms" : "–", null);
        string quality;
        lock (_qualityLock) quality = $"{_quality.Current}{(_quality.CpuCap > 0 ? " · CPU guard" : "")}";
        yield return ("Quality", !viewed ? "–"
            : l!.LastReport is { } r ? $"{quality} · loss {l.PacketLoss:P1} · queue {r.QueueMs} ms · jitter {r.JitterMs} ms" : quality, null);
        long lastInput = _input.LastInputAt;
        yield return ("Control", !viewed ? "–"
            : lastInput == 0 ? "not used yet"
            : $"last input {Stopwatch.GetElapsedTime(lastInput).TotalSeconds:0.0} s ago"
                + (_input.Blocked > 0 ? $" · {_input.Blocked} blocked by Windows" : ""), viewed && lastInput != 0 ? Accent : null);
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
            StopSharing();
            _closing.Cancel();
            _rendezvous?.Dispose();
            _client?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
            _access?.Dispose();
            _link?.Dispose();
            _mux?.Dispose();
            _udp?.Dispose();
            _input.ReleaseAll();
            _key?.Dispose();
            _requestFont.Dispose();
            _requestTextFont.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Hands what the capture loop makes to the link: the video format first, then frames and pointer changes.</summary>
    private sealed class LinkSender(Link link) : ICaptureSink
    {
        private (int Width, int Height) _format;

        public void OnVideo(EncodedFrame frame, int width, int height, long capturedAt, long encodedAt)
        {
            if ((width, height) != _format)
            {
                link.SendControl(ControlMessage.VideoFormat(width, height));
                _format = (width, height);
            }
            link.SendFrame(frame.Data, frame.KeyFrame, capturedAt);
        }

        public void OnPointer(PointerPosition? position, PointerShape? shape)
        {
            if (shape is not null) link.SendControl(ControlMessage.Pointer(shape));
            if (position is { } at) link.SendControl(ControlMessage.Pointer(at));
        }
    }
}
