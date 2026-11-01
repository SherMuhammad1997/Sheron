namespace Sheron.Core.Security;

public enum AccessResult : byte
{
    /// <summary>Let in: the session may start.</summary>
    LetIn = 1,
    /// <summary>The one-time password: the person at the remote PC is asked.</summary>
    AskPerson = 2,
    Wrong = 3,
    Locked = 4,
    /// <summary>The person at the remote PC said no.</summary>
    Denied = 5,
    /// <summary>Nobody answered the request in time.</summary>
    NoAnswer = 6,
}

/// <summary>What a password check gave: the result, the tries left before the lock, and the lock time left.</summary>
public readonly record struct AccessCheck(AccessResult Result, int TriesLeft, TimeSpan LockedFor);

/// <summary>
/// The remote PC's password rules (TRD › Security, Passwords and consent): the one-time password (the person is
/// asked) and the fixed password (let in at once). Three wrong passwords in a row, from any viewer, lock every
/// password out for 30 s. Thread-safe.
/// </summary>
public sealed class AccessGate
{
    public const int MaxTries = 3;
    public static readonly TimeSpan LockTime = TimeSpan.FromSeconds(30);

    private readonly object _lock = new();
    private readonly TimeProvider _clock;
    private string _oneTime = Security.OneTimePassword.New();
    private PasswordHash? _fixed;
    private int _wrong;
    private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;

    public AccessGate(PasswordHash? fixedPassword = null, TimeProvider? clock = null)
    {
        _fixed = fixedPassword;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The password this PC shows now.</summary>
    public string OneTimePassword
    {
        get
        {
            lock (_lock) return _oneTime;
        }
    }

    /// <summary>The unattended-access password; null for none (then every viewer needs the person to accept).</summary>
    public PasswordHash? FixedPassword
    {
        get
        {
            lock (_lock) return _fixed;
        }
        set
        {
            lock (_lock) _fixed = value;
        }
    }

    /// <summary>How long the lock still lasts; zero when there is none.</summary>
    public TimeSpan LockedFor
    {
        get
        {
            lock (_lock) return LockLeft();
        }
    }

    /// <summary>Checks a password typed on the viewer. May take one PBKDF2 (about 0.3 s): call it off the link's thread.</summary>
    public AccessCheck Check(string typed)
    {
        string oneTime;
        PasswordHash? fixedPassword;
        lock (_lock)
        {
            if (LockLeft() is { } left && left > TimeSpan.Zero) return new AccessCheck(AccessResult.Locked, 0, left);
            oneTime = _oneTime;
            fixedPassword = _fixed;
        }

        var result = Security.OneTimePassword.Matches(typed, oneTime) ? AccessResult.AskPerson
            : fixedPassword?.Verify(typed) == true ? AccessResult.LetIn
            : AccessResult.Wrong;

        lock (_lock)
        {
            // Another check may have started the lock while this one ran.
            if (LockLeft() is { } left && left > TimeSpan.Zero) return new AccessCheck(AccessResult.Locked, 0, left);
            if (result != AccessResult.Wrong)
            {
                _wrong = 0;
                return new AccessCheck(result, MaxTries, TimeSpan.Zero);
            }
            _wrong++;
            if (_wrong < MaxTries) return new AccessCheck(AccessResult.Wrong, MaxTries - _wrong, TimeSpan.Zero);
            _wrong = 0;
            _lockedUntil = _clock.GetUtcNow() + LockTime;
            return new AccessCheck(AccessResult.Wrong, 0, LockTime);
        }
    }

    /// <summary>A session that was let in has ended: the one-time password changes.</summary>
    public void SessionEnded()
    {
        lock (_lock) _oneTime = Security.OneTimePassword.New();
    }

    private TimeSpan LockLeft()
    {
        var left = _lockedUntil - _clock.GetUtcNow();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}
