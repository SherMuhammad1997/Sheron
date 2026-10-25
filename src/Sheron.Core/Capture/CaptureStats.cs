namespace Sheron.Core.Capture;

/// <summary>Figures for the last second of capture.</summary>
public readonly record struct CaptureSnapshot(int FramesPerSecond, double AverageFrameMs, double MaxFrameMs, double AverageChangedPercent);

/// <summary>
/// Rolling one-second capture figures: frames per second, time per frame and changed area.
/// The capture thread adds frames; the test window reads snapshots, so every call is locked.
/// </summary>
public sealed class CaptureStats
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly Queue<(TimeSpan At, double FrameMs, double ChangedPercent)> _frames = new();
    private readonly object _lock = new();

    /// <param name="at">When the frame arrived, on a steady clock.</param>
    public void AddFrame(TimeSpan at, double frameMs, double changedPercent)
    {
        lock (_lock)
        {
            _frames.Enqueue((at, frameMs, changedPercent));
            DropOlderThanWindow(at);
        }
    }

    public CaptureSnapshot Snapshot(TimeSpan now)
    {
        lock (_lock)
        {
            DropOlderThanWindow(now);
            if (_frames.Count == 0) return default;

            double total = 0, max = 0, changed = 0;
            foreach (var frame in _frames)
            {
                total += frame.FrameMs;
                max = Math.Max(max, frame.FrameMs);
                changed += frame.ChangedPercent;
            }
            return new CaptureSnapshot(_frames.Count, total / _frames.Count, max, changed / _frames.Count);
        }
    }

    private void DropOlderThanWindow(TimeSpan now)
    {
        while (_frames.Count > 0 && now - _frames.Peek().At > Window)
            _frames.Dequeue();
    }
}
