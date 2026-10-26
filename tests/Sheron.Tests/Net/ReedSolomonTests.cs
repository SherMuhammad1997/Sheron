using Sheron.Core.Net;

namespace Sheron.Tests.Net;

public class ReedSolomonTests
{
    [Fact]
    public void Every_byte_but_zero_times_its_inverse_is_one()
    {
        for (int a = 1; a < 256; a++)
            Assert.Equal(1, Galois.Multiply((byte)a, Galois.Inverse((byte)a)));
    }

    [Fact]
    public void Multiplying_spreads_over_adding()
    {
        var random = new Random(8);
        for (int i = 0; i < 1000; i++)
        {
            byte a = (byte)random.Next(256), b = (byte)random.Next(256), c = (byte)random.Next(256);
            Assert.Equal(Galois.Multiply(a, b), Galois.Multiply(b, a));
            Assert.Equal(Galois.Multiply(a, (byte)(b ^ c)), Galois.Multiply(a, b) ^ Galois.Multiply(a, c));
        }
    }

    /// <summary>The fast path (32 or 16 bytes per step) and the byte-by-byte tail give the same as one multiply per byte.</summary>
    [Fact]
    public void Multiply_add_matches_byte_by_byte_multiplying_at_every_length()
    {
        var random = new Random(5);
        for (int length = 0; length <= 100; length++)
        for (int factor = 0; factor < 256; factor += 17)
        {
            var source = new byte[length];
            var target = new byte[length];
            random.NextBytes(source);
            random.NextBytes(target);
            var expected = target.Select((t, i) => (byte)(t ^ Galois.Multiply((byte)factor, source[i]))).ToArray();

            Galois.MultiplyAdd((byte)factor, source, target);

            Assert.Equal(expected, target);
        }
    }

    [Theory]
    [InlineData(10, 4)]
    [InlineData(1, 1)]
    [InlineData(32, 7)]
    [InlineData(32, 32)]
    public void Any_k_shards_rebuild_the_data(int k, int m)
    {
        var random = new Random(k * 100 + m);
        var original = Block(k, m, 64, random);
        for (int round = 0; round < 50; round++)
        {
            // Lose m shards chosen at random (data, repair or both).
            var shards = original.Select(s => (byte[]?)s.ToArray()).ToArray();
            foreach (int lost in Enumerable.Range(0, k + m).OrderBy(_ => random.Next()).Take(m)) shards[lost] = null;

            Assert.True(new ReedSolomon(k, m).Reconstruct(shards));
            for (int j = 0; j < k; j++) Assert.Equal(original[j], shards[j]);
        }
    }

    [Fact]
    public void Losing_all_data_shards_is_rebuilt_from_repair_shards_alone()
    {
        var original = Block(5, 5, 100, new Random(3));
        var shards = original.Select((s, i) => i < 5 ? null : (byte[]?)s.ToArray()).ToArray();

        Assert.True(new ReedSolomon(5, 5).Reconstruct(shards));
        for (int j = 0; j < 5; j++) Assert.Equal(original[j], shards[j]);
    }

    [Fact]
    public void One_shard_too_few_cannot_be_rebuilt()
    {
        var original = Block(10, 4, 32, new Random(4));
        var shards = original.Select(s => (byte[]?)s).ToArray();
        for (int j = 0; j < 5; j++) shards[j] = null;

        Assert.False(new ReedSolomon(10, 4).Reconstruct(shards));
    }

    /// <summary>k random data shards and m repair shards made from them.</summary>
    private static byte[][] Block(int k, int m, int size, Random random)
    {
        var shards = new byte[k + m][];
        for (int j = 0; j < k + m; j++)
        {
            shards[j] = new byte[size];
            if (j < k) random.NextBytes(shards[j]);
        }
        new ReedSolomon(k, m).Encode(shards.Select(s => s.AsMemory()).ToArray());
        return shards;
    }
}
