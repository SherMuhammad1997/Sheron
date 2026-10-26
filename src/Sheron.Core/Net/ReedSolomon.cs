namespace Sheron.Core.Net;

/// <summary>
/// Systematic Reed–Solomon code over GF(2⁸) for one block of <see cref="DataShards"/> data shards and
/// <see cref="ParityShards"/> repair shards, all the same length (TRD › Packets). Repair shard i is the sum over data
/// shards j of C[i, j] × shard j, with the Cauchy matrix C[i, j] = 1 / (x_i + y_j), x_i = k + i, y_j = j. Every square
/// part of a Cauchy matrix can be inverted, so any k of the k + m shards rebuild the block.
/// </summary>
public sealed class ReedSolomon
{
    public const int MaxShards = 256;

    /// <summary>Repair rows: <c>_parity[i][j]</c> = C[i, j].</summary>
    private readonly byte[][] _parity;

    public ReedSolomon(int dataShards, int parityShards)
    {
        if (dataShards < 1 || parityShards < 0 || dataShards + parityShards > MaxShards)
            throw new ArgumentOutOfRangeException(nameof(dataShards), $"{dataShards} + {parityShards} shards do not fit one block.");
        DataShards = dataShards;
        ParityShards = parityShards;
        _parity = new byte[parityShards][];
        for (int i = 0; i < parityShards; i++)
        {
            _parity[i] = new byte[dataShards];
            for (int j = 0; j < dataShards; j++) _parity[i][j] = Galois.Inverse((byte)((dataShards + i) ^ j));
        }
    }

    public int DataShards { get; }
    public int ParityShards { get; }

    /// <summary>
    /// Fills the repair shards (indexes k to k + m − 1) from the data shards (0 to k − 1). The shards can be parts of
    /// larger buffers, such as the packets they go out in.
    /// </summary>
    public void Encode(IReadOnlyList<Memory<byte>> shards)
    {
        for (int i = 0; i < ParityShards; i++)
        {
            var target = shards[DataShards + i].Span;
            target.Clear();
            for (int j = 0; j < DataShards; j++) Galois.MultiplyAdd(_parity[i][j], shards[j].Span, target);
        }
    }

    /// <summary>
    /// Rebuilds the missing data shards (null entries among the first k) from any k shards that are there; repair
    /// shards are not rebuilt. False when fewer than k shards are there.
    /// </summary>
    /// <param name="shards">k + m entries, null where a shard is missing; present ones all have the same length.</param>
    public bool Reconstruct(byte[]?[] shards)
    {
        int k = DataShards;
        var missing = new List<int>();
        for (int j = 0; j < k; j++)
            if (shards[j] is null) missing.Add(j);
        if (missing.Count == 0) return true;

        // The first k shards that are there, and the rows of the full code matrix [I; C] that made them.
        var used = new int[k];
        int count = 0, length = 0;
        for (int index = 0; index < shards.Length && count < k; index++)
        {
            if (shards[index] is not { } shard) continue;
            used[count++] = index;
            length = shard.Length;
        }
        if (count < k) return false;

        var matrix = new byte[k, k];
        for (int r = 0; r < k; r++)
        {
            int index = used[r];
            for (int c = 0; c < k; c++)
                matrix[r, c] = index < k ? (byte)(index == c ? 1 : 0) : _parity[index - k][c];
        }
        var inverse = Invert(matrix, k);

        // Data shard j = row j of the inverse × the shards used.
        foreach (int j in missing)
        {
            var rebuilt = new byte[length];
            for (int c = 0; c < k; c++) Galois.MultiplyAdd(inverse[j, c], shards[used[c]], rebuilt);
            shards[j] = rebuilt;
        }
        return true;
    }

    /// <summary>Gauss–Jordan elimination in GF(2⁸). The matrices used here can always be inverted.</summary>
    private static byte[,] Invert(byte[,] matrix, int size)
    {
        var a = (byte[,])matrix.Clone();
        var result = new byte[size, size];
        for (int i = 0; i < size; i++) result[i, i] = 1;

        for (int column = 0; column < size; column++)
        {
            int pivot = column;
            while (a[pivot, column] == 0)
            {
                pivot++;
                if (pivot == size) throw new InvalidOperationException("The shard matrix cannot be inverted.");
            }
            if (pivot != column) SwapRows(a, result, pivot, column, size);

            byte scale = Galois.Inverse(a[column, column]);
            for (int c = 0; c < size; c++)
            {
                a[column, c] = Galois.Multiply(a[column, c], scale);
                result[column, c] = Galois.Multiply(result[column, c], scale);
            }
            for (int r = 0; r < size; r++)
            {
                byte factor = a[r, column];
                if (r == column || factor == 0) continue;
                for (int c = 0; c < size; c++)
                {
                    a[r, c] ^= Galois.Multiply(factor, a[column, c]);
                    result[r, c] ^= Galois.Multiply(factor, result[column, c]);
                }
            }
        }
        return result;
    }

    private static void SwapRows(byte[,] a, byte[,] b, int r1, int r2, int size)
    {
        for (int c = 0; c < size; c++)
        {
            (a[r1, c], a[r2, c]) = (a[r2, c], a[r1, c]);
            (b[r1, c], b[r2, c]) = (b[r2, c], b[r1, c]);
        }
    }
}
