using Sheron.Core.Capture;
using Sheron.Core.Session;

namespace Sheron.Tests.Session;

/// <summary>Plan step 9 in Core: input messages, where a click lands, and letting go of held keys (TRD › Input path).</summary>
public class InputTests
{
    [Fact]
    public void Each_input_message_reads_back_as_it_was_written()
    {
        var at = new AbsolutePoint(12_345, 65_535);
        Assert.True(InputMessage.TryReadMove(InputMessage.Move(at), out var moved));
        Assert.Equal(at, moved);

        var click = new MouseButtonInput(MouseButton.Forward, true, at);
        Assert.True(InputMessage.TryReadButton(InputMessage.Button(click), out var clickBack));
        Assert.Equal(click, clickBack);

        var wheel = new MouseWheelInput(-240, true, at);
        Assert.True(InputMessage.TryReadWheel(InputMessage.Wheel(wheel), out var wheelBack));
        Assert.Equal(wheel, wheelBack);

        var key = new KeyInput(0x4B, Extended: true, Down: false);   // left arrow, let go
        Assert.True(InputMessage.TryReadKey(InputMessage.Key(key), out var keyBack));
        Assert.Equal(key, keyBack);

        Assert.Equal(ControlType.ReleaseAll, ControlMessage.TypeOf(InputMessage.ReleaseAll()));
    }

    [Fact]
    public void Bad_input_messages_are_refused()
    {
        Assert.False(InputMessage.TryReadButton([(byte)ControlType.MouseButton, 9, 1, 0, 0, 0, 0], out _));   // no button 9
        Assert.False(InputMessage.TryReadKey(InputMessage.Key(new KeyInput(0, false, true)), out _));          // no key 0
        Assert.False(InputMessage.TryReadKey([(byte)ControlType.Key, 0x00, 0x01, 0, 1], out _));                // scan code 256
        Assert.False(InputMessage.TryReadMove([(byte)ControlType.MouseMove, 1, 2], out _));                     // too short
    }

    /// <summary>
    /// Every remote pixel's absolute value turns back into that pixel, whether Windows rounds down or to the nearest
    /// (SendInput documents only the 0–65535 range).
    /// </summary>
    [Theory]
    [InlineData(1920)]
    [InlineData(1366)]
    [InlineData(2560)]
    [InlineData(3840)]
    public void Each_remote_pixel_comes_back_from_its_absolute_value(int size)
    {
        for (int pixel = 0; pixel < size; pixel++)
        {
            ushort absolute = ScreenMapping.Absolute(pixel, size);
            Assert.Equal(pixel, (int)(absolute * (long)size / 65536));
            Assert.Equal(pixel, (int)Math.Round(absolute * (double)size / 65536, MidpointRounding.AwayFromZero));
        }
    }

    [Fact]
    public void A_point_in_the_window_lands_on_the_matching_remote_pixel()
    {
        // A 1920 × 1080 screen drawn at 960 × 540, 20 pixels down from the window's top (bars above and below).
        var picture = new ScreenRect(0, 20, 960, 560);

        Assert.Equal(ScreenMapping.Absolute(0, 1920), ScreenMapping.ToAbsolute(0, 20, picture, 1920, 1080).X);
        Assert.Equal(new AbsolutePoint(ScreenMapping.Absolute(1000, 1920), ScreenMapping.Absolute(500, 1080)),
            ScreenMapping.ToAbsolute(500, 270, picture, 1920, 1080));
        // On the bars: the nearest edge.
        Assert.Equal(new AbsolutePoint(ScreenMapping.Absolute(1919, 1920), ScreenMapping.Absolute(0, 1080)),
            ScreenMapping.ToAbsolute(5000, 3, picture, 1920, 1080));
    }

    [Fact]
    public void Release_all_lets_go_of_exactly_what_is_held()
    {
        var held = new HeldInput();
        var at = new AbsolutePoint(1, 1);
        held.Track(new KeyInput(0x1D, false, true));    // left Ctrl down
        held.Track(new KeyInput(0x1D, false, true));    // repeat: still one key
        held.Track(new KeyInput(0x2E, false, true));    // C down
        held.Track(new KeyInput(0x2E, false, false));   // C up
        held.Track(new MouseButtonInput(MouseButton.Left, true, at));

        var (keys, buttons) = held.ReleaseAll();

        Assert.Equal(new KeyInput(0x1D, false, false), Assert.Single(keys));
        Assert.Equal(MouseButton.Left, Assert.Single(buttons));
        Assert.Equal(0, held.Count);
    }
}
