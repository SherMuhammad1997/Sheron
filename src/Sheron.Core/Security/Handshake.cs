using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Sheron.Core.Net;

namespace Sheron.Core.Security;

/// <summary>
/// Sizes and labels of the session handshake (TRD › Security › Session handshake): Hello from the viewer, Welcome
/// signed by the host, Finish signed by the viewer; session keys from ECDH of two new P-256 keys and HKDF-SHA256.
/// </summary>
public static class Handshake
{
    public const byte Version = 1;
    public const int RandomSize = 32;
    public const int KeySize = 32;

    // Kind, version, device key, session key, random bytes (and the host's signature).
    public const int HelloSize = 2 + P256.PointSize * 2 + RandomSize;
    public const int WelcomeSignedPart = 2 + P256.PointSize * 2 + RandomSize;
    public const int WelcomeSize = WelcomeSignedPart + DeviceKey.SignatureSize;
    public const int FinishSize = 2 + DeviceKey.SignatureSize;

    private static readonly byte[] HostLabel = Encoding.ASCII.GetBytes("Sheron 1 host");
    private static readonly byte[] ViewerLabel = Encoding.ASCII.GetBytes("Sheron 1 viewer");
    private static readonly byte[] KeysInfo = Encoding.ASCII.GetBytes("Sheron 1 session keys");

    /// <summary>What the host signs: its label, then SHA-256 of the Hello and the Welcome up to the signature.</summary>
    internal static byte[] HostSigns(ReadOnlySpan<byte> hello, ReadOnlySpan<byte> welcomeSignedPart) =>
        [.. HostLabel, .. Hash(hello, welcomeSignedPart)];

    /// <summary>What the viewer signs: its label, then the transcript hash.</summary>
    internal static byte[] ViewerSigns(byte[] transcript) => [.. ViewerLabel, .. transcript];

    /// <summary>SHA-256 of the Hello and the whole Welcome.</summary>
    internal static byte[] Transcript(ReadOnlySpan<byte> hello, ReadOnlySpan<byte> welcome) => Hash(hello, welcome);

    /// <summary>
    /// 64 bytes from ECDH of the two session keys and HKDF-SHA256 (salt: the transcript hash): viewer → host key, then
    /// host → viewer key. Our session key is disposed: it is used once.
    /// </summary>
    internal static byte[] SessionKeys(ECDiffieHellman ours, ReadOnlySpan<byte> theirPublicKey, byte[] transcript)
    {
        using (ours)
        using (var theirs = ECDiffieHellman.Create(P256.Parameters(theirPublicKey)))
        {
            byte[] shared = ours.DeriveRawSecretAgreement(theirs.PublicKey);
            try
            {
                return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, KeySize * 2, transcript, KeysInfo);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(shared);
            }
        }
    }

    /// <summary>Kind and version check out and the message has exactly its size.</summary>
    internal static bool IsMessage(ReadOnlySpan<byte> message, PacketKind kind, int size) =>
        message.Length == size && message[0] == (byte)kind && message[1] == Version;

    private static byte[] Hash(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(first);
        hash.AppendData(second);
        return hash.GetHashAndReset();
    }

    internal static byte[] NewSessionKey(out ECDiffieHellman key)
    {
        key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return P256.Export(key);
    }
}

/// <summary>The viewer's side of the handshake: <see cref="Hello"/>, then <see cref="TryFinish"/> on the host's Welcome.</summary>
public sealed class ViewerHandshake(DeviceKey device) : IDisposable
{
    private ECDiffieHellman? _sessionKey;
    private byte[]? _hello;

    public byte[] Hello()
    {
        byte[] sessionKey = Handshake.NewSessionKey(out var key);
        _sessionKey?.Dispose();
        _sessionKey = key;
        _hello = [(byte)PacketKind.Hello, Handshake.Version, .. device.PublicKey, .. sessionKey, .. RandomNumberGenerator.GetBytes(Handshake.RandomSize)];
        return _hello;
    }

