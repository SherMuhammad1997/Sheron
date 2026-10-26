using Sheron.Core.Capture;
using Sheron.Core.Session;

namespace Sheron.Tests.Session;

public class ControlMessageTests
{
    [Fact]
    public void Each_message_reads_back_as_it_was_written()
    {
        Assert.True(ControlMessage.TryReadVideoFormat(ControlMessage.VideoFormat(2560, 1440), out int w, out int h));
        Assert.Equal((2560, 1440), (w, h));

        var mono = new PointerShape(PointerShapeKind.Monochrome, 32, 64, 4, 0, 0, new byte[4 * 64]);
        Assert.True(ControlMessage.TryReadPointerShape(ControlMessage.Pointer(mono), out var back));
        Assert.Equal((mono.Kind, mono.Width, mono.Height, mono.Pitch), (back!.Kind, back.Width, back.Height, back.Pitch));

        Assert.True(ControlMessage.TryReadPointerPosition(ControlMessage.Pointer(new PointerPosition(false, 5, -6)), out var p));
        Assert.Equal(new PointerPosition(false, 5, -6), p);
        Assert.Equal(ControlType.Ping, ControlMessage.TypeOf(ControlMessage.Ping()));
    }

    [Theory]
    [InlineData(PointerShapeKind.Color, 0, 32, 0, 0)]          // no width
    [InlineData(PointerShapeKind.Color, 32, 32, 64, 4096)]     // rows shorter than the width
    [InlineData(PointerShapeKind.Color, 32, 32, 128, 100)]     // pixels missing
    [InlineData(PointerShapeKind.Monochrome, 32, 33, 4, 132)]  // monochrome needs two masks of the same height
    [InlineData((PointerShapeKind)3, 32, 32, 128, 4096)]       // unknown kind
    [InlineData(PointerShapeKind.Color, 600, 32, 2400, 76800)] // larger than any pointer
    public void Shapes_that_cannot_be_drawn_safely_are_refused(PointerShapeKind kind, int width, int height, int pitch, int pixels)
    {
        var message = ControlMessage.Pointer(new PointerShape(kind, width, height, pitch, 0, 0, new byte[pixels]));
        Assert.False(ControlMessage.TryReadPointerShape(message, out _));
    }

    [Fact]
    public void Odd_or_silly_video_sizes_are_refused()
    {
        Assert.False(ControlMessage.TryReadVideoFormat(ControlMessage.VideoFormat(1921, 1080), out _, out _));
        Assert.False(ControlMessage.TryReadVideoFormat(ControlMessage.VideoFormat(0, 1080), out _, out _));
        Assert.False(ControlMessage.TryReadVideoFormat(ControlMessage.VideoFormat(1920, -2), out _, out _));
    }
}
