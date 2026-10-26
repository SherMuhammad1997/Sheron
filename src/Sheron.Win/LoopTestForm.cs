using Sheron.Core;
using Sheron.Win.Capture;
using Sheron.Win.View;

namespace Sheron.Win;

/// <summary>
/// Developer window for Plan step 5: capture → encode → decode → draw on one PC, with the capture-to-display timer.
/// The figures are drawn on the form; the video goes into one child window (CLAUDE.md › Windows UI). Open with
/// <c>Sheron.Win.exe --loop-test</c>.
/// </summary>
internal sealed class LoopTestForm : Form
{
    private static readonly Color Background = Color.FromArgb(0x0E, 0x14, 0x1B);
    private static readonly Color Muted = Color.FromArgb(0x9F, 0xB0, 0xC3);
    private static readonly Color Value = Color.FromArgb(0xE6, 0xED, 0xF3);
    private static readonly Color Accent = Color.FromArgb(0xF2, 0xA9, 0x3B);
    private static readonly Color Good = Color.FromArgb(0x3F, 0xB8, 0x71);
    private static readonly Color Bad = Color.FromArgb(0xE5, 0x53, 0x4B);
    private const TextFormatFlags OneLine = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

    /// <summary>Gate 1 (Plan step 5): capture to display, averaged over the last 5 s.</summary>
    private const double TargetMs = 25;

    /// <summary>Height of the figures above the video, at 96 DPI.</summary>
    private const int HeaderHeight = 196;
    private const int RowsPerColumn = 6;

    private readonly bool _allowHardwareEncoder;
    private readonly bool _allowHardwareDecoder;
    private readonly VideoPanel _video = new();
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private readonly CpuMeter _cpu = new();
    private readonly Font _titleFont = new("Segoe UI Semibold", 12f);
    private readonly Font _labelFont = new("Segoe UI", 10f);
    private readonly Font _valueFont = new("Consolas", 10.5f);
    private LoopViewer? _viewer;
    private CaptureLoop? _capture;
    private CaptureLoopSnapshot? _captureSnapshot;
    private LoopViewerSnapshot? _viewerSnapshot;
    private double _cpuPercent;

    /// <param name="allowHardwareEncoder">False tests the software encoder (<c>--software-encoder</c>).</param>
    /// <param name="allowHardwareDecoder">False tests the software decoder (<c>--software-decoder</c>).</param>
    public LoopTestForm(bool allowHardwareEncoder = true, bool allowHardwareDecoder = true)
    {
        _allowHardwareEncoder = allowHardwareEncoder;
        _allowHardwareDecoder = allowHardwareDecoder;

        // Sizes are at 96 DPI; ResumeLayout scales them to the screen's DPI.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(960, 690);
        Text = $"{AppInfo.Name} — loop test";
        BackColor = Background;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        Controls.Add(_video);
        ResumeLayout(false);

        _refresh.Tick += (_, _) => RefreshFigures();
    }

    /// <summary>Starts once the video window exists: the viewer first, so it gets the very first keyframe.</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _viewer = new LoopViewer(_video.Handle, _video.ClientSize.Width, _video.ClientSize.Height, _allowHardwareDecoder);
        _video.SizeChanged += (_, _) => _viewer.Resize(_video.ClientSize.Width, _video.ClientSize.Height);
        _capture = new CaptureLoop(_allowHardwareEncoder, _viewer);
        _refresh.Start();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int top = LogicalToDeviceUnits(HeaderHeight);
        _video.Bounds = new Rectangle(0, top, ClientSize.Width, Math.Max(1, ClientSize.Height - top));
    }

    private void RefreshFigures()
    {
        _cpuPercent = _cpu.Read();
        _captureSnapshot = _capture?.Snapshot();
        _viewerSnapshot = _viewer?.Snapshot();
        Invalidate(new Rectangle(0, 0, ClientSize.Width, LogicalToDeviceUnits(HeaderHeight)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        int margin = LogicalToDeviceUnits(20);
        int labelWidth = LogicalToDeviceUnits(118);
        int rowHeight = LogicalToDeviceUnits(24);
        int columnWidth = (ClientSize.Width - 2 * margin) / 2;
        int y = LogicalToDeviceUnits(14);

        TextRenderer.DrawText(g, "Loop test · Plan step 5 · capture → encode → decode → display on this PC", _titleFont,
            new Point(margin, y), Accent);
        y += _titleFont.Height + LogicalToDeviceUnits(12);

        var rows = Rows().ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var (label, value, color) = rows[i];
            int left = margin + (i / RowsPerColumn) * columnWidth;
            int top = y + (i % RowsPerColumn) * rowHeight;
            int valueWidth = columnWidth - labelWidth - LogicalToDeviceUnits(12);
            TextRenderer.DrawText(g, label, _labelFont, new Rectangle(left, top, labelWidth, rowHeight), Muted, OneLine);
            TextRenderer.DrawText(g, value, _valueFont, new Rectangle(left + labelWidth, top, valueWidth, rowHeight),
                color ?? Value, OneLine);
        }
    }

    private IEnumerable<(string Label, string Value, Color? Color)> Rows()
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

        // Right column: how fast.
        var latency = v.Latency;
        if (latency.FramesPerSecond == 0)
            yield return ("Capture → screen", "– (move a window to make frames)", null);
        else
            yield return ("Capture → screen", $"avg {latency.AverageMs:0.0} ms · max {latency.MaxMs:0.0} ms · target ≤ {TargetMs:0} ms",
                latency.AverageMs <= TargetMs ? Good : Bad);
        yield return ("Parts (avg)", latency.FramesPerSecond == 0 ? "–"
            : $"encode {latency.EncodeMs:0.0} · decode {latency.DecodeMs:0.0} · draw {latency.DrawMs:0.0} · screen {latency.ScreenMs:0.0} ms", null);
        yield return ("Frames", $"captured {c.Stats.FramesPerSecond} fps · shown {latency.FramesPerSecond} fps · untimed {latency.Untimed} (last 5 s)", null);
        yield return ("Screen", v.Screen, null);
        yield return ("Timer", c.FineTimer ? "1 ms" : "Windows default (15.6 ms)", null);
        yield return ("Sheron CPU", $"{_cpuPercent:0.0} % of this PC (last 3 s)", null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Dispose();
            // Capture first: it hands frames to the viewer.
            _capture?.Dispose();
            _viewer?.Dispose();
            _cpu.Dispose();
            _titleFont.Dispose();
            _labelFont.Dispose();
            _valueFont.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>The child window the video is drawn into; Windows Forms paints nothing on it.</summary>
    private sealed class VideoPanel : Control
    {
        public VideoPanel()
        {
            SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            SetStyle(ControlStyles.Selectable, false);
        }
    }
}