    /// <summary>
    /// Checks the host's Welcome: its keys are on the curve and its signature covers our Hello. On success gives the
    /// Finish to send and the session; the caller then checks <see cref="SecureSession.RemoteFingerprint"/> against
    /// <see cref="KnownKeys"/>.
    /// </summary>
    public bool TryFinish(ReadOnlySpan<byte> welcome, [NotNullWhen(true)] out byte[]? finish, [NotNullWhen(true)] out SecureSession? session)
    {
        finish = null;
        session = null;
        if (_hello is null || _sessionKey is null || !Handshake.IsMessage(welcome, PacketKind.Welcome, Handshake.WelcomeSize)) return false;
        var hostDevice = welcome.Slice(2, P256.PointSize);
        var hostSession = welcome.Slice(2 + P256.PointSize, P256.PointSize);
        if (!P256.IsOnCurve(hostDevice) || !P256.IsOnCurve(hostSession)) return false;
        var signed = Handshake.HostSigns(_hello, welcome[..Handshake.WelcomeSignedPart]);
        if (!DeviceKey.Verify(hostDevice, signed, welcome[Handshake.WelcomeSignedPart..])) return false;

        byte[] transcript = Handshake.Transcript(_hello, welcome);
        byte[] keys = Handshake.SessionKeys(_sessionKey, hostSession, transcript);
        _sessionKey = null;
        try
        {
            finish = [(byte)PacketKind.Finish, Handshake.Version, .. device.Sign(Handshake.ViewerSigns(transcript))];
            session = new SecureSession(keys.AsSpan(0, Handshake.KeySize), keys.AsSpan(Handshake.KeySize), hostDevice.ToArray());
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keys);
        }
    }

    public void Dispose() => _sessionKey?.Dispose();
}

/// <summary>
/// The host's side of the handshake: <see cref="TryWelcome"/> on the viewer's Hello, then <see cref="TryAccept"/> on
/// its Finish. Only then is there a session.
/// </summary>
public sealed class HostHandshake(DeviceKey device) : IDisposable
{
    private byte[]? _keys;
    private byte[]? _transcript;
    private byte[]? _viewerDevice;

    public bool TryWelcome(ReadOnlySpan<byte> hello, [NotNullWhen(true)] out byte[]? welcome)
    {
        welcome = null;
        if (!Handshake.IsMessage(hello, PacketKind.Hello, Handshake.HelloSize)) return false;
        var viewerDevice = hello.Slice(2, P256.PointSize);
        var viewerSession = hello.Slice(2 + P256.PointSize, P256.PointSize);
        if (!P256.IsOnCurve(viewerDevice) || !P256.IsOnCurve(viewerSession)) return false;

        byte[] sessionKey = Handshake.NewSessionKey(out var key);
        byte[] signedPart = [(byte)PacketKind.Welcome, Handshake.Version, .. device.PublicKey, .. sessionKey, .. RandomNumberGenerator.GetBytes(Handshake.RandomSize)];
        welcome = [.. signedPart, .. device.Sign(Handshake.HostSigns(hello, signedPart))];

        _transcript = Handshake.Transcript(hello, welcome);
        Wipe();
        _keys = Handshake.SessionKeys(key, viewerSession, _transcript);
        _viewerDevice = viewerDevice.ToArray();
        return true;
    }

    /// <summary>Checks the viewer's Finish: its device key signed this very handshake.</summary>
    public bool TryAccept(ReadOnlySpan<byte> finish, [NotNullWhen(true)] out SecureSession? session)
    {
        session = null;
        if (_keys is null || _transcript is null || _viewerDevice is null
            || !Handshake.IsMessage(finish, PacketKind.Finish, Handshake.FinishSize)
            || !DeviceKey.Verify(_viewerDevice, Handshake.ViewerSigns(_transcript), finish[2..]))
            return false;
        // The host sends with the second key and receives with the first.
        session = new SecureSession(_keys.AsSpan(Handshake.KeySize), _keys.AsSpan(0, Handshake.KeySize), _viewerDevice);
        Wipe();
        return true;
    }

    private void Wipe()
    {
        if (_keys is not null) CryptographicOperations.ZeroMemory(_keys);
        _keys = null;
    }

    public void Dispose() => Wipe();
}
