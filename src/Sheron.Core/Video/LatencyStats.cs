namespace Sheron.Core.Video;

/// <summary>
/// When one frame passed each point on its way from capture to the screen, in ticks of one steady clock (on Windows
/// the performance counter, which <c>Stopwatch</c> and DXGI's frame statistics both use). 0 means not yet.
/// </summary>
public readonly record struct FrameTimes(long Captured, long Encoded, long Decoded, long Drawn, long Presented, long Displayed = 0);

/// <summary>
/// Capture-to-display figures for the last <see cref="LatencyStats.Window"/>: frames shown per second, the average
/// and longest total, and the average of each part (<paramref name="PresentMs"/> is the time inside the call that hands
/// the frame to Windows). <paramref name="Untimed"/> counts frames Windows did not say
/// when it showed (replaced by a newer frame before the screen refresh, or not reported).
/// </summary>
public readonly record struct LatencySnapshot(int FramesPerSecond, double AverageMs, double MaxMs,
    double EncodeMs, double DecodeMs, double DrawMs, double PresentMs, double ScreenMs, int Untimed);

/// <summary>
/// Rolling capture-to-display timer (Plan step 5, TRD › Viewer). The viewer thread adds frames; the test window reads
/// snapshots, so every call is locked.
/// </summary>
public sealed class LatencyStats
{
    /// <summary>Long enough for a steady average: one second swings with the screen refresh.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly double _ticksPerMs;
    private readonly Queue<(TimeSpan At, FrameTimes Times)> _shown = new();
    private readonly Queue<TimeSpan> _untimed = new();
    private readonly object _lock = new();

    /// <param name="ticksPerSecond">The clock's rate, <c>Stopwatch.Frequency</c> on Windows.</param>
    public LatencyStats(long ticksPerSecond)
    {
        _ticksPerMs = ticksPerSecond / 1000.0;
    }

    /// <summary>A frame that reached the screen; <paramref name="times"/> has every point filled in.</summary>
    public void AddShown(TimeSpan at, FrameTimes times)
    {
        lock (_lock)
        {
            _shown.Enqueue((at, times));
            DropOlderThanWindow(at);
        }
    }

    public void AddUntimed(TimeSpan at)
    {
        lock (_lock)
        {
            _untimed.Enqueue(at);
            DropOlderThanWindow(at);
        }
    }

    public LatencySnapshot Snapshot(TimeSpan now)
    {
        lock (_lock)
        {
            DropOlderThanWindow(now);
            int count = _shown.Count;
            if (count == 0) return new LatencySnapshot(0, 0, 0, 0, 0, 0, 0, 0, _untimed.Count);

            double total = 0, max = 0, encode = 0, decode = 0, draw = 0, present = 0, screen = 0;
            foreach (var (_, t) in _shown)
            {
                double all = Ms(t.Captured, t.Displayed);
                total += all;
                max = Math.Max(max, all);
                encode += Ms(t.Captured, t.Encoded);
                decode += Ms(t.Encoded, t.Decoded);
                draw += Ms(t.Decoded, t.Drawn);
                present += Ms(t.Drawn, t.Presented);
                screen += Ms(t.Presented, t.Displayed);
            }
            int perSecond = (int)Math.Round(count / Window.TotalSeconds);
            return new LatencySnapshot(perSecond, total / count, max, encode / count, decode / count, draw / count,
                present / count, screen / count, _untimed.Count);
        }
    }

    private double Ms(long from, long to) => (to - from) / _ticksPerMs;

    private void DropOlderThanWindow(TimeSpan now)
    {
        while (_shown.Count > 0 && now - _shown.Peek().At > Window) _shown.Dequeue();
        while (_untimed.Count > 0 && now - _untimed.Peek() > Window) _untimed.Dequeue();
    }
}
