namespace Sheron.Core.Video;

/// <summary>
/// What the encoder makes (TRD › Video path). Fixed for now; automatic quality (Plan step 10) will change the
/// bitrate, frame rate and size to suit the link.
/// </summary>
public sealed record VideoSettings(int Width, int Height, int FramesPerSecond = 60, int BitsPerSecond = 8_000_000, int KeyFrameInterval = 300)
{
    /// <summary>H.264 and NV12 need even sizes, so an odd last column or row of the screen is left out.</summary>
    public static VideoSettings ForScreen(int screenWidth, int screenHeight) => new(screenWidth & ~1, screenHeight & ~1);
}
