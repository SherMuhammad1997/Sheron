using System.Diagnostics;
using Sheron.Core.Diagnostics;

namespace Sheron.Win;

/// <summary>
/// Sheron's own CPU use for the developer windows, averaged over about 3 s of half-second readings: process times move
/// in scheduler ticks, too coarse for half a second.
/// </summary>
internal sealed class CpuMeter : IDisposable
{
    private const int Readings = 6;

    private readonly Process _self = Process.GetCurrentProcess();
    private readonly Queue<(long At, TimeSpan Cpu)> _readings = new();

    public CpuMeter()
    {
        _readings.Enqueue((Stopwatch.GetTimestamp(), _self.TotalProcessorTime));
    }

    /// <summary>Takes a reading; returns the percent of the whole PC used since the oldest reading kept.</summary>
    public double Read()
    {
        _self.Refresh();
        long at = Stopwatch.GetTimestamp();
        var cpu = _self.TotalProcessorTime;
        var oldest = _readings.Peek();
        double percent = CpuUsage.Percent(cpu - oldest.Cpu, Stopwatch.GetElapsedTime(oldest.At, at), Environment.ProcessorCount);
        _readings.Enqueue((at, cpu));
        if (_readings.Count > Readings) _readings.Dequeue();
        return percent;
    }

    public void Dispose() => _self.Dispose();
}
