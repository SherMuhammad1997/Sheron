using Sheron.Core.Security;

namespace Sheron.Tests.Security;

/// <summary>Plan step 16: the fixed password's hash and the secrets record (TRD › Security).</summary>
public class PasswordTests
{
    [Fact]
    public void The_fixed_password_is_kept_only_as_a_salted_hash_that_checks_it()
    {
        var hash = PasswordHash.Create("correct horse");
        Assert.True(hash.Verify("correct horse"));
        Assert.False(hash.Verify("correct hors"));
        Assert.False(hash.Verify("Correct horse"));

        byte[] stored = hash.ToBytes();
        Assert.Equal(PasswordHash.StoredSize, stored.Length);
        Assert.Equal(-1, stored.AsSpan().IndexOf("correct"u8));
        Assert.True(PasswordHash.TryFrom(stored, out var back));
        Assert.True(back!.Verify("correct horse"));

        // A new salt each time: the same password never gives the same stored bytes.
        Assert.NotEqual(stored, PasswordHash.Create("correct horse").ToBytes());
    }

    [Fact]
    public void A_fixed_password_is_8_to_64_characters_and_a_weakened_stored_hash_is_refused()
    {
        Assert.NotNull(PasswordHash.Problem("short"));
        Assert.NotNull(PasswordHash.Problem("        "));
        Assert.NotNull(PasswordHash.Problem(new string('x', 65)));
        Assert.Null(PasswordHash.Problem("eight ch"));
        Assert.Throws<ArgumentException>(() => PasswordHash.Create("short"));

        byte[] stored = PasswordHash.Create("eight ch").ToBytes();
        stored[0] = 1;
        stored[1] = stored[2] = stored[3] = 0;   // 1 round
        Assert.False(PasswordHash.TryFrom(stored, out _));
        Assert.False(PasswordHash.TryFrom(stored.AsSpan(1), out _));
    }

    [Fact]
    public void Secrets_come_back_whole_and_damaged_bytes_are_refused()
    {
        var empty = Secrets.New();
        Assert.True(Secrets.TryFrom(empty.ToBytes(), out var back));
        Assert.Equal(empty.DevicePrivateKey, back!.DevicePrivateKey);
        Assert.Null(back.FixedPassword);

        var full = empty with { FixedPassword = PasswordHash.Create("unattended 1") };
        byte[] bytes = full.ToBytes();
        Assert.True(Secrets.TryFrom(bytes, out back));
        Assert.True(back!.FixedPassword!.Verify("unattended 1"));
        using (var a = DeviceKey.Import(full.DevicePrivateKey))
        using (var b = DeviceKey.Import(back.DevicePrivateKey))
            Assert.Equal(a.PublicKey, b.PublicKey);

        Assert.False(Secrets.TryFrom(bytes.AsSpan(0, bytes.Length - 1), out _));
        Assert.False(Secrets.TryFrom([.. bytes, 0], out _));
        var wrongVersion = bytes.ToArray();
        wrongVersion[0] = 2;
        Assert.False(Secrets.TryFrom(wrongVersion, out _));
        var badKey = bytes.ToArray();
        badKey[10] ^= 0xFF;
        Assert.False(Secrets.TryFrom(badKey, out _));
    }
}
