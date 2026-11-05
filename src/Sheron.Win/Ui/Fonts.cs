using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Sheron.Win.Ui;

internal enum Weight
{
    Regular,
    Medium,
    SemiBold,
    Bold,
}

/// <summary>
/// IBM Plex Sans and Mono, built into the exe (TRD › Windows UI › Fonts; third_party/ibm-plex): loaded once for this
/// process only, for GDI text (<c>AddFontMemResourceEx</c>) and for <see cref="Font"/> objects (a
/// <see cref="PrivateFontCollection"/>). Nothing is installed on the PC. Sizes are in pixels, like the design boards.
/// </summary>
internal static class Fonts
{
    private static readonly string[] Files =
    [
        "IBMPlexSans-Regular.ttf", "IBMPlexSans-Medium.ttf", "IBMPlexSans-SemiBold.ttf", "IBMPlexSans-Bold.ttf",
        "IBMPlexMono-Regular.ttf", "IBMPlexMono-Medium.ttf", "IBMPlexMono-SemiBold.ttf",
    ];

    private static readonly object Lock = new();
    private static PrivateFontCollection? _collection;
    private static FontFamily[] _families = [];

    /// <summary>Loads the fonts; later calls do nothing. Fonts that cannot be loaded fall back to Segoe UI and Consolas.</summary>
    public static void Load()
    {
        lock (Lock)
        {
            if (_collection is not null) return;
            _collection = new PrivateFontCollection();
            foreach (string file in Files)
            {
                using var stream = typeof(Fonts).Assembly.GetManifestResourceStream($"Sheron.Win.Fonts.{file}");
                if (stream is null) continue;
                var data = new byte[stream.Length];
                stream.ReadExactly(data);
                // Kept for the life of the process: both GDI and GDI+ read the font from this memory.
                IntPtr memory = Marshal.AllocCoTaskMem(data.Length);
                Marshal.Copy(data, 0, memory, data.Length);
                _collection.AddMemoryFont(memory, data.Length);
                uint added = 0;
                AddFontMemResourceEx(memory, (uint)data.Length, IntPtr.Zero, ref added);
            }
            _families = _collection.Families;
        }
    }

    public static Font Sans(float pixels, Weight weight = Weight.Regular) => Make("IBM Plex Sans", "Segoe UI", pixels, weight);

    public static Font Mono(float pixels, Weight weight = Weight.Regular) => Make("IBM Plex Mono", "Consolas", pixels, weight);

    private static Font Make(string family, string fallback, float pixels, Weight weight)
    {
        Load();
        // Windows names the weights as families of their own; Bold is a style of the regular family.
        string name = weight switch
        {
            Weight.Medium => family + " Medm",
            Weight.SemiBold => family + " SmBld",
            _ => family,
        };
        var style = weight == Weight.Bold ? FontStyle.Bold : FontStyle.Regular;
        var found = _families.FirstOrDefault(f => f.Name == name && f.IsStyleAvailable(style));
        return found is not null
            ? new Font(found, pixels, style, GraphicsUnit.Pixel)
            : new Font(fallback, pixels, weight >= Weight.SemiBold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr AddFontMemResourceEx(IntPtr font, uint length, IntPtr reserved, ref uint fonts);
}
