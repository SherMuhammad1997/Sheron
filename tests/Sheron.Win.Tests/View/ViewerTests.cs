using Sheron.Core.Capture;
using Sheron.Core.Video;
using Sheron.Win.Encode;
using Sheron.Win.View;
using static Sheron.Win.Tests.TestScreen;

namespace Sheron.Win.Tests.View;

/// <summary>
/// Plan step 5, the viewer half: decodes what the encoder makes from the real screen, and draws it. Needs a real,
/// unlocked Windows desktop: run from Sher's PC, not over Remote Desktop.
/// </summary>
[Collection(DesktopDuplication.Collection)]
public class ViewerTests
{
    [Fact]
    public void Hardware_decoder_gives_each_frame_straight_back() =>
        OnMtaThread(() => AssertDecodes(DecoderKind.Hardware));

    [Fact]
    public void Software_decoder_gives_each_frame_straight_back() =>
        OnMtaThread(() => AssertDecodes(DecoderKind.Software));

    /// <summary>
    /// Draws a decoded frame with an I-beam-like pointer (black and inverting pixels) into a window: compiles the
    /// pointer shaders and runs the video processor. 640 × 480 is squarer than the screen, so there are bars.
    /// </summary>
    [Fact]
    public void Renderer_draws_a_decoded_frame_and_the_pointer() => OnMtaThread(() =>
    {
        var (frames, width, height) = EncodeScreen(1);
        var (device, context) = LoopViewer.CreateDevice();
        using (device)
        using (context)
        using (var decoder = H264Decoder.Create(device, context, DecoderKind.Software, width, height))
        using (var window = new Form { ClientSize = new Size(640, 480), ShowInTaskbar = false })
        using (var renderer = new VideoRenderer(device, context, window.Handle, 640, 480))
        {
            Assert.True(decoder.Decode(frames[0].Data, frames[0].KeyFrame, TimeSpan.Zero), "the decoder gave no picture");
            var pointer = PointerImage.From(IBeam());

            renderer.Draw(decoder.Frame!, decoder.Width, decoder.Height, pointer, new PointerPosition(true, 100, 100));
            uint count = renderer.Present();
            renderer.TryReadScreen(out _, out _);

            Assert.True(count > 0);
            Assert.Equal(640, renderer.Picture.Width);
            Assert.True(renderer.Picture.Height < 480, $"picture {renderer.Picture} should leave bars in 640 × 480");
        }
    });

    /// <summary>Plan step 5: each frame comes out of the decoder as soon as it goes in (low-latency mode).</summary>
    private static void AssertDecodes(DecoderKind kind)
    {
        var (frames, width, height) = EncodeScreen(3);
        var (device, context) = LoopViewer.CreateDevice();
        using (device)
        using (context)
        using (var decoder = H264Decoder.Create(device, context, kind, width, height))
        {
            for (int i = 0; i < frames.Count; i++)
            {
                bool decoded = decoder.Decode(frames[i].Data, frames[i].KeyFrame, TimeSpan.FromSeconds(i / 60.0));
                Assert.True(decoded, $"{decoder.Name} ({kind}) gave no picture for frame {i} (keyframe: {frames[i].KeyFrame})");
            }
            Assert.Equal((width, height), (decoder.Width, decoder.Height));
            Assert.NotNull(decoder.Frame);
        }
    }

    /// <summary>
    /// Encodes the captured screen <paramref name="count"/> times (a keyframe, then frames with no change) with the
    /// first encoder route that starts on this PC, as the capture loop does.
    /// </summary>
    private static (List<EncodedFrame> Frames, int Width, int Height) EncodeScreen(int count)
    {
        using var duplicator = OpenWithFirstFrame();
        var settings = VideoSettings.ForScreen(duplicator.Width, duplicator.Height);
        var failures = new List<string>();
        foreach (var kind in EncoderFactory.AllKinds)
        {
            using var encoder = EncoderFactory.Create(duplicator.Device, duplicator.Context, settings, kind,
                duplicator.AdapterVendorId, duplicator.AdapterIndex, out var tried);
            failures.AddRange(tried);
            if (encoder is null) continue;
            using var converter = new Nv12Converter(duplicator.Device, duplicator.Context, duplicator.Width,
                duplicator.Height, settings, encoder.InputBindFlags);
            var frames = new List<EncodedFrame>();
            for (int i = 0; i < 30 && frames.Count < count; i++)
                frames.AddRange(encoder.Encode(converter.Convert(duplicator.Frame!), TimeSpan.FromSeconds(i / 60.0)));
            Assert.True(frames.Count >= count, $"{encoder.Name} gave {frames.Count} frames of {count}");
            Assert.True(frames[0].KeyFrame, $"{encoder.Name}: the first frame is not a keyframe");
            return (frames.Take(count).ToList(), settings.Width, settings.Height);
        }
        throw new Xunit.Sdk.XunitException("No encoder started: " + string.Join("; ", failures));
    }

    /// <summary>
    /// A 16 × 16 monochrome pointer: a black top bar, an inverting stem in the middle, see-through elsewhere.
    /// </summary>
    private static PointerShape IBeam()
    {
        const int width = 16, height = 16, pitch = 2;
        var pixels = new byte[pitch * height * 2];
        for (int y = 0; y < height; y++)
        {
            bool bar = y < 2;
            // AND mask: 0 where the pointer covers the screen (the bar), 1 elsewhere.
            pixels[y * pitch] = pixels[y * pitch + 1] = bar ? (byte)0x00 : (byte)0xFF;
            // XOR mask: the two middle columns, where AND is 1, invert the screen.
            pixels[(height + y) * pitch] = bar ? (byte)0x00 : (byte)0x01;
            pixels[(height + y) * pitch + 1] = bar ? (byte)0x00 : (byte)0x80;
        }
        return new PointerShape(PointerShapeKind.Monochrome, width, height * 2, pitch, HotSpotX: 8, HotSpotY: 8, pixels);
    }
}
