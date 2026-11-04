using System.Buffers.Binary;
using System.Text;
using Sheron.Core.Capture;

namespace Sheron.Core.Session;

/// <summary>Byte 0 of a control message (TRD › Connect by IP › Control messages).</summary>
public enum ControlType : byte
{
    VideoFormat = 1,
    PointerShape = 2,
    PointerPosition = 3,
    Ping = 4,

    // Remote control, viewer → remote PC (Plan step 9, TRD › Input path); built by InputMessage.
    MouseMove = 5,
    MouseButton = 6,
    MouseWheel = 7,
    Key = 8,
    ReleaseAll = 9,

    /// <summary>Viewer → remote PC: send a keyframe now (TRD › Connect by IP › After a lost frame).</summary>
    KeyFrameRequest = 10,

    /// <summary>Viewer → remote PC: the password typed (TRD › Passwords and consent, Plan step 16).</summary>
    AccessRequest = 11,
    /// <summary>Remote PC → viewer: let in, asking the person, wrong, locked, denied or no answer.</summary>
    AccessAnswer = 12,

    // Handled inside the link (Plan step 10, TRD › Automatic quality); never reach the window.
    ClockProbe = 13,
    ClockReply = 14,
    LinkReport = 15,
}

/// <summary>
/// What the viewer saw in the last 250 ms (TRD › Automatic quality): packets expected and received (loss before
/// repair), frames received and lost (after repair), bytes, and the queueing delay and jitter in milliseconds.
/// </summary>
public readonly record struct LinkReport(uint PacketsExpected, uint PacketsReceived, int FramesReceived, int FramesLost,
    uint BytesReceived, int QueueMs, int JitterMs)
{
    /// <summary>Packets lost on the way, 0–1; null when too few were expected to tell.</summary>
    public double? PacketLoss => PacketsExpected < 20 ? null : Math.Clamp(1 - (double)PacketsReceived / PacketsExpected, 0, 1);

    /// <summary>Frames lost after repair, 0–1; null when no frames came or went missing.</summary>
    public double? FrameLoss => FramesReceived + FramesLost == 0 ? null : (double)FramesLost / (FramesReceived + FramesLost);
}

/// <summary>The remote PC's answer to an access request: the result, tries left before the lock, and seconds (time to answer, or lock left).</summary>
public readonly record struct AccessAnswer(Security.AccessResult Result, int TriesLeft, int Seconds);

/// <summary>Control messages that go over the reliable channel: building them, and reading them back with checks.</summary>
public static class ControlMessage
{
    /// <summary>Largest pointer side accepted (Windows pointers are at most 256 pixels, even at large sizes).</summary>
    public const int MaxPointerSide = 512;

    private const int PointerShapeHeader = 1 + 1 + 2 * 6;

    public static ControlType? TypeOf(ReadOnlySpan<byte> message) =>
        message.Length > 0 && Enum.IsDefined((ControlType)message[0]) ? (ControlType)message[0] : null;

    public static byte[] Ping() => [(byte)ControlType.Ping];

    public static byte[] KeyFrameRequest() => [(byte)ControlType.KeyFrameRequest];

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

    /// <summary>
    /// Access request: the password in UTF-8 after a 1-byte length; empty asks the person (Plan step 16b). Secret: never
    /// log it.
    /// </summary>
    public static byte[] AccessRequest(string password)
    {
        byte[] text = Encoding.UTF8.GetBytes(password);
        if (text.Length > byte.MaxValue) throw new ArgumentException("A password is at most 255 bytes in UTF-8.", nameof(password));
        return [(byte)ControlType.AccessRequest, (byte)text.Length, .. text];
    }

