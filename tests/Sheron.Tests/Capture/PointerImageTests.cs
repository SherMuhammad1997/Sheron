using Sheron.Core.Capture;

namespace Sheron.Tests.Capture;

public class PointerImageTests
{
    private static byte[] Pixel(byte[] image, int width, int x, int y) => image.AsSpan((y * width + x) * 4, 4).ToArray();

    [Fact]
    public void Monochrome_masks_give_black_white_see_through_and_invert()
    {
        // 8 × 2 pointer, 1 byte per row: AND rows, then XOR rows. Pixel 0: AND 0 XOR 0 (black), 1: 0 1 (white),
        // 2: 1 0 (see-through), 3: 1 1 (invert). Row 1 is all see-through.
        var shape = new PointerShape(PointerShapeKind.Monochrome, 8, 4, 1, HotSpotX: 1, HotSpotY: 0,
            [0b0011_1111, 0xFF, 0b0101_0000, 0x00]);

        var image = PointerImage.From(shape);

        Assert.Equal((8, 2, 1, 0), (image.Width, image.Height, image.HotSpotX, image.HotSpotY));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(image.Color, 8, 0, 0));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(image.Color, 8, 1, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(image.Color, 8, 2, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(image.Invert, 8, 2, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(image.Color, 8, 3, 0));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(image.Invert, 8, 3, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(image.Color, 8, 0, 1));
        Assert.True(image.HasInvert);
    }

    [Fact]
    public void Colour_pixels_are_premultiplied_and_row_padding_is_dropped()
    {
        // One BGRA pixel at half alpha; the row is 8 bytes long (4 bytes of padding).
        var shape = new PointerShape(PointerShapeKind.Color, 1, 1, 8, 0, 0, [200, 100, 50, 128, 9, 9, 9, 9]);

        var image = PointerImage.From(shape);

        Assert.Equal(new byte[] { 100, 50, 25, 128 }, image.Color);
        Assert.False(image.HasInvert);
    }

    [Fact]
    public void Masked_colour_replaces_skips_or_inverts()
    {
        // Mask 0: replace with the colour. Mask 0xFF with black: XOR changes nothing. Mask 0xFF with white: invert.
        var shape = new PointerShape(PointerShapeKind.MaskedColor, 3, 1, 12, 0, 0,
            [10, 20, 30, 0, 0, 0, 0, 0xFF, 255, 255, 255, 0xFF]);

        var image = PointerImage.From(shape);

        Assert.Equal(new byte[] { 10, 20, 30, 255 }, Pixel(image.Color, 3, 0, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(image.Color, 3, 1, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(image.Invert, 3, 1, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, Pixel(image.Color, 3, 2, 0));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(image.Invert, 3, 2, 0));
        Assert.True(image.HasInvert);
    }
}
