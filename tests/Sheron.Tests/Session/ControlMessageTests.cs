using Sheron.Core.Capture;
using Sheron.Core.Security;
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

    [Fact]
    public void Access_request_and_answer_read_back_and_damaged_ones_are_refused()
    {
        Assert.True(ControlMessage.TryReadAccessRequest(ControlMessage.AccessRequest("pässwort 1"), out string password));
        Assert.Equal("pässwort 1", password);
        var answer = new AccessAnswer(AccessResult.Locked, 0, 27);
        Assert.True(ControlMessage.TryReadAnswer(ControlMessage.Answer(answer), out var back));
        Assert.Equal(answer, back);

        // No password: ask the person (Plan step 16b).
        Assert.True(ControlMessage.TryReadAccessRequest(ControlMessage.AccessRequest(""), out string none));
        Assert.Equal("", none);
        Assert.Throws<ArgumentException>(() => ControlMessage.AccessRequest(new string('x', 256)));
        Assert.False(ControlMessage.TryReadAccessRequest([(byte)ControlType.AccessRequest, 5, (byte)'a'], out _));   // shorter than its length
        Assert.False(ControlMessage.TryReadAccessRequest([(byte)ControlType.AccessRequest, 1, 0xFF], out _));       // not UTF-8
        Assert.False(ControlMessage.TryReadAnswer([(byte)ControlType.AccessAnswer, 9, 0, 0, 0], out _));            // unknown result
        Assert.False(ControlMessage.TryReadAnswer([(byte)ControlType.AccessAnswer, 1, 0, 0], out _));
    }

    [Fact]
    public void Names_go_with_the_request_and_with_Let_in_and_old_messages_still_read()
    {
        Assert.True(ControlMessage.TryReadAccessRequest(ControlMessage.AccessRequest("", "Sher-Laptop"), out string password, out string name));
        Assert.Equal(("", "Sher-Laptop"), (password, name));
        // Before Plan step 15a: the password only, no name.
        Assert.True(ControlMessage.TryReadAccessRequest([(byte)ControlType.AccessRequest, 1, (byte)'a'], out password, out name));
        Assert.Equal(("a", ""), (password, name));
        Assert.False(ControlMessage.TryReadAccessRequest([(byte)ControlType.AccessRequest, 0, 3, (byte)'a'], out _, out _));   // name shorter than its length

        var letIn = new AccessAnswer(AccessResult.LetIn, 3, 0, "Office-PC");
        Assert.True(ControlMessage.TryReadAnswer(ControlMessage.Answer(letIn), out var back));
        Assert.Equal(letIn, back);
        // The name goes only with Let in.
        Assert.True(ControlMessage.TryReadAnswer(ControlMessage.Answer(new AccessAnswer(AccessResult.Denied, 0, 0, "Office-PC")), out back));
        Assert.Null(back.Name);
        Assert.True(ControlMessage.TryReadAnswer([(byte)ControlType.AccessAnswer, 8, 0, 0, 0], out back));   // 5 bytes, as before
        Assert.Equal(new AccessAnswer(AccessResult.Busy, 0, 0), back);
    }

    [Theory]
    [InlineData("Office-PC", "Office-PC")]
    [InlineData("  Sher\tLaptop \n", "SherLaptop")]
    [InlineData("A name that is much longer than thirty-two characters", "A name that is much longer than ")]
    [InlineData(null, "")]
    public void A_PC_name_is_cleaned_and_cut_to_32_characters(string? given, string kept)
    {
        Assert.Equal(kept.Trim(), PcName.Clean(given));
    }

    [Fact]
    public void A_PC_name_is_cut_by_whole_characters_to_64_bytes()
    {
        string cleaned = PcName.Clean(new string('ق', 40));   // 2 bytes each in UTF-8
        Assert.Equal(32, cleaned.Length);
        string emoji = PcName.Clean(string.Concat(Enumerable.Repeat("😀", 20)));   // 4 bytes each
        Assert.Equal(16, System.Globalization.StringInfo.ParseCombiningCharacters(emoji).Length);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(emoji) <= PcName.MaxBytes);
    }

    [Fact]
    public void Clock_and_report_messages_read_back()
    {
        Assert.True(ControlMessage.TryReadClockProbe(ControlMessage.ClockProbe(123_456_789), out long sent));
        Assert.Equal(123_456_789, sent);
        Assert.True(ControlMessage.TryReadClockReply(ControlMessage.ClockReply(5, -7), out long viewer, out long host));
        Assert.Equal((5L, -7L), (viewer, host));

        var report = new LinkReport(400, 380, 15, 1, 450_000, 12, 40);
        Assert.True(ControlMessage.TryReadReport(ControlMessage.Report(report), out var back));
        Assert.Equal(report, back);
        Assert.Equal(0.05, back.PacketLoss!.Value, 6);
        Assert.Equal(1.0 / 16, back.FrameLoss!.Value, 6);
        Assert.Null(new LinkReport(10, 10, 0, 0, 0, 0, 0).PacketLoss);   // too few to tell
        Assert.False(ControlMessage.TryReadReport(ControlMessage.Report(report).AsSpan(0, 20), out _));
    }
}
