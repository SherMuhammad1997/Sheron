using System.Diagnostics;
using Sheron.Core;
using Sheron.Core.Capture;
using Sheron.Core.Diagnostics;
using Sheron.Win.Capture;

namespace Sheron.Win;

/// <summary>
/// Developer window for Plan steps 3–4: shows what the screen capture and the encoder do. Drawn on the form itself
/// with no child controls (CLAUDE.md › Windows UI). Open with <c>Sheron.Win.exe --capture-test</c>.
/// </summary>
internal sealed class CaptureTestForm : Form
{
    private static readonly Color Background = Color.FromArgb(0x0E, 0x14, 0x1B);
    private static readonly Color Muted = Color.FromArgb(0x9F, 0xB0, 0xC3);
    private static readonly Color Value = Color.FromArgb(0xE6, 0xED, 0xF3);
    private static readonly Color Accent = Color.FromArgb(0xF2, 0xA9, 0x3B);
    private const TextFormatFlags Wrapped = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;

    /// <summary>CPU is averaged over about 3 s: process times move in scheduler ticks, too coarse for half a second.</summary>
    private const int CpuSamples = 6;

    private readonly CaptureLoop _capture;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 500 };
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly Queue<(long At, TimeSpan Cpu)> _cpuSamples = new();
    private readonly Font _titleFont = new("Segoe UI Semibold", 12f);
    private readonly Font _labelFont = new("Segoe UI", 10f);
    private readonly Font _valueFont = new("Consolas", 10.5f);
    private double _cpuPercent;
    private CaptureLoopSnapshot? _snapshot;

    /// <param name="allowHardwareEncoder">False tests the software encoder (<c>--software-encoder</c>).</param>
    public CaptureTestForm(bool allowHardwareEncoder = true)
    {
        // Sizes are at 96 DPI; ResumeLayout scales them to the screen's DPI.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(720, 600);
        Text = $"{AppInfo.Name} — capture test";
        BackColor = Background;
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        ResumeLayout(false);

        _capture = new CaptureLoop(allowHardwareEncoder);
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
        int valueWidth = ClientSize.Width - valueLeft - left;
        int gap = LogicalToDeviceUnits(8);
        int y = LogicalToDeviceUnits(20);

        TextRenderer.DrawText(g, "Capture and encoder test · Plan steps 3–4", _titleFont, new Point(left, y), Accent);
        y += _titleFont.Height + LogicalToDeviceUnits(14);

        foreach (var (label, value) in Rows())
        {
            // Long values (encoder names, reasons) wrap inside the value column.
            int height = TextRenderer.MeasureText(g, value, _valueFont, new Size(valueWidth, int.MaxValue), Wrapped).Height;
            TextRenderer.DrawText(g, label, _labelFont, new Point(left, y), Muted);
            TextRenderer.DrawText(g, value, _valueFont, new Rectangle(valueLeft, y, valueWidth, height), Value, Wrapped);
            y += Math.Max(height, _labelFont.Height) + gap;
        }

        y += gap;
        var hint = new Rectangle(left, y, ClientSize.Width - 2 * left, Math.Max(0, ClientSize.Height - y));
        TextRenderer.DrawText(g, "Move a window or play a video to see the capture and encode rates. Leave the screen still to check the CPU.",
            _labelFont, hint, Muted, Wrapped);
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
        yield return ("Capture time", $"avg {stats.AverageFrameMs:0.00} ms · max {stats.MaxFrameMs:0.00} ms");
        yield return ("Changed area", $"{stats.AverageChangedPercent:0.0} % per frame");
        yield return ("Pointer", s.Pointer.Visible ? $"at {s.Pointer.X}, {s.Pointer.Y}" : "hidden");
        yield return ("Pointer shape", s.Shape is { } shape ? $"{Describe(shape)} · changed {s.ShapeChanges}×" : "–");

        var encode = s.Encode;
        yield return ("Encoder", s.Encoder);
        yield return ("Encoder settings", s.EncoderSettings);
        yield return ("Encode time", $"avg {encode.AverageEncodeMs:0.00} ms · max {encode.MaxEncodeMs:0.00} ms · inside encoder avg {encode.AverageEncoderOwnMs:0.00} ms");
        yield return ("Video", $"{encode.FramesPerSecond} fps · {encode.KilobitsPerSecond} kbit/s · {encode.KeyFrames} keyframes");
        yield return ("Held back", $"{s.HeldBack} frames");
        yield return ("Timer", s.FineTimer ? "1 ms" : "Windows default (15.6 ms)");

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
