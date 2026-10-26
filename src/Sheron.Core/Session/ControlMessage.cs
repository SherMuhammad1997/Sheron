using System.Buffers.Binary;
using Sheron.Core.Capture;

namespace Sheron.Core.Session;

/// <summary>Byte 0 of a control message (TRD › Connect by IP › Control messages).</summary>
public enum ControlType : byte
{
    VideoFormat = 1,
    PointerShape = 2,
    PointerPosition = 3,
    Ping = 4,
}

/// <summary>Control messages that go over the reliable channel: building them, and reading them back with checks.</summary>
public static class ControlMessage
{
    /// <summary>Largest pointer side accepted (Windows pointers are at most 256 pixels, even at large sizes).</summary>
    public const int MaxPointerSide = 512;

    private const int PointerShapeHeader = 1 + 1 + 2 * 6;

    public static ControlType? TypeOf(ReadOnlySpan<byte> message) =>
        message.Length > 0 && Enum.IsDefined((ControlType)message[0]) ? (ControlType)message[0] : null;

    public static byte[] Ping() => [(byte)ControlType.Ping];

    public static byte[] VideoFormat(int width, int height)
    {
        var message = new byte[9];
        message[0] = (byte)ControlType.VideoFormat;
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(1), width);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(5), height);
        return message;
    }

    public static bool TryReadVideoFormat(ReadOnlySpan<byte> message, out int width, out int height)
    {
        width = height = 0;
        if (message.Length != 9 || message[0] != (byte)ControlType.VideoFormat) return false;
        width = BinaryPrimitives.ReadInt32LittleEndian(message[1..]);
        height = BinaryPrimitives.ReadInt32LittleEndian(message[5..]);
        // H.264 and NV12 need even sizes; 16384 is beyond any screen.
        return width is > 0 and <= 16384 && height is > 0 and <= 16384 && width % 2 == 0 && height % 2 == 0;
    }

    public static byte[] Pointer(PointerShape shape)
    {
        var message = new byte[PointerShapeHeader + shape.Pixels.Length];
        message[0] = (byte)ControlType.PointerShape;
        message[1] = (byte)shape.Kind;
        var numbers = message.AsSpan(2);
        BinaryPrimitives.WriteUInt16LittleEndian(numbers, (ushort)shape.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(numbers[2..], (ushort)shape.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(numbers[4..], (ushort)shape.Pitch);
        BinaryPrimitives.WriteUInt16LittleEndian(numbers[6..], (ushort)shape.HotSpotX);
        BinaryPrimitives.WriteUInt16LittleEndian(numbers[8..], (ushort)shape.HotSpotY);
        BinaryPrimitives.WriteUInt16LittleEndian(numbers[10..], 0);
        shape.Pixels.CopyTo(message, PointerShapeHeader);
        return message;
    }

    /// <summary>
    /// Reads a pointer shape and checks it can be drawn safely: a known kind, sides of 1–512 pixels, rows long
    /// enough for the width, and pixels for every row.
    /// </summary>
    public static bool TryReadPointerShape(ReadOnlySpan<byte> message, out PointerShape? shape)
    {
        shape = null;
        if (message.Length < PointerShapeHeader || message[0] != (byte)ControlType.PointerShape) return false;
        var kind = (PointerShapeKind)message[1];
        var numbers = message[2..];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(numbers);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(numbers[2..]);
        int pitch = BinaryPrimitives.ReadUInt16LittleEndian(numbers[4..]);
        int hotX = BinaryPrimitives.ReadUInt16LittleEndian(numbers[6..]);
        int hotY = BinaryPrimitives.ReadUInt16LittleEndian(numbers[8..]);
        var pixels = message[PointerShapeHeader..];

        int rowBytes = kind switch
        {
            PointerShapeKind.Monochrome => (width + 7) / 8,
            PointerShapeKind.Color or PointerShapeKind.MaskedColor => width * 4,
            _ => -1,
        };
        // A monochrome shape holds two masks, so it is twice as tall as the pointer.
        int maxHeight = kind == PointerShapeKind.Monochrome ? 2 * MaxPointerSide : MaxPointerSide;
        if (rowBytes < 0 || width is < 1 or > MaxPointerSide || height < 1 || height > maxHeight
            || (kind == PointerShapeKind.Monochrome && height % 2 != 0)
            || pitch < rowBytes || pixels.Length < pitch * height)
            return false;
        shape = new PointerShape(kind, width, height, pitch, hotX, hotY, pixels.ToArray());
        return true;
    }

    public static byte[] Pointer(PointerPosition position)
    {
        var message = new byte[10];
        message[0] = (byte)ControlType.PointerPosition;
        message[1] = position.Visible ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(2), position.X);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(6), position.Y);
        return message;
    }

    public static bool TryReadPointerPosition(ReadOnlySpan<byte> message, out PointerPosition position)
    {
        position = default;
        if (message.Length != 10 || message[0] != (byte)ControlType.PointerPosition) return false;
        position = new PointerPosition(message[1] != 0,
            BinaryPrimitives.ReadInt32LittleEndian(message[2..]), BinaryPrimitives.ReadInt32LittleEndian(message[6..]));
        return true;
    }
}
