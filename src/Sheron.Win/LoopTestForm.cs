using Sheron.Win.Capture;
using Sheron.Win.View;

namespace Sheron.Win;

/// <summary>
/// Developer window for Plan step 5: capture → encode → decode → draw on one PC, with the capture-to-display timer.
/// Open with <c>Sheron.Win.exe --loop-test</c>.
/// </summary>
internal sealed class LoopTestForm : DevForm
{
    /// <summary>Gate 1 (Plan step 5): capture to display, averaged over the last 5 s.</summary>
    private const double TargetMs = 25;

    private readonly bool _allowHardwareEncoder;
    private readonly bool _allowHardwareDecoder;
    private VideoViewer? _viewer;
    private CaptureLoop? _capture;
    private CaptureLoopSnapshot? _captureSnapshot;
    private VideoViewerSnapshot? _viewerSnapshot;

    /// <param name="allowHardwareEncoder">False tests the software encoder (<c>--software-encoder</c>).</param>
    /// <param name="allowHardwareDecoder">False tests the software decoder (<c>--software-decoder</c>).</param>
    public LoopTestForm(bool allowHardwareEncoder = true, bool allowHardwareDecoder = true)
        : base("loop test", "Loop test · Plan step 5 · capture → encode → decode → display on this PC",
            new Size(960, 720), headerHeight: 252, rowsPerColumn: 8, withVideo: true)
    {
        _allowHardwareEncoder = allowHardwareEncoder;
        _allowHardwareDecoder = allowHardwareDecoder;
    }

    /// <summary>Starts once the video window exists: the viewer first, so it gets the very first keyframe.</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var video = Video!;
        _viewer = new VideoViewer(video.Handle, video.ClientSize.Width, video.ClientSize.Height, _allowHardwareDecoder);
        video.SizeChanged += (_, _) => _viewer.Resize(video.ClientSize.Width, video.ClientSize.Height);
        _capture = new CaptureLoop(_allowHardwareEncoder, _viewer);
        StartRefresh();
    }

    protected override void ReadFigures()
    {
        _captureSnapshot = _capture?.Snapshot();
        _viewerSnapshot = _viewer?.Snapshot();
    }

    protected override IEnumerable<(string Label, string Value, Color? Color)> Rows()
    {
        var c = _captureSnapshot;
        var v = _viewerSnapshot;
        if (c is null || v is null)
        {
            yield return ("Status", "Starting", null);
            yield break;
        }

        // Left column: what runs.
        string status = !c.Capturing ? c.Status : v.Status;
        yield return ("Status", status, null);
        yield return ("Graphics", c.Adapter == v.Adapter ? c.Adapter : $"capture {c.Adapter} · viewer {v.Adapter}", null);
        yield return ("Encoder", c.Encoder, null);
        yield return ("Decoder", v.HeldBack > 0 ? $"{v.Decoder} · held back {v.HeldBack}" : v.Decoder, null);
        yield return ("Picture", v.VideoWidth == 0 ? "–"
            : $"{v.VideoWidth} × {v.VideoHeight} shown at {v.Picture.Width} × {v.Picture.Height}", null);
        yield return ("Pointer", c.Shape is { } shape
            ? $"{CaptureTestForm.Describe(shape)} · {(c.Pointer.Visible ? $"at {c.Pointer.X}, {c.Pointer.Y}" : "hidden")}"
            : "–", null);
        yield return ("Timer", c.FineTimer ? "1 ms" : "Windows default (15.6 ms)", null);
        yield return ("Sheron CPU", $"{CpuPercent:0.0} % of this PC (last 3 s)", null);

        // Right column: how fast, averages over the last 5 s.
        var l = v.Latency;
        bool timed = l.FramesPerSecond > 0;
        yield return ("Capture → screen", timed ? $"avg {l.AverageMs:0.0} · max {l.MaxMs:0.0} ms · goal ≤ {TargetMs:0}" : "– (move a window)",
            timed ? (l.AverageMs <= TargetMs ? Good : Bad) : null);
        yield return ("Encode", timed ? $"{l.EncodeMs:0.0} ms capture → encoded" : "–", null);
        yield return ("Decode", timed ? $"{l.DecodeMs:0.0} ms encoded → decoded" : "–", null);
        yield return ("Draw", timed ? $"{l.DrawMs:0.0} ms decoded → drawn" : "–", null);
        yield return ("Present", timed ? $"{l.PresentMs:0.0} ms handing it to Windows" : "–", null);
        yield return ("Screen wait", timed ? $"{l.ScreenMs:0.0} ms presented → shown" : "–", null);
        yield return ("Screen", v.Screen, null);
        yield return ("Frames /s", $"captured {c.Stats.FramesPerSecond} · shown {l.FramesPerSecond} · untimed {l.Untimed / 5}", null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Capture first: it hands frames to the viewer.
            _capture?.Dispose();
            _viewer?.Dispose();
        }
        base.Dispose(disposing);
    }
}
