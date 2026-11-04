using Sheron.Core.Session;

namespace Sheron.Core.Video;

/// <summary>What the remote PC encodes at one step of automatic quality.</summary>
public sealed record QualityLevel(int BitsPerSecond, int FramesPerSecond, int ScalePercent)
{
    /// <summary>The encoder settings for a screen of this size at this level: even sizes, as H.264 needs.</summary>
    public VideoSettings For(int screenWidth, int screenHeight) => new(
        Math.Max(2, screenWidth * ScalePercent / 100) & ~1, Math.Max(2, screenHeight * ScalePercent / 100) & ~1,
        FramesPerSecond, BitsPerSecond);

    public override string ToString() => $"{BitsPerSecond / 1e6:0.#} Mbps · {FramesPerSecond} fps · {ScalePercent} %";
}

/// <summary>
/// Automatic quality on the remote PC (Plan step 10, TRD › Automatic quality): steps down the ladder (bitrate, then
/// fps, then size) when the viewer's reports show frames lost after repair or the delay rising, and back up when the
/// link stays calm; a CPU guard caps fps and size when Sheron uses too much of the PC. Times are any monotonic clock.
/// </summary>
public sealed class QualityController
{
    public static readonly IReadOnlyList<QualityLevel> Ladder =
    [
        new(8_000_000, 60, 100), new(6_000_000, 60, 100), new(4_000_000, 60, 100), new(3_000_000, 60, 100),
        new(2_000_000, 60, 100), new(2_000_000, 30, 100), new(2_000_000, 15, 100),
        new(1_500_000, 15, 75), new(1_000_000, 15, 50),
    ];

    /// <summary>The CPU guard's caps: none, then 30 fps, 15 fps, 75 % and 50 % size.</summary>
    private static readonly (int Fps, int Scale)[] CpuCaps = [(60, 100), (30, 100), (15, 100), (15, 75), (15, 50)];

    public const double BadFrameLoss = 0.02;
    public const double CalmFrameLoss = 0.005;
    public const int BadQueueMs = 100;
    public const int CalmQueueMs = 30;
    public const double CpuHigh = 10, CpuLow = 7;

    private TimeSpan _lastChange = TimeSpan.MinValue;
    private int _bad, _calm;
    private TimeSpan? _cpuHighSince, _cpuLowSince;

    /// <summary>Place on <see cref="Ladder"/>: 0 is the best.</summary>
    public int Level { get; private set; }

    /// <summary>How many CPU caps are on, 0–4.</summary>
    public int CpuCap { get; private set; }

    /// <summary>The ladder step with the CPU caps applied: what to encode now.</summary>
    public QualityLevel Current
    {
        get
        {
            var step = Ladder[Level];
            var cap = CpuCaps[CpuCap];
            return step with { FramesPerSecond = Math.Min(step.FramesPerSecond, cap.Fps), ScalePercent = Math.Min(step.ScalePercent, cap.Scale) };
        }
    }

    public static bool IsBad(LinkReport report) => report.FrameLoss > BadFrameLoss || report.QueueMs > BadQueueMs;

    public static bool IsCalm(LinkReport report) => (report.FrameLoss ?? 0) <= CalmFrameLoss && report.QueueMs < CalmQueueMs;

    /// <summary>One Link report (every 250 ms). True when <see cref="Current"/> changed.</summary>
    public bool OnReport(LinkReport report, TimeSpan now)
    {
        var before = Current;
        if (IsBad(report))
        {
            _bad++;
            _calm = 0;
            if (_bad >= 2 && Level < Ladder.Count - 1 && SinceChange(now) >= TimeSpan.FromSeconds(1)) Change(Level + 1, now);
        }
        else if (IsCalm(report))
        {
            _calm++;
            _bad = 0;
            if (_calm >= 8 && Level > 0 && SinceChange(now) >= TimeSpan.FromSeconds(2)) Change(Level - 1, now);
        }
        else
        {
            _bad = _calm = 0;
        }
        return Current != before;
    }

    /// <summary>Sheron's CPU use on this PC, in percent (every second or so). True when <see cref="Current"/> changed.</summary>
    public bool OnCpu(double percent, TimeSpan now)
    {
        var before = Current;
        _cpuHighSince = percent > CpuHigh ? _cpuHighSince ?? now : null;
        _cpuLowSince = percent < CpuLow ? _cpuLowSince ?? now : null;
        if (_cpuHighSince is { } high && now - high >= TimeSpan.FromSeconds(5) && CpuCap < CpuCaps.Length - 1)
        {
            CpuCap++;
            _cpuHighSince = now;
        }
        else if (_cpuLowSince is { } low && now - low >= TimeSpan.FromSeconds(10) && CpuCap > 0)
        {
            CpuCap--;
            _cpuLowSince = now;
        }
        return Current != before;
    }

    private TimeSpan SinceChange(TimeSpan now) => _lastChange == TimeSpan.MinValue ? TimeSpan.MaxValue : now - _lastChange;

    private void Change(int level, TimeSpan now)
    {
        Level = level;
        _lastChange = now;
        _bad = _calm = 0;
    }
}
