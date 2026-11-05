namespace Sheron.Win.Ui;

/// <summary>The Control Room colours of the design boards (TRD › Windows UI › Style; docs/design/README.md).</summary>
internal static class Theme
{
    public static readonly Color Background = Hex(0x0E141B);
    public static readonly Color Bar = Hex(0x111922);
    public static readonly Color Panel = Hex(0x151E28);
    public static readonly Color Line = Hex(0x243142);
    public static readonly Color RowLine = Hex(0x1E2936);
    public static readonly Color Edge = Hex(0x3A4A5E);
    public static readonly Color Raised = Hex(0x1C2733);
    public static readonly Color RaisedEdge = Hex(0x2C3A4C);
    public static readonly Color Ink = Hex(0xE6EDF3);
    public static readonly Color Soft = Hex(0xC9D4E0);
    public static readonly Color Muted = Hex(0x9FB0C3);
    public static readonly Color Placeholder = Hex(0x8B9BAE);
    public static readonly Color Accent = Hex(0xF2A93B);
    public static readonly Color AccentHover = Hex(0xFFC56B);
    public static readonly Color AccentShade = Hex(0x2A2113);
    public static readonly Color Good = Hex(0x3FB871);
    public static readonly Color Stop = Hex(0xE5534B);
    public static readonly Color StopText = Hex(0xF47067);
    public static readonly Color StopShade = Hex(0x2A1517);

    private static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
