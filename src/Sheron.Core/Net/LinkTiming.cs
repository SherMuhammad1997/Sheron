using System.Diagnostics;

namespace Sheron.Core.Net;

/// <summary>Stopwatch timestamps in microseconds, the unit the links send (TRD › Automatic quality).</summary>
public static class Micros
{
    public static long From(long stopwatchTimestamp) => (long)(stopwatchTimestamp * (1_000_000.0 / Stopwatch.Frequency));

    public static long ToTimestamp(long micros) => (long)(micros * (Stopwatch.Frequency / 1_000_000.0));

    public static long Now() => From(Stopwatch.GetTimestamp());
}

/// <summary>
/// One-way delay samples, arrival − capture, from two clocks that differ by a fixed amount, so only changes mean
/// something (TRD › Automatic quality › Measure): queueing delay is the last second's lowest above the lowest of the
/// last 10 s, jitter the spread (95th − 5th percentile) over the last 2 s. Used by one thread.
/// </summary>
public sealed class DelayTracker
{
    private const long KeepMicros = 10_000_000;
    private readonly Queue<(long At, long Delay)> _samples = new();

    public void Add(long nowMicros, long delayMicros)
    {
        _samples.Enqueue((nowMicros, delayMicros));
        while (_samples.Count > 0 && nowMicros - _samples.Peek().At > KeepMicros) _samples.Dequeue();
    }

    public int QueueMs(long nowMicros)
    {
        // The lowest delay of the last second against the lowest of the last 10 s: jitter alone does not lift the lowest
        // value, a link filling up lifts them all.
        long lowest = long.MaxValue, lowestRecent = long.MaxValue;
        foreach (var (at, delay) in _samples)
        {
            if (nowMicros - at > KeepMicros) continue;
            lowest = Math.Min(lowest, delay);
            if (nowMicros - at <= 1_000_000) lowestRecent = Math.Min(lowestRecent, delay);
        }
        return lowestRecent == long.MaxValue ? 0 : (int)Math.Max(0, (lowestRecent - lowest) / 1000);
    }

    /// <summary>Enough frames in the last 2 s to tell the jitter (10 or more).</summary>
    public bool KnowsJitter(long nowMicros) => _samples.Count(s => nowMicros - s.At <= 2_000_000) >= 10;

    public int JitterMs(long nowMicros)
    {
        var recent = _samples.Where(s => nowMicros - s.At <= 2_000_000).Select(s => s.Delay).OrderBy(d => d).ToList();
        if (recent.Count < 4) return 0;
        long p5 = recent[(int)(0.05 * (recent.Count - 1))], p95 = recent[(int)Math.Ceiling(0.95 * (recent.Count - 1))];
        return (int)((p95 - p5) / 1000);
    }
}

/// <summary>
/// The other PC's clock from Clock probe and Clock reply (TRD › Automatic quality › Delay figure): of the last eight
/// replies, the one with the shortest round trip gives the offset (other clock − this clock).
/// </summary>
public sealed class ClockSync
{
    private readonly Queue<(long RoundTrip, long Offset)> _samples = new();

    /// <summary>Remote clock − local clock, in microseconds; null before the first reply.</summary>
    public long? Offset { get; private set; }

    public void Add(long sentMicros, long remoteMicros, long receivedMicros)
    {
        long roundTrip = receivedMicros - sentMicros;
        if (roundTrip < 0) return;
        _samples.Enqueue((roundTrip, remoteMicros - (sentMicros + roundTrip / 2)));
        while (_samples.Count > 8) _samples.Dequeue();
        Offset = _samples.MinBy(s => s.RoundTrip).Offset;
    }
}
