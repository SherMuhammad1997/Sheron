namespace Sheron.Core.Video;

/// <summary>
/// One reading of DXGI frame statistics: the last screen refresh (number <paramref name="SyncRefreshCount"/> at
/// <paramref name="SyncTicks"/>) and the newest present on screen (<paramref name="PresentCount"/>, first shown at
/// refresh number <paramref name="PresentRefreshCount"/>).
/// </summary>
public readonly record struct ScreenReport(uint PresentCount, uint PresentRefreshCount, uint SyncRefreshCount, long SyncTicks);

/// <summary>
/// Matches the viewer's presents with the screen refresh that showed them, for the capture-to-display timer
/// (TRD › Viewer). A present either shows a new video frame or draws the same frame again with the pointer moved;
/// a frame counts once, from its first present. Used by the viewer thread only.
/// </summary>
public sealed class PresentTracker
{
    /// <summary>A minimised window shows nothing; stop keeping presents after about 4 s of them.</summary>
    private const int MaxWaiting = 240;

    private readonly Queue<(uint Count, FrameTimes Times)> _waiting = new();
    private long _lastCaptured;
    private uint _lastSyncRefresh;
    private long _lastSyncTicks;
    private double _refreshTicks;

    /// <summary>Presents not yet seen on screen.</summary>
    public int Waiting => _waiting.Count;

    /// <param name="presentCount">The swap chain's present count right after this present.</param>
    /// <param name="times">The video frame this present shows; <see cref="FrameTimes.Captured"/> tells frames apart.</param>
    public void Presented(uint presentCount, FrameTimes times)
    {
        if (_waiting.Count >= MaxWaiting) _waiting.Dequeue();
        _waiting.Enqueue((presentCount, times));
    }

    /// <summary>
    /// Takes one statistics reading. A frame now on screen goes to <paramref name="shown"/> with
    /// <see cref="FrameTimes.Displayed"/> filled in; older frames that were never reported count as untimed.
    /// </summary>
    /// <returns>How many frames became untimed.</returns>
    public int Report(ScreenReport report, List<FrameTimes> shown)
    {
        MeasureRefresh(report);
        var settled = new List<(uint Count, FrameTimes Times)>();
        while (_waiting.Count > 0 && _waiting.Peek().Count <= report.PresentCount) settled.Add(_waiting.Dequeue());
        if (settled.Count == 0) return 0;

        var newest = settled[^1];
        long? onScreen = newest.Count == report.PresentCount ? DisplayedAt(report) : null;
        int untimed = 0;
        foreach (var (_, times) in settled)
        {
            if (times.Captured == _lastCaptured) continue;   // the same frame again, already counted
            _lastCaptured = times.Captured;
            if (onScreen is { } at && times.Captured == newest.Times.Captured)
                shown.Add(times with { Displayed = at });
            else
                untimed++;
        }
        return untimed;
    }

    /// <summary>
    /// When the newest present first reached the screen: the reported refresh, moved back by whole refreshes if
    /// Windows reports it a little late.
    /// </summary>
    private long? DisplayedAt(ScreenReport report)
    {
        if (report.SyncTicks <= 0) return null;
        long late = (long)report.SyncRefreshCount - report.PresentRefreshCount;
        if (late <= 0) return report.SyncTicks;
        if (_refreshTicks <= 0) return null;
        return report.SyncTicks - (long)Math.Round(late * _refreshTicks);
    }

    /// <summary>The time between screen refreshes, from two readings of different refreshes.</summary>
    private void MeasureRefresh(ScreenReport report)
    {
        if (_lastSyncTicks != 0 && report.SyncRefreshCount > _lastSyncRefresh && report.SyncTicks > _lastSyncTicks)
            _refreshTicks = (double)(report.SyncTicks - _lastSyncTicks) / (report.SyncRefreshCount - _lastSyncRefresh);
        _lastSyncRefresh = report.SyncRefreshCount;
        _lastSyncTicks = report.SyncTicks;
    }
}
