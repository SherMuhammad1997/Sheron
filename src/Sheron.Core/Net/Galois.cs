using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Sheron.Core.Net;

/// <summary>
/// Arithmetic in GF(2⁸), the field of bytes used by the repair shards (TRD › Packets). Adding is XOR; multiplying
/// works through logarithms to the generator 2, modulo the polynomial x⁸ + x⁴ + x³ + x² + 1.
/// <see cref="MultiplyAdd"/> does 32 or 16 bytes per step where the processor can (AVX2, SSSE3): c × x is
/// c × (low 4 bits of x) + c × (high 4 bits of x), each looked up in a 16-entry table with one byte shuffle.
/// Elsewhere it looks each byte up in a 256-entry product table.
/// </summary>
public static class Galois
{
    private const int Polynomial = 0x11D;

    private static readonly byte[] Exp = new byte[510];
    private static readonly byte[] Log = new byte[256];
    private static readonly byte[][] Products = new byte[256][];
    /// <summary>For each factor c: c × 0..15, and c × (0..15 shifted up 4 bits).</summary>
    private static readonly Vector128<byte>[] LowProducts = new Vector128<byte>[256];
    private static readonly Vector128<byte>[] HighProducts = new Vector128<byte>[256];

    static Galois()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = Exp[i + 255] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256) x ^= Polynomial;
        }
        Span<byte> low = stackalloc byte[16], high = stackalloc byte[16];
        for (int a = 0; a < 256; a++)
        {
            var row = Products[a] = new byte[256];
            if (a != 0)
                for (int b = 1; b < 256; b++) row[b] = Exp[Log[a] + Log[b]];
            for (int n = 0; n < 16; n++)
            {
                low[n] = row[n];
                high[n] = row[n << 4];
            }
            LowProducts[a] = Vector128.Create<byte>(low);
            HighProducts[a] = Vector128.Create<byte>(high);
        }
    }

    public static byte Multiply(byte a, byte b) => Products[a][b];

    /// <summary>The byte that multiplies <paramref name="a"/> to 1. There is none for 0.</summary>
    public static byte Inverse(byte a)
    {
        if (a == 0) throw new DivideByZeroException("0 has no inverse in GF(2⁸).");
        return Exp[255 - Log[a]];
    }

    /// <summary><paramref name="target"/> += <paramref name="factor"/> × <paramref name="source"/>, byte by byte.</summary>
    public static void MultiplyAdd(byte factor, ReadOnlySpan<byte> source, Span<byte> target)
    {
        if (factor == 0) return;
        if (target.Length < source.Length) throw new ArgumentException("The target is shorter than the source.", nameof(target));
        ref byte from = ref MemoryMarshal.GetReference(source);
        ref byte to = ref MemoryMarshal.GetReference(target);
        int length = source.Length, i = 0;

        if (Avx2.IsSupported)
        {
            var low = Vector256.Create(LowProducts[factor], LowProducts[factor]);
            var high = Vector256.Create(HighProducts[factor], HighProducts[factor]);
            var nibble = Vector256.Create((byte)0x0F);
            for (; i <= length - 32; i += 32)
            {
                var x = Vector256.LoadUnsafe(ref from, (nuint)i);
                var product = Avx2.Shuffle(low, x & nibble) ^ Avx2.Shuffle(high, Vector256.ShiftRightLogical(x.AsUInt16(), 4).AsByte() & nibble);
                (Vector256.LoadUnsafe(ref to, (nuint)i) ^ product).StoreUnsafe(ref to, (nuint)i);
            }
        }
        if (Ssse3.IsSupported)
        {
            var low = LowProducts[factor];
            var high = HighProducts[factor];
            var nibble = Vector128.Create((byte)0x0F);
            for (; i <= length - 16; i += 16)
            {
                var x = Vector128.LoadUnsafe(ref from, (nuint)i);
                var product = Ssse3.Shuffle(low, x & nibble) ^ Ssse3.Shuffle(high, Vector128.ShiftRightLogical(x.AsUInt16(), 4).AsByte() & nibble);
                (Vector128.LoadUnsafe(ref to, (nuint)i) ^ product).StoreUnsafe(ref to, (nuint)i);
            }
        }

        var row = Products[factor];
        for (; i < length; i++) Unsafe.Add(ref to, i) ^= row[Unsafe.Add(ref from, i)];
    }
}
