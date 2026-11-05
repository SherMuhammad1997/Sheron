namespace Sheron.Core.Security;

public enum AccessResult : byte
{
    /// <summary>Let in: the session may start.</summary>
    LetIn = 1,
    /// <summary>No password: the person at the remote PC is asked.</summary>
    AskPerson = 2,
    Wrong = 3,
    Locked = 4,
    /// <summary>The person at the remote PC said no.</summary>
    Denied = 5,
    /// <summary>Nobody answered the request in time.</summary>
    NoAnswer = 6,
    /// <summary>This viewer was refused 3 times within 10 minutes: its requests are refused for a while, nobody asked.</summary>
    TooManyRequests = 7,
    /// <summary>The remote PC is in another session (PRD decision 5 Nov): nothing checked, nobody asked.</summary>
    Busy = 8,
}

/// <summary>What a password check gave: the result, the tries left before the lock, and the lock time left.</summary>
public readonly record struct AccessCheck(AccessResult Result, int TriesLeft, TimeSpan LockedFor);

/// <summary>
/// The remote PC's access rules (TRD › Security, Passwords and consent; PRD decision 4 Nov): a viewer with no
/// password is asked about (the person accepts or denies); the fixed password lets in at once. Three wrong passwords in
/// a row, from any viewer, lock every password out for 30 s. A viewer refused three times within 10 minutes (Deny or
/// no answer) is refused for 10 minutes without asking. Thread-safe.
/// </summary>
public sealed class AccessGate
{
    public const int MaxTries = 3;
    public static readonly TimeSpan LockTime = TimeSpan.FromSeconds(30);
    public const int MaxRefusals = 3;
    public static readonly TimeSpan RefusalWindow = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, List<DateTimeOffset>> _refusals = [];
    private readonly Dictionary<string, DateTimeOffset> _blockedUntil = [];
    private PasswordHash? _fixed;
    private int _wrong;
    private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;

    public AccessGate(PasswordHash? fixedPassword = null, TimeProvider? clock = null)
    {
        _fixed = fixedPassword;
        _clock = clock ?? TimeProvider.System;
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

    /// <summary>How long the password lock still lasts; zero when there is none.</summary>
    public TimeSpan LockedFor
    {
        get
        {
            lock (_lock) return Left(_lockedUntil);
        }
    }

    /// <summary>
    /// Checks a request from the viewer with the device key <paramref name="viewerFingerprint"/>: empty
    /// <paramref name="typed"/> asks the person. May take one PBKDF2 (about 0.3 s): call it off the link's thread.
    /// </summary>
    public AccessCheck Check(string typed, byte[]? viewerFingerprint = null)
    {
        PasswordHash? fixedPassword;
        lock (_lock)
        {
            if (viewerFingerprint is not null && _blockedUntil.TryGetValue(Key(viewerFingerprint), out var until) && Left(until) is { } blocked
                && blocked > TimeSpan.Zero)
                return new AccessCheck(AccessResult.TooManyRequests, 0, blocked);
            if (Left(_lockedUntil) is { } locked && locked > TimeSpan.Zero) return new AccessCheck(AccessResult.Locked, 0, locked);
            if (typed.Length == 0) return new AccessCheck(AccessResult.AskPerson, MaxTries - _wrong, TimeSpan.Zero);
            fixedPassword = _fixed;
        }

        bool right = fixedPassword?.Verify(typed) == true;

        lock (_lock)
        {
            // Another check may have started the lock while this one ran.
            if (Left(_lockedUntil) is { } locked && locked > TimeSpan.Zero) return new AccessCheck(AccessResult.Locked, 0, locked);
            if (right)
            {
                _wrong = 0;
                return new AccessCheck(AccessResult.LetIn, MaxTries, TimeSpan.Zero);
            }
            _wrong++;
            if (_wrong < MaxTries) return new AccessCheck(AccessResult.Wrong, MaxTries - _wrong, TimeSpan.Zero);
            _wrong = 0;
            _lockedUntil = _clock.GetUtcNow() + LockTime;
            return new AccessCheck(AccessResult.Wrong, 0, LockTime);
        }
    }

    /// <summary>The person denied this viewer, or did not answer: the third time within 10 minutes blocks it for 10 minutes.</summary>
    public void Refused(byte[] viewerFingerprint)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            string key = Key(viewerFingerprint);
            if (!_refusals.TryGetValue(key, out var times)) _refusals[key] = times = [];
            times.RemoveAll(t => now - t > RefusalWindow);
            times.Add(now);
            if (times.Count < MaxRefusals) return;
            times.Clear();
            _blockedUntil[key] = now + RefusalWindow;
        }
    }

    private TimeSpan Left(DateTimeOffset until)
    {
        var left = until - _clock.GetUtcNow();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private static string Key(byte[] fingerprint) => Convert.ToHexString(fingerprint);
}
