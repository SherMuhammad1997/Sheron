namespace Sheron.Core.Capture;

/// <summary>A rectangle on the captured screen in pixels; Right and Bottom are exclusive, as in Windows.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Math.Max(0, Right - Left);
    public int Height => Math.Max(0, Bottom - Top);
    public long Area => (long)Width * Height;

    /// <summary>The part of this rectangle that lies on a screen of the given size.</summary>
    public ScreenRect ClipTo(int screenWidth, int screenHeight) =>
        new(Math.Clamp(Left, 0, screenWidth), Math.Clamp(Top, 0, screenHeight),
            Math.Clamp(Right, 0, screenWidth), Math.Clamp(Bottom, 0, screenHeight));
}

public static class ChangedArea
{
    /// <summary>
    /// How much of the screen the changed rectangles cover, 0–100 %. Overlaps are counted twice, so the result is
    /// an upper estimate, capped at 100 %.
    /// </summary>
    public static double Percent(IEnumerable<ScreenRect> changed, int screenWidth, int screenHeight)
    {
        long screen = (long)screenWidth * screenHeight;
        if (screen <= 0) return 0;
        long covered = changed.Sum(r => r.ClipTo(screenWidth, screenHeight).Area);
        return Math.Min(100.0, 100.0 * covered / screen);
    }
}
