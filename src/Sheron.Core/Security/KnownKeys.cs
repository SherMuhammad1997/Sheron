using System.Security.Cryptography;

namespace Sheron.Core.Security;

/// <summary>What <see cref="KnownKeys.Check"/> found.</summary>
public enum KeyCheck
{
    /// <summary>First connect to this ID: the key is now remembered.</summary>
    New,
    /// <summary>The key remembered for this ID: go on.</summary>
    Same,
    /// <summary>A different key than remembered: stop the session and warn (TRD › Security › Key pinning).</summary>
    Changed,
}

/// <summary>
/// The viewer's pinned remote keys: remote ID → device key fingerprint (TRD › Security › Key pinning). Saved in
/// <c>known-keys.json</c> from Plan step 15; public data, no secrets.
/// </summary>
public sealed class KnownKeys
{
    private readonly Dictionary<string, byte[]> _fingerprints = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> All => _fingerprints;

    /// <summary>Compares a session's <see cref="SecureSession.RemoteFingerprint"/> with the one remembered for the ID.</summary>
    public KeyCheck Check(string remoteId, ReadOnlySpan<byte> fingerprint)
    {
        if (!_fingerprints.TryGetValue(remoteId, out var known))
        {
            _fingerprints[remoteId] = fingerprint.ToArray();
            return KeyCheck.New;
        }
        return CryptographicOperations.FixedTimeEquals(known, fingerprint) ? KeyCheck.Same : KeyCheck.Changed;
    }

    /// <summary>Remembers a changed key; only when the person has seen the warning and said so.</summary>
    public void Trust(string remoteId, ReadOnlySpan<byte> fingerprint) => _fingerprints[remoteId] = fingerprint.ToArray();
}
