using System.Text;
using Sheron.Core.Security;

namespace Sheron.Tests.Security;

public class DeviceKeyTests
{
    [Fact]
    public void A_signature_checks_only_with_the_same_key_and_data()
    {
        using var key = DeviceKey.Create();
        using var other = DeviceKey.Create();
        var data = Encoding.ASCII.GetBytes("Sheron");
        var signature = key.Sign(data);

        Assert.Equal(DeviceKey.SignatureSize, signature.Length);
        Assert.True(DeviceKey.Verify(key.PublicKey, data, signature));
        Assert.False(DeviceKey.Verify(other.PublicKey, data, signature));
        Assert.False(DeviceKey.Verify(key.PublicKey, Encoding.ASCII.GetBytes("Sheron!"), signature));
    }

    [Fact]
    public void An_exported_key_comes_back_the_same()
    {
        using var key = DeviceKey.Create();
        using var back = DeviceKey.Import(key.ExportPrivateKey());

        Assert.Equal(key.PublicKey, back.PublicKey);
        Assert.True(DeviceKey.Verify(key.PublicKey, [1, 2, 3], back.Sign([1, 2, 3])));
    }

    [Fact]
    public void The_fingerprint_shows_as_8_groups_of_4_hex_digits()
    {
        var fingerprint = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        Assert.Equal("0001 0203 0405 0607 0809 0A0B 0C0D 0E0F", KeyFingerprint.Show(fingerprint));
    }
}
