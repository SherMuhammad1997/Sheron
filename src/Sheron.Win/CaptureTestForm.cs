using System.Diagnostics;
using Sheron.Core;
using Sheron.Core.Capture;
using Sheron.Core.Diagnostics;
using Sheron.Win.Capture;

namespace Sheron.Win;

/// <summary>
/// Developer window for Plan step 3: shows what the screen capture does. Drawn on the form itself with no child
/// controls (CLAUDE.md › Windows UI). Open with <c>Sheron.Win.exe --capture-test</c>.
/// </summary>
internal sealed class CaptureTestForm : Form
{
    private static readonly Color Background = Color.FromArgb(0x0E, 0x14, 0x1B);
    private static readonly Color Muted = Color.FromArgb(0x9F, 0xB0, 0xC3);
    private static readonly Color Value = Color.FromArgb(0xE6, 0xED, 0xF3);
    private static readonly Color Accent = Color.FromArgb(0xF2, 0xA9, 0x3B);

    /// <summary>CPU is averaged over about 3 s: process times move in scheduler ticks, too coarse for half a second.</summary>
    private const int CpuSamples = 6;

    private readonly CaptureLoop _capture = new();
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly Queue<(long At, TimeSpan Cpu)> _cpuSamples = new();
    private readonly Font _titleFont = new("Segoe UI Semibold", 12f);
    private readonly Font _labelFont = new("Segoe UI", 10f);
    private readonly Font _valueFont = new("Consolas", 10.5f);
    private double _cpuPercent;
    private CaptureLoopSnapshot? _snapshot;

    public CaptureTestForm()
    {
        // Sizes are at 96 DPI; ResumeLayout scales them to the screen's DPI.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 420);
        Text = $"{AppInfo.Name} — capture test";
        BackColor = Background;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        ResumeLayout(false);

        _cpuSamples.Enqueue((Stopwatch.GetTimestamp(), _self.TotalProcessorTime));
        _refresh.Tick += (_, _) => RefreshFigures();
        _refresh.Start();
    }

    private void RefreshFigures()
    {
        _self.Refresh();
        long at = Stopwatch.GetTimestamp();
        var cpu = _self.TotalProcessorTime;
        var oldest = _cpuSamples.Peek();
        _cpuPercent = CpuUsage.Percent(cpu - oldest.Cpu, Stopwatch.GetElapsedTime(oldest.At, at), Environment.ProcessorCount);
        _cpuSamples.Enqueue((at, cpu));
        if (_cpuSamples.Count > CpuSamples) _cpuSamples.Dequeue();

        _snapshot = _capture.Snapshot();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        int left = LogicalToDeviceUnits(24);
        int valueLeft = LogicalToDeviceUnits(200);
        int y = LogicalToDeviceUnits(20);

        TextRenderer.DrawText(g, "Screen capture test · Plan step 3", _titleFont, new Point(left, y), Accent);
        y += _titleFont.Height + LogicalToDeviceUnits(14);

        int rowHeight = Math.Max(_labelFont.Height, _valueFont.Height) + LogicalToDeviceUnits(8);
        foreach (var (label, value) in Rows())
        {
            TextRenderer.DrawText(g, label, _labelFont, new Point(left, y), Muted);
            TextRenderer.DrawText(g, value, _valueFont, new Point(valueLeft, y), Value);
            y += rowHeight;
        }

        y += LogicalToDeviceUnits(8);
        var hint = new Rectangle(left, y, ClientSize.Width - 2 * left, ClientSize.Height - y);
        TextRenderer.DrawText(g, "Move a window or play a video to see the capture rate. Leave the screen still to check the CPU.",
            _labelFont, hint, Muted, TextFormatFlags.WordBreak);
    }

    private IEnumerable<(string Label, string Value)> Rows()
    {
        var s = _snapshot;
        if (s is null)
        {
            yield return ("Status", "Starting");
            yield break;
        }

        var stats = s.Stats;
        string status = s.Capturing && stats.FramesPerSecond == 0 ? "Capturing · screen still, nothing to send" : s.Status;
        yield return ("Status", status);
        yield return ("Monitor", $"{s.Monitor} · {s.Width} × {s.Height}");
        yield return ("Graphics", s.Adapter);
        yield return ("Capture rate", $"{stats.FramesPerSecond} fps");
        yield return ("Time per frame", $"avg {stats.AverageFrameMs:0.00} ms · max {stats.MaxFrameMs:0.00} ms");
        yield return ("Changed area", $"{stats.AverageChangedPercent:0.0} % per frame");
        yield return ("Pointer", s.Pointer.Visible ? $"at {s.Pointer.X}, {s.Pointer.Y}" : "hidden");
        yield return ("Pointer shape", s.Shape is { } shape ? $"{Describe(shape)} · changed {s.ShapeChanges}×" : "–");
        yield return ("Sheron CPU", $"{_cpuPercent:0.0} % of this PC (last 3 s)");
        yield return ("Reopened", $"{s.Reopens}×");
    }

    private static string Describe(PointerShape shape) => shape.Kind switch
    {
        // A monochrome shape holds two masks stacked, so the pointer is half as tall.
        PointerShapeKind.Monochrome => $"{shape.Width} × {shape.Height / 2} monochrome",
        PointerShapeKind.Color => $"{shape.Width} × {shape.Height} colour",
        PointerShapeKind.MaskedColor => $"{shape.Width} × {shape.Height} masked colour",
        _ => $"{shape.Width} × {shape.Height} kind {(int)shape.Kind}",
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Dispose();
            _capture.Dispose();
            _self.Dispose();
            _titleFont.Dispose();
            _labelFont.Dispose();
            _valueFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
