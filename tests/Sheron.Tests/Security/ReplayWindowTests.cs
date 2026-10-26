using Sheron.Core.Security;

namespace Sheron.Tests.Security;

public class ReplayWindowTests
{
    private static bool Take(ReplayWindow window, ulong counter)
    {
        if (!window.IsNew(counter)) return false;
        window.Mark(counter);
        return true;
    }

    [Fact]
    public void Each_counter_is_taken_once_in_any_order_inside_the_window()
    {
        var window = new ReplayWindow();
        var order = Enumerable.Range(0, 3000).Select(i => (ulong)i).OrderBy(i => (i * 7919) % 3000).ToList();
        int taken = 0;
        ulong highest = 0;
        foreach (ulong counter in order)
        {
            bool inside = counter + ReplayWindow.Size > highest;
            Assert.Equal(inside, Take(window, counter));
            if (inside) taken++;
            highest = Math.Max(highest, counter);
        }
        foreach (ulong counter in order) Assert.False(Take(window, counter));
        Assert.True(taken > 0);
    }

    [Fact]
    public void A_jump_far_ahead_forgets_everything_older_than_the_window()
    {
        var window = new ReplayWindow();
        Assert.True(Take(window, 5));
        Assert.True(Take(window, 5 + 10_000));

        Assert.False(window.IsNew(5));
        Assert.True(window.IsNew(5 + 10_000 - ReplayWindow.Size + 1));
        Assert.False(window.IsNew(5 + 10_000 - ReplayWindow.Size));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1000)]
    public void Shifting_keeps_every_bit_in_its_place(int step)
    {
        var window = new ReplayWindow();
        var marked = new List<ulong>();
        for (ulong counter = 0; counter < 4000; counter += (ulong)step)
        {
            window.Mark(counter);
            marked.Add(counter);
        }
        ulong highest = marked[^1];
        for (ulong counter = highest + 1 - ReplayWindow.Size; counter <= highest; counter++)
            Assert.Equal(!marked.Contains(counter), window.IsNew(counter));
    }
}