    public static bool TryReadAccessRequest(ReadOnlySpan<byte> message, out string password)
    {
        password = "";
        if (message.Length < 2 || message[0] != (byte)ControlType.AccessRequest || message.Length != 2 + message[1])
            return false;
        try
        {
            password = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(message[2..]);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public static byte[] Answer(AccessAnswer answer)
    {
        var message = new byte[5];
        message[0] = (byte)ControlType.AccessAnswer;
        message[1] = (byte)answer.Result;
        message[2] = (byte)Math.Clamp(answer.TriesLeft, 0, byte.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(message.AsSpan(3), (ushort)Math.Clamp(answer.Seconds, 0, ushort.MaxValue));
        return message;
    }

    public static bool TryReadAnswer(ReadOnlySpan<byte> message, out AccessAnswer answer)
    {
        answer = default;
        if (message.Length != 5 || message[0] != (byte)ControlType.AccessAnswer) return false;
        var result = (Security.AccessResult)message[1];
        if (!Enum.IsDefined(result)) return false;
        answer = new AccessAnswer(result, message[2], BinaryPrimitives.ReadUInt16LittleEndian(message[3..]));
        return true;
    }

    public static byte[] ClockProbe(long viewerMicros)
    {
        var message = new byte[9];
        message[0] = (byte)ControlType.ClockProbe;
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(1), viewerMicros);
        return message;
    }

    public static bool TryReadClockProbe(ReadOnlySpan<byte> message, out long viewerMicros)
    {
        viewerMicros = 0;
        if (message.Length != 9 || message[0] != (byte)ControlType.ClockProbe) return false;
        viewerMicros = BinaryPrimitives.ReadInt64LittleEndian(message[1..]);
        return true;
    }

    public static byte[] ClockReply(long viewerMicros, long hostMicros)
    {
        var message = new byte[17];
        message[0] = (byte)ControlType.ClockReply;
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(1), viewerMicros);
        BinaryPrimitives.WriteInt64LittleEndian(message.AsSpan(9), hostMicros);
        return message;
    }

    public static bool TryReadClockReply(ReadOnlySpan<byte> message, out long viewerMicros, out long hostMicros)
    {
        viewerMicros = hostMicros = 0;
        if (message.Length != 17 || message[0] != (byte)ControlType.ClockReply) return false;
        viewerMicros = BinaryPrimitives.ReadInt64LittleEndian(message[1..]);
        hostMicros = BinaryPrimitives.ReadInt64LittleEndian(message[9..]);
        return true;
    }

    public static byte[] Report(LinkReport report)
    {
        var message = new byte[21];
        var span = message.AsSpan();
        span[0] = (byte)ControlType.LinkReport;
        BinaryPrimitives.WriteUInt32LittleEndian(span[1..], report.PacketsExpected);
        BinaryPrimitives.WriteUInt32LittleEndian(span[5..], report.PacketsReceived);
        BinaryPrimitives.WriteUInt16LittleEndian(span[9..], (ushort)Math.Clamp(report.FramesReceived, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(span[11..], (ushort)Math.Clamp(report.FramesLost, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(span[13..], report.BytesReceived);
        BinaryPrimitives.WriteUInt16LittleEndian(span[17..], (ushort)Math.Clamp(report.QueueMs, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(span[19..], (ushort)Math.Clamp(report.JitterMs, 0, ushort.MaxValue));
        return message;
    }

    public static bool TryReadReport(ReadOnlySpan<byte> message, out LinkReport report)
    {
        report = default;
        if (message.Length != 21 || message[0] != (byte)ControlType.LinkReport) return false;
        report = new LinkReport(BinaryPrimitives.ReadUInt32LittleEndian(message[1..]), BinaryPrimitives.ReadUInt32LittleEndian(message[5..]),
            BinaryPrimitives.ReadUInt16LittleEndian(message[9..]), BinaryPrimitives.ReadUInt16LittleEndian(message[11..]),
            BinaryPrimitives.ReadUInt32LittleEndian(message[13..]), BinaryPrimitives.ReadUInt16LittleEndian(message[17..]),
            BinaryPrimitives.ReadUInt16LittleEndian(message[19..]));
        // A late packet can make "received" pass "expected" in one report; the loss figure is clamped instead.
        return true;
    }
}
