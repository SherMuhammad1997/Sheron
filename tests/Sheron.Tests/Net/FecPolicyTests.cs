using Sheron.Core.Net;

namespace Sheron.Tests.Net;

public class FecPolicyTests
{
    [Theory]
    [InlineData(15, 0.05, 5)]   // TRD › Packets: at 5 % loss a 15-shard frame gets 5 repair shards
    [InlineData(32, 0.05, 7)]
    [InlineData(15, 0.01, 2)]
    [InlineData(15, 0.0, 2)]    // never below the 1 % floor
    [InlineData(1, 0.05, 1)]
    [InlineData(10, 0.2, 10)]   // capped at one repair shard per data shard
    public void Repair_shards_follow_the_loss_rate(int dataShards, double lossRate, int expected)
    {
        Assert.Equal(expected, FecPolicy.ParityShards(dataShards, lossRate));
    }

    [Fact]
    public void The_count_chosen_is_the_fewest_that_keeps_block_loss_at_or_below_the_target()
    {
        foreach (double p in new[] { 0.01, 0.03, 0.05, 0.1 })
        for (int k = 2; k <= 32; k++)
        {
            int m = FecPolicy.ParityShards(k, p);
            if (m == k) continue;
            Assert.True(FecPolicy.ChanceOfMoreThan(m, k + m, p) <= FecPolicy.TargetBlockLoss, $"k {k}, p {p}: m {m} is too few");
            if (m > 1) Assert.True(FecPolicy.ChanceOfMoreThan(m - 1, k + m - 1, p) > FecPolicy.TargetBlockLoss, $"k {k}, p {p}: m {m} is more than needed");
        }
    }

    [Fact]
    public void A_slower_link_plans_for_fewer_lost_blocks_and_so_gets_more_repair_shards()
    {
        Assert.Equal(FecPolicy.TargetBlockLoss, FecPolicy.TargetFor(null));
        Assert.Equal(FecPolicy.TargetBlockLoss, FecPolicy.TargetFor(TimeSpan.FromMilliseconds(40)));
        Assert.Equal(FecPolicy.TargetBlockLoss / 6, FecPolicy.TargetFor(TimeSpan.FromMilliseconds(600)), 10);
        Assert.Equal(FecPolicy.TargetBlockLoss / 10, FecPolicy.TargetFor(TimeSpan.FromSeconds(5)), 10);
        Assert.True(FecPolicy.ParityShards(15, 0.05, FecPolicy.TargetFor(TimeSpan.FromMilliseconds(600))) > FecPolicy.ParityShards(15, 0.05));
    }
}
