using Sheron.Core.Security;

namespace Sheron.Tests.Security;

/// <summary>Plan step 16: which password does what, and PRD A9: three wrong passwords lock the ID for 30 s.</summary>
public class AccessGateTests
{
    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly PasswordHash Fixed = PasswordHash.Create("unattended 1");

    [Fact]
    public void The_one_time_password_asks_the_person_and_the_fixed_password_lets_in_at_once()
    {
        var gate = new AccessGate(Fixed);

        Assert.Equal(AccessResult.AskPerson, gate.Check(gate.OneTimePassword.ToLowerInvariant()).Result);
        Assert.Equal(AccessResult.LetIn, gate.Check("unattended 1").Result);
        Assert.Equal(AccessResult.Wrong, gate.Check("unattended 2").Result);

        // With no fixed password set, only the one-time password works.
        gate.FixedPassword = null;
        Assert.Equal(AccessResult.Wrong, gate.Check("unattended 1").Result);
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
        Assert.Equal(new AccessCheck(AccessResult.Locked, 0, TimeSpan.FromSeconds(20)), gate.Check(gate.OneTimePassword));
        Assert.Equal(AccessResult.Locked, gate.Check("unattended 1").Result);
        Assert.Equal(TimeSpan.FromSeconds(20), gate.LockedFor);

        clock.Now += TimeSpan.FromSeconds(20);
        Assert.Equal(TimeSpan.Zero, gate.LockedFor);
        Assert.Equal(AccessResult.LetIn, gate.Check("unattended 1").Result);
        // The count starts again after the lock.
        Assert.Equal(2, gate.Check("guess 4").TriesLeft);
    }

    [Fact]
    public void A_right_password_resets_the_count_of_wrong_ones()
    {
        var gate = new AccessGate(Fixed);
        gate.Check("guess 1");
        gate.Check("guess 2");
        Assert.Equal(AccessResult.AskPerson, gate.Check(gate.OneTimePassword).Result);
        Assert.Equal(2, gate.Check("guess 3").TriesLeft);
    }

    [Fact]
    public void The_one_time_password_changes_after_a_session()
    {
        var gate = new AccessGate();
        string first = gate.OneTimePassword;
        gate.SessionEnded();
        Assert.NotEqual(first, gate.OneTimePassword);
        Assert.Equal(AccessResult.Wrong, gate.Check(first).Result);
    }
}
