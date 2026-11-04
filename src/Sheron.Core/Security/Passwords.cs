using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Sheron.Core.Security;

/// <summary>
/// The fixed password for unattended access, kept only as a hash (TRD › Security): PBKDF2-SHA256, 600,000 rounds,
/// 16-byte salt.
/// </summary>
public sealed class PasswordHash
{
    public const int Rounds = 600_000;
    public const int SaltSize = 16;
    public const int HashSize = 32;
    public const int MinLength = 8;
    public const int MaxLength = 64;
    /// <summary>Rounds (4 bytes), salt, hash.</summary>
    public const int StoredSize = 4 + SaltSize + HashSize;

    private readonly int _rounds;
    private readonly byte[] _salt;
    private readonly byte[] _hash;

    private PasswordHash(int rounds, byte[] salt, byte[] hash)
    {
        _rounds = rounds;
        _salt = salt;
        _hash = hash;
    }

    /// <summary>Why <paramref name="password"/> cannot be a fixed password, or null when it can.</summary>
    public static string? Problem(string password)
    {
        int length = password.Normalize(NormalizationForm.FormC).Length;
        if (string.IsNullOrWhiteSpace(password) || length < MinLength) return $"At least {MinLength} characters.";
        return length > MaxLength ? $"At most {MaxLength} characters." : null;
    }

    public static PasswordHash Create(string password)
    {
        if (Problem(password) is { } problem) throw new ArgumentException(problem, nameof(password));
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        return new PasswordHash(Rounds, salt, Derive(password, salt, Rounds));
    }

    /// <summary>True when <paramref name="password"/> is the one hashed. Takes one PBKDF2: about 0.3 s.</summary>
    public bool Verify(string password)
    {
        if (password.Length is 0 or > 4 * MaxLength) return false;
        return CryptographicOperations.FixedTimeEquals(Derive(password, _salt, _rounds), _hash);
    }

    public byte[] ToBytes()
    {
        var bytes = new byte[StoredSize];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, _rounds);
        _salt.CopyTo(bytes, 4);
        _hash.CopyTo(bytes, 4 + SaltSize);
        return bytes;
    }

    public static bool TryFrom(ReadOnlySpan<byte> bytes, out PasswordHash? hash)
    {
        hash = null;
        if (bytes.Length != StoredSize) return false;
        int rounds = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        // Never fewer rounds than Sheron uses: a file changed to make guessing cheap is refused.
        if (rounds is < Rounds or > 10 * Rounds) return false;
        hash = new PasswordHash(rounds, bytes.Slice(4, SaltSize).ToArray(), bytes.Slice(4 + SaltSize, HashSize).ToArray());
        return true;
    }

    // The same password typed with composed or decomposed accents gives the same hash.
    private static byte[] Derive(string password, byte[] salt, int rounds) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC)), salt, rounds,
            HashAlgorithmName.SHA256, HashSize);
}
