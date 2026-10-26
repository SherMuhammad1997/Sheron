namespace Sheron.Core.Capture;

/// <summary>
/// A pointer shape turned into what the viewer draws on top of the video (TRD › Viewer): colour pixels with
/// premultiplied alpha, and a mask of the pixels that invert the screen (the text I-beam, for example). Both are BGRA,
/// <see cref="Width"/> × <see cref="Height"/>, rows without padding.
/// </summary>
public sealed record PointerImage(int Width, int Height, int HotSpotX, int HotSpotY, byte[] Color, byte[] Invert, bool HasInvert)
{
    private const int Bgra = 4;

    public static PointerImage From(PointerShape shape) => shape.Kind switch
    {
        PointerShapeKind.Monochrome => FromMonochrome(shape),
        PointerShapeKind.MaskedColor => FromMaskedColor(shape),
        _ => FromColor(shape),
    };

    /// <summary>
    /// 1 bit per pixel, most significant bit first: the AND mask on top, the XOR mask below it, so the pointer is half
    /// as tall as the shape. AND 0 / XOR 0 black, 0 / 1 white, 1 / 0 screen unchanged, 1 / 1 screen inverted.
    /// </summary>
    private static PointerImage FromMonochrome(PointerShape shape)
    {
        int width = shape.Width, height = shape.Height / 2;
        var color = new byte[width * height * Bgra];
        var invert = new byte[width * height * Bgra];
        bool hasInvert = false;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            bool and = Bit(shape, x, y);
            bool xor = Bit(shape, x, y + height);
            int at = (y * width + x) * Bgra;
            if (!and)
                Set(color, at, xor ? (byte)255 : (byte)0, 255);
            else if (xor)
            {
                Set(invert, at, 255, 255);
                hasInvert = true;
            }
        }
        return new PointerImage(width, height, shape.HotSpotX, shape.HotSpotY, color, invert, hasInvert);
    }

    /// <summary>32-bit BGRA with straight alpha, as Windows hands it over; multiplied out here.</summary>
    private static PointerImage FromColor(PointerShape shape)
    {
        int width = shape.Width, height = shape.Height;
        var color = new byte[width * height * Bgra];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int from = y * shape.Pitch + x * Bgra, to = (y * width + x) * Bgra;
            byte alpha = shape.Pixels[from + 3];
            for (int c = 0; c < 3; c++) color[to + c] = (byte)((shape.Pixels[from + c] * alpha + 127) / 255);
            color[to + 3] = alpha;
        }
        return new PointerImage(width, height, shape.HotSpotX, shape.HotSpotY, color, new byte[color.Length], false);
    }

    /// <summary>
    /// 32-bit BGRX; the top byte 0 means the pixel replaces the screen, 0xFF means it is XORed with the screen. XOR
    /// with black changes nothing; XOR with any other colour is drawn as an inverted pixel (Windows uses white for
    /// that, so this is exact for normal pointers).
    /// </summary>
    private static PointerImage FromMaskedColor(PointerShape shape)
    {
        int width = shape.Width, height = shape.Height;
        var color = new byte[width * height * Bgra];
        var invert = new byte[width * height * Bgra];
        bool hasInvert = false;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int from = y * shape.Pitch + x * Bgra, to = (y * width + x) * Bgra;
            var pixel = shape.Pixels.AsSpan(from, 3);
            if (shape.Pixels[from + 3] == 0)
            {
                pixel.CopyTo(color.AsSpan(to, 3));
                color[to + 3] = 255;
            }
            else if (pixel[0] != 0 || pixel[1] != 0 || pixel[2] != 0)
            {
                Set(invert, to, 255, 255);
                hasInvert = true;
            }
        }
        return new PointerImage(width, height, shape.HotSpotX, shape.HotSpotY, color, invert, hasInvert);
    }

    private static bool Bit(PointerShape shape, int x, int row) =>
        (shape.Pixels[row * shape.Pitch + x / 8] & (0x80 >> (x % 8))) != 0;

    private static void Set(byte[] pixels, int at, byte grey, byte alpha)
    {
        pixels[at] = pixels[at + 1] = pixels[at + 2] = grey;
        pixels[at + 3] = alpha;
    }
}
