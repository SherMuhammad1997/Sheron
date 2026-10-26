using System.Numerics;
using System.Security.Cryptography;

namespace Sheron.Core.Security;

/// <summary>
/// A device's long-term ECDSA P-256 key (TRD › Security): it signs the session handshake, and its fingerprint is
/// what the viewer pins. The private key leaves only through <see cref="ExportPrivateKey"/>, to be kept with DPAPI
/// (Plan step 16).
/// </summary>
public sealed class DeviceKey : IDisposable
{
    public const int SignatureSize = 64;

    private readonly ECDsa _key;

    private DeviceKey(ECDsa key)
    {
        _key = key;
        PublicKey = P256.Export(key);
        Fingerprint = KeyFingerprint.Of(PublicKey);
    }

    /// <summary>The raw public point, X then Y (64 bytes).</summary>
    public byte[] PublicKey { get; }

    /// <summary>SHA-256 of <see cref="PublicKey"/>.</summary>
    public byte[] Fingerprint { get; }

    public static DeviceKey Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>A key from the PKCS#8 bytes <see cref="ExportPrivateKey"/> gave.</summary>
    public static DeviceKey Import(ReadOnlySpan<byte> pkcs8)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            if (key.KeySize != 256) throw new CryptographicException("A device key must be on P-256.");
            return new DeviceKey(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>The private key as PKCS#8 bytes. Secret: keep only DPAPI-protected, never log (CLAUDE.md › Secrets).</summary>
    public byte[] ExportPrivateKey() => _key.ExportPkcs8PrivateKey();

    /// <summary>ECDSA with SHA-256; 64 bytes, r then s.</summary>
    public byte[] Sign(ReadOnlySpan<byte> data) => _key.SignData(data, HashAlgorithmName.SHA256);

    /// <summary>True when <paramref name="signature"/> is the owner of <paramref name="publicKey"/> signing <paramref name="data"/>.</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != SignatureSize || !P256.IsOnCurve(publicKey)) return false;
        using var key = ECDsa.Create(P256.Parameters(publicKey));
        return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>Fingerprints of device public keys (TRD › Security).</summary>
public static class KeyFingerprint
{
    public static byte[] Of(ReadOnlySpan<byte> publicKey) => SHA256.HashData(publicKey);

    /// <summary>For people: the first 16 bytes as 8 groups of 4 hex digits, such as "3F2A 9C01 …".</summary>
    public static string Show(ReadOnlySpan<byte> fingerprint)
    {
        var hex = Convert.ToHexString(fingerprint[..16]);
        return string.Join(' ', Enumerable.Range(0, 8).Select(i => hex.Substring(i * 4, 4)));
    }
}

/// <summary>NIST P-256 public points as Sheron sends them: 64 bytes, X then Y.</summary>
internal static class P256
{
    public const int PointSize = 64;

    // Curve y² = x³ − 3x + b over the prime p (FIPS 186-4, D.1.2.3).
    private static readonly BigInteger Prime = BigInteger.Parse("0ffffffff00000001000000000000000000000000ffffffffffffffffffffffff",
        System.Globalization.NumberStyles.HexNumber);
    private static readonly BigInteger B = BigInteger.Parse("05ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b",
        System.Globalization.NumberStyles.HexNumber);

    public static byte[] Export(ECAlgorithm key)
    {
        var q = key.ExportParameters(false).Q;
        return [.. q.X!, .. q.Y!];
    }

    public static ECParameters Parameters(ReadOnlySpan<byte> point) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = point[..32].ToArray(), Y = point[32..PointSize].ToArray() },
    };

    /// <summary>
    /// True when the 64 bytes are a point on P-256: both numbers below p and y² = x³ − 3x + b. A point off the curve
    /// could leak bits of the private key it is combined with.
    /// </summary>
    public static bool IsOnCurve(ReadOnlySpan<byte> point)
    {
        if (point.Length != PointSize) return false;
        var x = new BigInteger(point[..32], isUnsigned: true, isBigEndian: true);
        var y = new BigInteger(point[32..], isUnsigned: true, isBigEndian: true);
        if (x >= Prime || y >= Prime) return false;
        var left = BigInteger.ModPow(y, 2, Prime);
        var right = ((BigInteger.ModPow(x, 3, Prime) - 3 * x + B) % Prime + Prime) % Prime;
        return left == right;
    }
}
