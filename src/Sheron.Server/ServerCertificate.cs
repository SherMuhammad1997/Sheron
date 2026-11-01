using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Sheron.Server;

/// <summary>
/// The server's TLS certificate (TRD › Server › Rendezvous protocol): self-signed ECDSA P-256, made on first start and
/// kept in the data folder, readable by the server's own user only. The app trusts it by its pinned key.
/// </summary>
internal static class ServerCertificate
{
    private const string FileName = "server-certificate.pfx";

    public static X509Certificate2 LoadOrCreate(string dataFolder)
    {
        string path = Path.Combine(dataFolder, FileName);
        if (File.Exists(path)) return new X509Certificate2(path, (string?)null, X509KeyStorageFlags.Exportable);

        var certificate = Create();
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return certificate;
    }

    /// <summary>A new self-signed certificate, valid 10 years. Re-imported from PFX so TLS works on Windows too.</summary>
    public static X509Certificate2 Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Sheron server", key, HashAlgorithmName.SHA256);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        return new X509Certificate2(made.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
    }
}
