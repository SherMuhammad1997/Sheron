using System.Buffers.Binary;
using Sheron.Core.Capture;

namespace Sheron.Core.Session;

/// <summary>Mouse buttons as the input messages number them (TRD › Connect by IP › Control messages).</summary>
public enum MouseButton : byte
{
    Left = 1,
    Right = 2,
    Middle = 3,
    Back = 4,
    Forward = 5,
}

/// <summary>A position on the remote screen in SendInput's absolute units: 0–65535 across each side.</summary>
public readonly record struct AbsolutePoint(ushort X, ushort Y);

public readonly record struct MouseButtonInput(MouseButton Button, bool Down, AbsolutePoint At);

/// <summary>Wheel turn: 120 per notch; positive is away from the user (up) or to the right.</summary>
public readonly record struct MouseWheelInput(short Delta, bool Horizontal, AbsolutePoint At);

/// <summary>A key by its place on the keyboard (scan code), so the remote PC's own layout gives the character.</summary>
public readonly record struct KeyInput(ushort ScanCode, bool Extended, bool Down);

/// <summary>The input control messages of Plan step 9 (TRD › Input path): building them and reading them back with checks.</summary>
public static class InputMessage
{
    public static byte[] Move(AbsolutePoint at)
    {
        var message = new byte[5];
        message[0] = (byte)ControlType.MouseMove;
        WritePoint(message.AsSpan(1), at);
        return message;
    }

    public static bool TryReadMove(ReadOnlySpan<byte> message, out AbsolutePoint at)
    {
        at = default;
        if (message.Length != 5 || message[0] != (byte)ControlType.MouseMove) return false;
        at = ReadPoint(message[1..]);
        return true;
    }

    public static byte[] Button(MouseButtonInput input)
    {
        var message = new byte[7];
        message[0] = (byte)ControlType.MouseButton;
        message[1] = (byte)input.Button;
        message[2] = input.Down ? (byte)1 : (byte)0;
        WritePoint(message.AsSpan(3), input.At);
        return message;
    }

    public static bool TryReadButton(ReadOnlySpan<byte> message, out MouseButtonInput input)
    {
        input = default;
        if (message.Length != 7 || message[0] != (byte)ControlType.MouseButton || !Enum.IsDefined((MouseButton)message[1])) return false;
        input = new MouseButtonInput((MouseButton)message[1], message[2] != 0, ReadPoint(message[3..]));
        return true;
    }

    public static byte[] Wheel(MouseWheelInput input)
    {
        var message = new byte[8];
        message[0] = (byte)ControlType.MouseWheel;
        BinaryPrimitives.WriteInt16LittleEndian(message.AsSpan(1), input.Delta);
        message[3] = input.Horizontal ? (byte)1 : (byte)0;
        WritePoint(message.AsSpan(4), input.At);
        return message;
    }

    public static bool TryReadWheel(ReadOnlySpan<byte> message, out MouseWheelInput input)
    {
        input = default;
        if (message.Length != 8 || message[0] != (byte)ControlType.MouseWheel) return false;
        input = new MouseWheelInput(BinaryPrimitives.ReadInt16LittleEndian(message[1..]), message[3] != 0, ReadPoint(message[4..]));
        return true;
    }

    public static byte[] Key(KeyInput input)
    {
        var message = new byte[5];
        message[0] = (byte)ControlType.Key;
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(1), input.ScanCode);
        message[3] = input.Extended ? (byte)1 : (byte)0;
        message[4] = input.Down ? (byte)1 : (byte)0;
        return message;
    }

    /// <summary>Scan codes are 1–255 (one byte, plus the extended flag); 0 is no key.</summary>
    public static bool TryReadKey(ReadOnlySpan<byte> message, out KeyInput input)
    {
        input = default;
        if (message.Length != 5 || message[0] != (byte)ControlType.Key) return false;
        ushort scanCode = BinaryPrimitives.ReadUInt16LittleEndian(message[1..]);
        if (scanCode is 0 or > 0xFF) return false;
        input = new KeyInput(scanCode, message[3] != 0, message[4] != 0);
        return true;
    }

    public static byte[] ReleaseAll() => [(byte)ControlType.ReleaseAll];

    private static void WritePoint(Span<byte> to, AbsolutePoint at)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(to, at.X);
        BinaryPrimitives.WriteUInt16LittleEndian(to[2..], at.Y);
    }

    private static AbsolutePoint ReadPoint(ReadOnlySpan<byte> from) =>
        new(BinaryPrimitives.ReadUInt16LittleEndian(from), BinaryPrimitives.ReadUInt16LittleEndian(from[2..]));
}

/// <summary>From a point in the viewer's video window to the remote screen (TRD › Input path).</summary>
public static class ScreenMapping
{
    /// <summary>
    /// The remote pixel under (<paramref name="x"/>, <paramref name="y"/>) in the window, where the remote screen of
    /// <paramref name="remoteWidth"/> × <paramref name="remoteHeight"/> is drawn into <paramref name="picture"/>.
    /// Points on the bars around the picture go to the nearest edge.
    /// </summary>
    public static AbsolutePoint ToAbsolute(int x, int y, ScreenRect picture, int remoteWidth, int remoteHeight)
    {
        int remoteX = Pixel(x - picture.Left, picture.Width, remoteWidth);
        int remoteY = Pixel(y - picture.Top, picture.Height, remoteHeight);
        return new AbsolutePoint(Absolute(remoteX, remoteWidth), Absolute(remoteY, remoteHeight));
    }

    /// <summary>
    /// Remote pixel <paramref name="pixel"/> in absolute units: the smallest value at or past the pixel's left (top)
    /// edge. Turned back into pixels it is at most size / 65536 of a pixel past the edge (under 0.06 for 3840 pixels),
    /// so Windows lands on that pixel whether it rounds down or to the nearest.
    /// </summary>
    public static ushort Absolute(int pixel, int size) =>
        size <= 0 ? (ushort)0 : (ushort)Math.Clamp((65536L * pixel + size - 1) / size, 0, 65535);

    private static int Pixel(int offset, int drawnSize, int remoteSize)
    {
        if (drawnSize <= 0 || remoteSize <= 0) return 0;
        return (int)Math.Clamp((long)offset * remoteSize / drawnSize, 0, remoteSize - 1);
    }
}

/// <summary>
/// Which keys and mouse buttons the viewer holds down on the remote PC, so they can all be let go when the viewer
/// asks or the session ends (TRD › Input path › Stuck keys). Used by one thread at a time.
/// </summary>
public sealed class HeldInput
{
    private readonly HashSet<(ushort ScanCode, bool Extended)> _keys = [];
    private readonly HashSet<MouseButton> _buttons = [];

    public int Count => _keys.Count + _buttons.Count;

    public void Track(KeyInput key)
    {
        if (key.Down) _keys.Add((key.ScanCode, key.Extended));
        else _keys.Remove((key.ScanCode, key.Extended));
    }

    public void Track(MouseButtonInput button)
    {
        if (button.Down) _buttons.Add(button.Button);
        else _buttons.Remove(button.Button);
    }

    /// <summary>The key-ups and button-ups that let go of everything held; afterwards nothing is held.</summary>
    public (List<KeyInput> Keys, List<MouseButton> Buttons) ReleaseAll()
    {
        var keys = _keys.Select(k => new KeyInput(k.ScanCode, k.Extended, Down: false)).ToList();
        var buttons = _buttons.ToList();
        _keys.Clear();
        _buttons.Clear();
        return (keys, buttons);
    }
}
