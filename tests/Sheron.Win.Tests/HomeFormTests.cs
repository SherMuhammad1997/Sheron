using Sheron.Core;
using Sheron.Win;
using Sheron.Win.Ui;

namespace Sheron.Win.Tests;

/// <summary>Plan step 15a on Windows: the home window and the bundled fonts (TRD › Windows UI).</summary>
public class HomeFormTests
{
    [Fact]
    public void The_home_window_shows_the_product_name()
    {
        using var form = new HomeForm();
        Assert.Equal(AppInfo.Name, form.Text);
    }

    [Theory]
    [InlineData(false, 0, "IBM Plex Sans")]
    [InlineData(false, 1, "IBM Plex Sans Medm")]
    [InlineData(false, 2, "IBM Plex Sans SmBld")]
    [InlineData(false, 3, "IBM Plex Sans")]
    [InlineData(true, 0, "IBM Plex Mono")]
    [InlineData(true, 2, "IBM Plex Mono SmBld")]
    public void The_bundled_Plex_fonts_load_not_a_fallback(bool mono, int weightNumber, string family)
    {
        var weight = (Weight)weightNumber;
        using var font = mono ? Fonts.Mono(15, weight) : Fonts.Sans(15, weight);
        Assert.Equal(family, font.FontFamily.Name);
        Assert.Equal(weight == Weight.Bold, font.Bold);
        // GDI draws it too: the text is as wide as Plex makes it, not as a fallback would.
        Assert.True(TextRenderer.MeasureText("Sheron 1920", font).Width > 0);
    }
}
