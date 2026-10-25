using Sheron.Core;

namespace Sheron.Tests;

public class AppInfoTests
{
    [Fact]
    public void The_name_and_version_match_the_design()
    {
        Assert.Equal("Sheron", AppInfo.Name);
        Assert.Equal("0.1", AppInfo.Version);
    }
}
