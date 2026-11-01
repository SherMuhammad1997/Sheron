using System.ComponentModel;
using System.Runtime.InteropServices;
using Sheron.Core.Capture;

namespace Sheron.Win.View;

/// <summary>
/// The remote pointer's shape as the viewer's own Windows pointer (TRD › Input path › Pointer), so it moves with the
/// viewer's mouse at once. Made with <c>CreateIconIndirect</c>: a monochrome shape gives its AND and XOR masks
/// straight to Windows; a colour shape gives its BGRA pixels with alpha; a masked-colour shape gives an AND mask
/// (the pixels that XOR the screen) and its colours with no alpha, so Windows does the XOR itself.
/// </summary>
internal sealed class RemoteCursor : IDisposable
{
    private readonly IntPtr _handle;

    private RemoteCursor(IntPtr handle)
    {
        _handle = handle;
        Cursor = new Cursor(handle);
    }

    /// <summary>The cursor; it stays valid until this object is disposed.</summary>
    public Cursor Cursor { get; }

    public static RemoteCursor From(PointerShape shape)
    {
        int width = shape.Width, height = shape.Height;
        IntPtr mask = IntPtr.Zero, color = IntPtr.Zero;
        try
        {
            switch (shape.Kind)
            {
                case PointerShapeKind.Monochrome:
                    // AND mask on top of the XOR mask, both in one 1-bit bitmap of twice the height.
                    mask = MaskBitmap(width, height, (x, y) => Bit(shape, x, y));
                    height /= 2;
                    break;
                case PointerShapeKind.MaskedColor:
                    mask = MaskBitmap(width, height, (x, y) => shape.Pixels[y * shape.Pitch + x * 4 + 3] == 0xFF);
                    color = ColorBitmap(shape, keepAlpha: false);
                    break;
                default:
                    mask = MaskBitmap(width, height, (_, _) => false);
                    color = ColorBitmap(shape, keepAlpha: true);
                    break;
            }

            var info = new IconInfo
            {
                IsIcon = false,
                HotSpotX = Math.Min(shape.HotSpotX, width - 1),
                HotSpotY = Math.Min(shape.HotSpotY, height - 1),
                Mask = mask,
                Color = color,
            };
            var handle = CreateIconIndirect(ref info);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateIconIndirect failed");
            return new RemoteCursor(handle);
        }
        finally
        {
            // The cursor keeps its own copies of the bitmaps.
            if (mask != IntPtr.Zero) DeleteObject(mask);
            if (color != IntPtr.Zero) DeleteObject(color);
        }
    }

    private static bool Bit(PointerShape shape, int x, int row) =>
        (shape.Pixels[row * shape.Pitch + x / 8] & (0x80 >> (x % 8))) != 0;

    /// <summary>A 1-bit bitmap, most significant bit first, rows padded to 2 bytes as <c>CreateBitmap</c> wants.</summary>
    private static IntPtr MaskBitmap(int width, int height, Func<int, int, bool> set)
    {
        int stride = (width + 15) / 16 * 2;
        var bits = new byte[stride * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            if (set(x, y)) bits[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
        var bitmap = CreateBitmap(width, height, 1, 1, bits);
        if (bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateBitmap failed");
        return bitmap;
    }

    /// <summary>A 32-bit top-down DIB of the shape's pixels; without alpha, Windows applies the AND mask and XORs the colour.</summary>
    private static unsafe IntPtr ColorBitmap(PointerShape shape, bool keepAlpha)
    {
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = shape.Width,
            Height = -shape.Height,   // negative: rows from the top
            Planes = 1,
            BitCount = 32,
        };
        var bitmap = CreateDIBSection(IntPtr.Zero, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDIBSection failed");
        byte* to = (byte*)bits;
        for (int y = 0; y < shape.Height; y++)
        for (int x = 0; x < shape.Width; x++)
        {
            int from = y * shape.Pitch + x * 4, at = (y * shape.Width + x) * 4;
            to[at] = shape.Pixels[from];
            to[at + 1] = shape.Pixels[from + 1];
            to[at + 2] = shape.Pixels[from + 2];
            to[at + 3] = keepAlpha ? shape.Pixels[from + 3] : (byte)0;
        }
        return bitmap;
    }

    public void Dispose()
    {
        Cursor.Dispose();
        DestroyIcon(_handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool IsIcon;
        public int HotSpotX;
        public int HotSpotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref IconInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, byte[] bits);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr bitmap);
}
