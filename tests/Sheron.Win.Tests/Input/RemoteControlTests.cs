using System.Runtime.InteropServices;
using Sheron.Core.Capture;
using Sheron.Win.Input;
using Sheron.Win.View;

namespace Sheron.Win.Tests.Input;

/// <summary>Plan step 9 on Windows: the SendInput layout and the remote pointer as a Windows cursor (TRD › Input path).</summary>
public class RemoteControlTests
{
    /// <summary>SendInput refuses the whole call if the INPUT size is wrong; it is 40 bytes on 64-bit Windows.</summary>
    [Fact]
    public void The_input_record_has_the_size_Windows_expects()
    {
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(40, Marshal.SizeOf<InputInjector.Native>());
    }

    [Fact]
    public void Every_kind_of_pointer_shape_becomes_a_cursor()
    {
        // An I-beam-like monochrome shape (two 32 × 32 masks), a colour arrow with alpha, and a masked-colour shape.
        var mono = new byte[4 * 64];
        for (int y = 0; y < 32; y++) mono[y * 4] = 0xFF;            // AND: see-through on the left
        for (int y = 32; y < 64; y++) mono[y * 4 + 2] = 0x01;       // XOR: an inverting column
        var shapes = new[]
        {
            new PointerShape(PointerShapeKind.Monochrome, 32, 64, 4, 15, 16, mono),
            new PointerShape(PointerShapeKind.Color, 32, 32, 128, 0, 0, Enumerable.Repeat((byte)0x80, 128 * 32).ToArray()),
            new PointerShape(PointerShapeKind.MaskedColor, 24, 24, 96, 3, 3, Enumerable.Repeat((byte)0xFF, 96 * 24).ToArray()),
        };

        foreach (var shape in shapes)
        {
            using var cursor = RemoteCursor.From(shape);
            Assert.NotEqual(IntPtr.Zero, cursor.Cursor.Handle);
        }
    }
}
