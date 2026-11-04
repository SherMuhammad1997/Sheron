namespace Sheron.Core.Net;

/// <summary>How many repair shards a block gets for the link's loss rate (TRD › Packets).</summary>
public static class FecPolicy
{
    /// <summary>Loss rate never planned below.</summary>
    public const double MinLossRate = 0.01;

    /// <summary>Loss rate assumed until the viewer's first report (Plan step 10): a lost frame at the start costs a keyframe.</summary>
    public const double FirstLossRate = 0.05;

    /// <summary>Highest loss rate planned for; beyond it every block gets as many repair shards as data shards.</summary>
    public const double MaxLossRate = 0.5;

    /// <summary>Chance a block may still be lost, on a link with a round trip of <see cref="FastRoundTrip"/> or less.</summary>
    public const double TargetBlockLoss = 0.001;

    public static readonly TimeSpan FastRoundTrip = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A lost frame costs a round trip to repair with a keyframe, so a slower link plans for fewer lost blocks:
    /// <see cref="TargetBlockLoss"/> × 100 ms ÷ round trip, at most <see cref="TargetBlockLoss"/>, at least a tenth of it.
    /// </summary>
    public static double TargetFor(TimeSpan? roundTrip) => roundTrip is { } rtt && rtt > FastRoundTrip
        ? Math.Max(TargetBlockLoss / 10, TargetBlockLoss * FastRoundTrip.TotalMilliseconds / rtt.TotalMilliseconds)
        : TargetBlockLoss;

    /// <summary>
    /// The fewest repair shards m for which a block of <paramref name="dataShards"/> + m shards, each lost with
    /// <paramref name="lossRate"/>, loses more than m with a chance of at most <paramref name="targetBlockLoss"/>
    /// (<see cref="TargetBlockLoss"/> when not given). At least 1, at most as many as the data shards.
    /// </summary>
    public static int ParityShards(int dataShards, double lossRate, double targetBlockLoss = TargetBlockLoss)
    {
        double p = Math.Clamp(lossRate, MinLossRate, MaxLossRate);
        for (int m = 1; m < dataShards; m++)
            if (ChanceOfMoreThan(m, dataShards + m, p) <= targetBlockLoss) return m;
        return dataShards;
    }

    /// <summary>The chance that more than <paramref name="lost"/> of <paramref name="shards"/> are lost (binomial).</summary>
    public static double ChanceOfMoreThan(int lost, int shards, double p)
    {
        double atMost = 0;
        double term = Math.Pow(1 - p, shards);   // exactly 0 lost
        for (int i = 0; i <= lost; i++)
        {
            atMost += term;
            term *= (double)(shards - i) / (i + 1) * p / (1 - p);
        }
        return Math.Max(0, 1 - atMost);
    }
}
