using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Sheron.Core.Security;

namespace Sheron.Win.Security;

/// <summary>
/// The secrets file (TRD › Passwords and consent › Secrets file): the device private key and the fixed password's
/// hash, sealed with DPAPI in machine scope and readable only by SYSTEM, Administrators and the Windows user who
/// made it. Never log what it holds.
/// </summary>
internal static class SecretStore
{
    private static readonly byte[] Entropy = "Sheron 1 secrets"u8.ToArray();

    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sheron", "secrets.bin");

    private static readonly Lazy<Secrets> ThisPcSecrets = new(() => LoadOrCreate(DefaultPath));

    /// <summary>This PC's secrets from <see cref="DefaultPath"/>, read once per run.</summary>
    public static Secrets ThisPc() => ThisPcSecrets.Value;

    /// <summary>
    /// The secrets in <paramref name="path"/>, or new ones (a new device key) when there is no file. A file that cannot
    /// be opened is kept beside it as ".unreadable". Throws <see cref="UnauthorizedAccessException"/> when another
    /// Windows user's file is in the way.
    /// </summary>
    public static Secrets LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            byte[] sealedBytes = File.ReadAllBytes(path);
            try
            {
                byte[] plain = ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.LocalMachine);
                try
                {
                    if (Secrets.TryFrom(plain, out var secrets)) return secrets!;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }
            }
            catch (CryptographicException)
            {
                // Sealed on another PC, or damaged: falls through.
            }
            File.Move(path, path + ".unreadable", overwrite: true);
        }
        var fresh = Secrets.New();
        Save(path, fresh);
        return fresh;
    }

    /// <summary>Seals and writes the secrets: to a new file with its access already set, then moved over the old one.</summary>
    public static void Save(string path, Secrets secrets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] plain = secrets.ToBytes();
        byte[] sealedBytes;
        try
        {
            sealedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        string next = path + ".new";
        if (File.Exists(next)) File.Delete(next);
        using (var stream = new FileInfo(next).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096,
            FileOptions.WriteThrough, Access()))
        {
            stream.Write(sealedBytes);
        }
        File.Move(next, path, overwrite: true);
    }

    /// <summary>SYSTEM, Administrators and this Windows user only; nothing inherited from the folder, where every user may read.</summary>
    internal static FileSecurity Access()
    {
        var access = new FileSecurity();
        access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var who in new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            WindowsIdentity.GetCurrent().User!,
        })
            access.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl, AccessControlType.Allow));
        return access;
    }
}
