namespace Sheron.Core.Diagnostics;

public static class CpuUsage
{
    /// <summary>
    /// Share of the whole PC a process used, 0–100 %, the way Task Manager shows it:
    /// CPU time used ÷ (time passed × logical processors).
    /// </summary>
    public static double Percent(TimeSpan cpuTimeUsed, TimeSpan timePassed, int logicalProcessors)
    {
        if (timePassed <= TimeSpan.Zero || logicalProcessors <= 0) return 0;
        return 100.0 * cpuTimeUsed.Ticks / (timePassed.Ticks * (double)logicalProcessors);
    }
}
