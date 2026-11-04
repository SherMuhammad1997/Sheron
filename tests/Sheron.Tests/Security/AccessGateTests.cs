using Sheron.Core.Security;

namespace Sheron.Tests.Security;

/// <summary>
/// Plans 16 and 16b: no password asks the person, the fixed password lets in at once; PRD A9: three wrong passwords
/// lock the ID for 30 s, and three refused requests from one viewer stop its requests for 10 minutes.
/// </summary>
public class AccessGateTests
{
    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly PasswordHash Fixed = PasswordHash.Create("unattended 1");
    private static readonly byte[] ViewerA = [1, 2, 3], ViewerB = [4, 5, 6];

    [Fact]
    public void No_password_asks_the_person_and_the_fixed_password_lets_in_at_once()
    {
        var gate = new AccessGate(Fixed);

        Assert.Equal(AccessResult.AskPerson, gate.Check("", ViewerA).Result);
        Assert.Equal(AccessResult.LetIn, gate.Check("unattended 1", ViewerA).Result);
        Assert.Equal(AccessResult.Wrong, gate.Check("unattended 2", ViewerA).Result);

        // With no fixed password set, any password is wrong; asking still works.
        gate.FixedPassword = null;
        Assert.Equal(AccessResult.Wrong, gate.Check("unattended 1").Result);
        Assert.Equal(AccessResult.AskPerson, gate.Check("").Result);
    }

    [Fact]
    public void Three_wrong_passwords_lock_even_the_right_one_out_for_30_seconds()
    {
        var clock = new ManualClock();
        var gate = new AccessGate(Fixed, clock);

        Assert.Equal(new AccessCheck(AccessResult.Wrong, 2, TimeSpan.Zero), gate.Check("guess 1"));
        Assert.Equal(new AccessCheck(AccessResult.Wrong, 1, TimeSpan.Zero), gate.Check("guess 2"));
        Assert.Equal(new AccessCheck(AccessResult.Wrong, 0, TimeSpan.FromSeconds(30)), gate.Check("guess 3"));

        clock.Now += TimeSpan.FromSeconds(10);
        Assert.Equal(new AccessCheck(AccessResult.Locked, 0, TimeSpan.FromSeconds(20)), gate.Check("unattended 1"));
        Assert.Equal(AccessResult.Locked, gate.Check("").Result);
        Assert.Equal(TimeSpan.FromSeconds(20), gate.LockedFor);

        clock.Now += TimeSpan.FromSeconds(20);
        Assert.Equal(AccessResult.LetIn, gate.Check("unattended 1").Result);
        Assert.Equal(2, gate.Check("guess 4").TriesLeft);   // the count starts again after the lock
    }

    [Fact]
    public void A_right_password_resets_the_count_of_wrong_ones()
    {
        var gate = new AccessGate(Fixed);
        gate.Check("guess 1");
        gate.Check("guess 2");
        Assert.Equal(AccessResult.LetIn, gate.Check("unattended 1").Result);
        Assert.Equal(2, gate.Check("guess 3").TriesLeft);
    }

    [Fact]
    public void A_viewer_refused_three_times_in_10_minutes_is_not_asked_about_for_10_minutes()
    {
        var clock = new ManualClock();
        var gate = new AccessGate(Fixed, clock);

        gate.Refused(ViewerA);
        clock.Now += TimeSpan.FromMinutes(4);
        gate.Refused(ViewerA);
        Assert.Equal(AccessResult.AskPerson, gate.Check("", ViewerA).Result);
        clock.Now += TimeSpan.FromMinutes(4);
        gate.Refused(ViewerA);

        Assert.Equal(new AccessCheck(AccessResult.TooManyRequests, 0, TimeSpan.FromMinutes(10)), gate.Check("", ViewerA));
        Assert.Equal(AccessResult.TooManyRequests, gate.Check("unattended 1", ViewerA).Result);
        Assert.Equal(AccessResult.AskPerson, gate.Check("", ViewerB).Result);   // other viewers are not affected

        clock.Now += TimeSpan.FromMinutes(10);
        Assert.Equal(AccessResult.AskPerson, gate.Check("", ViewerA).Result);
    }

    [Fact]
    public void Refusals_more_than_10_minutes_apart_do_not_add_up()
    {
        var clock = new ManualClock();
        var gate = new AccessGate(clock: clock);
        for (int i = 0; i < 5; i++)
        {
            gate.Refused(ViewerA);
            clock.Now += TimeSpan.FromMinutes(6);
        }
        Assert.Equal(AccessResult.AskPerson, gate.Check("", ViewerA).Result);
    }
}
