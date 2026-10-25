using Sheron.Core.Diagnostics;

namespace Sheron.Tests.Diagnostics;

public class CpuUsageTests
{
    [Fact]
    public void Half_a_second_of_cpu_in_one_second_on_4_processors_is_12_5_percent()
    {
        Assert.Equal(12.5, CpuUsage.Percent(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), 4));
    }

    [Fact]
    public void No_time_passed_reads_as_zero()
    {
        Assert.Equal(0, CpuUsage.Percent(TimeSpan.FromSeconds(1), TimeSpan.Zero, 4));
    }
}
