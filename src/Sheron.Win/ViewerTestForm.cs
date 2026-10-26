using System.Diagnostics;
using System.Net;
using Sheron.Core.Net;
using Sheron.Core.Security;
using Sheron.Core.Session;
using LinkState = Sheron.Core.Session.LinkState;
using Sheron.Win.Encode;
using Sheron.Win.View;

namespace Sheron.Win;

/// <summary>
/// Developer window for Plan step 8 on the viewer: connects to a remote PC on the same network by IP and shows its
/// screen, view only. Open with <c>Sheron.Win.exe --view &lt;IP&gt;[:port]</c>.
/// </summary>
internal sealed class ViewerTestForm : DevForm
{
    /// <summary>Frames held while the video format is not yet known (TRD › Connect by IP).</summary>
    private const int MaxEarlyFrames = 60;

    private readonly IPEndPoint _remote;
    private readonly bool _allowHardwareDecoder;
    private readonly DeviceKey _key = DeviceKey.Create();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _formatLock = new();
    private readonly List<AssembledFrame> _early = [];
    private (int Width, int Height)? _format;
    private VideoViewer? _viewer;
    private Link? _link;
    private LinkSnapshot? _linkSnapshot;
    private VideoViewerSnapshot? _viewerSnapshot;
    private (TimeSpan At, int Frames, long Bytes) _last;
    private double _framesPerSecond;
    private double _megabitsPerSecond;

    public ViewerTestForm(IPEndPoint remote, bool allowHardwareDecoder = true)
        : base($"viewing {remote} (developer)", $"Viewing {remote} · Plan step 8 · view only, same network",
            new Size(960, 720), headerHeight: 204, rowsPerColumn: 6, withVideo: true)
    {
        _remote = remote;
        _allowHardwareDecoder = allowHardwareDecoder;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var video = Video!;
        _viewer = new VideoViewer(video.Handle, video.ClientSize.Width, video.ClientSize.Height, _allowHardwareDecoder);
        video.SizeChanged += (_, _) => _viewer.Resize(video.ClientSize.Width, video.ClientSize.Height);
        _link = Link.Viewer(_key, _remote);
        _link.FrameReceived += OnFrame;
        _link.ControlReceived += OnControl;
        StartRefresh();
    }

    /// <summary>On the link's thread. Frames wait until the video format is known; from a keyframe on, at most 60.</summary>
    private void OnFrame(AssembledFrame frame)
    {
        lock (_formatLock)
        {
            if (_format is not { } format)
            {
                if (frame.KeyFrame) _early.Clear();
                if (_early.Count < MaxEarlyFrames) _early.Add(frame);
                return;
            }
            Show(frame, format);
        }
    }

    /// <summary>On the link's thread.</summary>
    private void OnControl(byte[] message)
    {
        switch (ControlMessage.TypeOf(message))
        {
            case ControlType.VideoFormat when ControlMessage.TryReadVideoFormat(message, out int width, out int height):
                lock (_formatLock)
                {
                    _format = (width, height);
                    foreach (var frame in _early) Show(frame, (width, height));
                    _early.Clear();
                }
                break;
            case ControlType.PointerShape when ControlMessage.TryReadPointerShape(message, out var shape):
                _viewer?.OnPointer(null, shape);
                break;
            case ControlType.PointerPosition when ControlMessage.TryReadPointerPosition(message, out var position):
                _viewer?.OnPointer(position, null);
                break;
        }
    }

    /// <summary>The viewer's timer starts when the frame arrived whole: the two PCs' clocks cannot be compared.</summary>
    private void Show(AssembledFrame frame, (int Width, int Height) format)
    {
        long arrived = Stopwatch.GetTimestamp();
        _viewer?.OnVideo(new EncodedFrame(frame.Data, frame.KeyFrame), format.Width, format.Height, arrived, arrived);
    }

    protected override void ReadFigures()
    {
        _linkSnapshot = _link?.Snapshot();
        _viewerSnapshot = _viewer?.Snapshot();
        if (_linkSnapshot is { } l)
        {
            var now = _clock.Elapsed;
            double seconds = (now - _last.At).TotalSeconds;
            _framesPerSecond = (l.FramesReceived - _last.Frames) / seconds;
            _megabitsPerSecond = (l.BytesReceived - _last.Bytes) * 8 / seconds / 1e6;
            _last = (now, l.FramesReceived, l.BytesReceived);
        }
    }

    protected override IEnumerable<(string Label, string Value, Color? Color)> Rows()
    {
        var l = _linkSnapshot;
        var v = _viewerSnapshot;
        if (l is null || v is null)
        {
            yield return ("Status", "Starting", null);
            yield break;
        }

        // Left column: who and what.
        string status = l.State switch
        {
            LinkState.Connecting => $"Connecting to {_remote} …",
            LinkState.Lost => "Connection lost (nothing heard for 5 s)",
            _ => v.Status,
        };
        yield return ("Status", status, l.State == LinkState.Lost ? Bad : null);
        yield return ("Remote PC", $"{_remote}", null);
        yield return ("Remote key", l.PeerFingerprint is { } fingerprint ? KeyFingerprint.Show(fingerprint) : "–", null);
        yield return ("This PC's key", KeyFingerprint.Show(_key.Fingerprint), null);
        yield return ("Decoder", v.HeldBack > 0 ? $"{v.Decoder} · held back {v.HeldBack}" : v.Decoder, null);
        yield return ("Picture", v.VideoWidth == 0 ? "–"
            : $"{v.VideoWidth} × {v.VideoHeight} shown at {v.Picture.Width} × {v.Picture.Height}", null);

        // Right column: how well.
        var t = v.Latency;
        yield return ("Frames /s", $"received {_framesPerSecond:0} · shown {t.FramesPerSecond}", null);
        yield return ("Arrived → shown", t.FramesPerSecond > 0 ? $"avg {t.AverageMs:0.0} · max {t.MaxMs:0.0} ms" : "–", null);
        yield return ("Lost frames", $"{l.LostFrames} · shards rebuilt {l.RebuiltShards} · refused {l.Refused}", l.LostFrames > 0 ? Bad : null);
        yield return ("Data", $"{_megabitsPerSecond:0.0} Mbit/s", null);
        yield return ("Round trip", l.RoundTrip is { } rtt ? $"{rtt.TotalMilliseconds:0.0} ms" : "–", null);
        yield return ("Sheron CPU", $"{CpuPercent:0.0} % of this PC (last 3 s)", null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // The link first: it hands frames to the viewer.
            _link?.Dispose();
            _viewer?.Dispose();
            _key.Dispose();
        }
        base.Dispose(disposing);
    }
}
