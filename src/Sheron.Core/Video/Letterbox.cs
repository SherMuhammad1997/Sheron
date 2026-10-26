using Sheron.Core.Capture;

namespace Sheron.Core.Video;

public static class Letterbox
{
    /// <summary>
    /// The largest rectangle with the picture's shape that fits in the window, centred; the bars around it stay
    /// background (TRD › Viewer).
    /// </summary>
    public static ScreenRect Fit(int pictureWidth, int pictureHeight, int windowWidth, int windowHeight)
    {
        if (pictureWidth <= 0 || pictureHeight <= 0 || windowWidth <= 0 || windowHeight <= 0) return default;
        // Compare the shapes in whole numbers: is the window wider than the picture?
        int width, height;
        if ((long)windowWidth * pictureHeight > (long)windowHeight * pictureWidth)
        {
            height = windowHeight;
            width = (int)Math.Round((double)windowHeight * pictureWidth / pictureHeight);
        }
        else
        {
            width = windowWidth;
            height = (int)Math.Round((double)windowWidth * pictureHeight / pictureWidth);
        }
        int left = (windowWidth - width) / 2, top = (windowHeight - height) / 2;
        return new ScreenRect(left, top, left + width, top + height);
    }
}
