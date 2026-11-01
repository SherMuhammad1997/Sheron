using System.Security.AccessControl;
using System.Security.Principal;
using Sheron.Core.Security;
using Sheron.Win.Security;

namespace Sheron.Win.Tests.Security;

/// <summary>Plan step 16: the DPAPI secrets file (TRD › Passwords and consent › Secrets file). Windows only.</summary>
public sealed class SecretStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("sheron-secrets-").FullName;

    private string File => Path.Combine(_folder, "secrets.bin");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void The_first_load_makes_a_device_key_and_later_loads_give_the_same_one()
    {
        var first = SecretStore.LoadOrCreate(File);
        var again = SecretStore.LoadOrCreate(File);

        using var a = DeviceKey.Import(first.DevicePrivateKey);
        using var b = DeviceKey.Import(again.DevicePrivateKey);
        Assert.Equal(a.PublicKey, b.PublicKey);
        Assert.Null(again.FixedPassword);
    }

    [Fact]
    public void The_file_holds_the_secrets_sealed_not_in_plain()
    {
        var secrets = Secrets.New() with { FixedPassword = PasswordHash.Create("unattended 1") };
        SecretStore.Save(File, secrets);

        byte[] onDisk = System.IO.File.ReadAllBytes(File);
        Assert.Equal(-1, onDisk.AsSpan().IndexOf(secrets.DevicePrivateKey.AsSpan(0, 32)));
        Assert.Equal(-1, onDisk.AsSpan().IndexOf(secrets.FixedPassword!.ToBytes().AsSpan(4, 16)));
        var back = SecretStore.LoadOrCreate(File);
        Assert.True(back.FixedPassword!.Verify("unattended 1"));
    }

    [Fact]
    public void Only_SYSTEM_Administrators_and_this_user_may_open_the_file()
    {
        SecretStore.LoadOrCreate(File);

        var access = new FileInfo(File).GetAccessControl();
        Assert.True(access.AreAccessRulesProtected);
        var allowed = access.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .Select(r => (SecurityIdentifier)r.IdentityReference)
            .ToHashSet();
        Assert.Equal(new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            WindowsIdentity.GetCurrent().User!,
        }, allowed);
    }

    [Fact]
    public void A_file_that_cannot_be_opened_is_kept_aside_and_new_secrets_are_made()
    {
        System.IO.File.WriteAllBytes(File, [1, 2, 3, 4, 5]);

        var secrets = SecretStore.LoadOrCreate(File);

        Assert.True(System.IO.File.Exists(File + ".unreadable"));
        using var key = DeviceKey.Import(secrets.DevicePrivateKey);
        Assert.Equal(secrets.DevicePrivateKey, SecretStore.LoadOrCreate(File).DevicePrivateKey);
    }
}
