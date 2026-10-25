using Sheron.Core;
using Sheron.Win;

namespace Sheron.Win.Tests;

public class MainFormTests
{
    [Fact]
    public void The_main_window_shows_the_product_name()
    {
        using var form = new MainForm();
        Assert.Equal(AppInfo.Name, form.Text);
    }
}
