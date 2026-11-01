using System.Buffers.Binary;

namespace Sheron.Core.Security;

/// <summary>
/// What the secrets file holds (TRD › Passwords and consent › Secrets file): the device private key and the fixed
/// password's hash. These are the bytes before DPAPI; never log them or put them in any export.
/// </summary>
public sealed record Secrets(byte[] DevicePrivateKey, PasswordHash? FixedPassword)
{
    public const byte Version = 1;

    public static Secrets New()
    {
        using var key = DeviceKey.Create();
        return new Secrets(key.ExportPrivateKey(), null);
    }

    public byte[] ToBytes()
    {
        var bytes = new List<byte>(256) { Version, (byte)(DevicePrivateKey.Length & 0xFF), (byte)(DevicePrivateKey.Length >> 8) };
        bytes.AddRange(DevicePrivateKey);
        if (FixedPassword is null)
        {
            bytes.Add(0);
        }
        else
        {
            bytes.Add(1);
            bytes.AddRange(FixedPassword.ToBytes());
        }
        return [.. bytes];
    }

    /// <summary>Reads the bytes back; false when they are not a whole, valid secrets record.</summary>
    public static bool TryFrom(ReadOnlySpan<byte> bytes, out Secrets? secrets)
    {
        secrets = null;
        if (bytes.Length < 4 || bytes[0] != Version) return false;
        int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[1..]);
        int at = 3 + keyLength;
        if (keyLength == 0 || bytes.Length < at + 1) return false;
        var key = bytes.Slice(3, keyLength).ToArray();
        PasswordHash? fixedPassword = null;
        switch (bytes[at])
        {
            case 0 when bytes.Length == at + 1:
                break;
            case 1 when PasswordHash.TryFrom(bytes[(at + 1)..], out fixedPassword):
                break;
            default:
                return false;
        }
        try
        {
            // The key must be a usable P-256 key, or the file is not ours.
            using var check = DeviceKey.Import(key);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
        secrets = new Secrets(key, fixedPassword);
        return true;
    }
}
