using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Sheron.Core.Capture;
using Sheron.Core.Security;
using Sheron.Core.Session;
using LinkState = Sheron.Core.Session.LinkState;
using Sheron.Win.Capture;
using Sheron.Win.Encode;

namespace Sheron.Win;

/// <summary>
/// Developer window for Plan step 8 on the remote PC: lets one viewer on the same network see this screen, and shows
/// who is viewing. Sharing lasts only while this window is open; there is no hidden mode (CLAUDE.md › Hard rules).
/// Open with <c>Sheron.Win.exe --host [--port 47920]</c>.
/// </summary>
internal sealed class HostTestForm : DevForm
{
    private readonly int _port;
    private readonly bool _allowHardwareEncoder;
    private readonly DeviceKey _key = DeviceKey.Create();
    private readonly string _addresses = LocalAddresses();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Link? _link;
    private string? _linkError;
    private CaptureLoop? _capture;
    private CaptureLoopSnapshot? _captureSnapshot;
    private LinkSnapshot? _linkSnapshot;
    private (TimeSpan At, int Frames, long Bytes) _last;
    private double _framesPerSecond;
    private double _megabitsPerSecond;

    public HostTestForm(int port, bool allowHardwareEncoder = true)
        : base("sharing this screen (developer)", "Sharing this screen · Plan step 8 · close this window to stop sharing",
            new Size(960, 260), headerHeight: 260, rowsPerColumn: 6, withVideo: false)
    {
        _port = port;
        _allowHardwareEncoder = allowHardwareEncoder;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            _link = Link.Host(_key, _port);
            // Capture runs only while a viewer is connected, so each viewer's first frame is a keyframe.
            _link.Connected += () => OnUiThread(StartSharing);
            _link.Disconnected += () => OnUiThread(StopSharing);
        }
        catch (SocketException error)
        {
            _linkError = $"Cannot use UDP port {_port}: {error.Message}";
        }
        StartRefresh();
    }

    private void StartSharing()
    {
        StopSharing();
        if (_link is null) return;
        _capture = new CaptureLoop(_allowHardwareEncoder, new LinkSender(_link));
    }

    private void StopSharing()
    {
        _capture?.Dispose();
        _capture = null;
        _captureSnapshot = null;
    }

    protected override void ReadFigures()
    {
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
        bool viewed = l?.State == LinkState.Connected;
        string status = _linkError
            ?? (viewed ? "Being viewed" : $"Waiting for a viewer on UDP {_port}");

        // Left column: who.
        yield return ("Status", status, _linkError is null ? (viewed ? Accent : null) : Bad);
        yield return ("This PC", $"{_addresses} · port {_port}", null);
        yield return ("This PC's key", KeyFingerprint.Show(_key.Fingerprint), null);
        yield return ("Viewer", viewed ? $"{l!.Peer}" : "–", null);
        yield return ("Viewer's key", viewed && l!.PeerFingerprint is { } fingerprint ? KeyFingerprint.Show(fingerprint) : "–", null);
        yield return ("Encoder", c?.Encoder ?? "–", null);

        // Right column: how much.
        yield return ("Graphics", c?.Adapter ?? "–", null);
        yield return ("Capture", c is null ? "–" : $"{c.Stats.FramesPerSecond} fps · {c.Stats.AverageFrameMs:0.00} ms", null);
        yield return ("Sent", viewed ? $"{_framesPerSecond:0} frames/s · {_megabitsPerSecond:0.0} Mbit/s" : "–", null);
        yield return ("Round trip", l?.RoundTrip is { } rtt && viewed ? $"{rtt.TotalMilliseconds:0} ms" : "–", null);
        yield return ("Resends", l is null ? "–" : $"{l.Resends} · refused packets {l.Refused}", null);
        yield return ("Sheron CPU", $"{CpuPercent:0.0} % of this PC (last 3 s)", null);
    }

    /// <summary>This PC's IPv4 addresses on networks that are up, for the viewer to type.</summary>
    private static string LocalAddresses()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address.ToString())
            .ToList();
        return addresses.Count == 0 ? "no network" : string.Join(", ", addresses);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopSharing();
            _link?.Dispose();
            _key.Dispose();
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
            link.SendFrame(frame.Data, frame.KeyFrame);
        }

        public void OnPointer(PointerPosition? position, PointerShape? shape)
        {
            if (shape is not null) link.SendControl(ControlMessage.Pointer(shape));
            if (position is { } at) link.SendControl(ControlMessage.Pointer(at));
        }
    }
}
