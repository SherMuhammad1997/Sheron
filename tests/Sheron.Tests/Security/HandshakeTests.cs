using Sheron.Core.Net;
using Sheron.Core.Security;

namespace Sheron.Tests.Security;

/// <summary>Plan step 7: the session handshake and sealed packets (TRD › Security).</summary>
public class HandshakeTests
{
    internal sealed record Pair(SecureSession Viewer, SecureSession Host);

    /// <summary>A full handshake between a viewer and a host device key.</summary>
    internal static Pair Connect(DeviceKey viewerKey, DeviceKey hostKey)
    {
        using var viewer = new ViewerHandshake(viewerKey);
        using var host = new HostHandshake(hostKey);
        Assert.True(host.TryWelcome(viewer.Hello(), out var welcome));
        Assert.True(viewer.TryFinish(welcome, out var finish, out var viewerSession));
        Assert.True(host.TryAccept(finish, out var hostSession));
        return new Pair(viewerSession, hostSession);
    }

    internal static byte[] Plain(int length, int seed)
    {
        var plain = new byte[length];
        new Random(seed).NextBytes(plain);
        plain[0] = (byte)PacketKind.Video;
        return plain;
    }

    [Fact]
    public void Both_sides_end_with_the_same_keys_and_know_who_the_other_is()
    {
        using var viewerKey = DeviceKey.Create();
        using var hostKey = DeviceKey.Create();
        var (viewer, host) = Connect(viewerKey, hostKey);

        var toHost = Plain(500, 1);
        var toViewer = Plain(PacketLimits.MaxPlain, 2);
        Assert.True(host.TryOpen(viewer.Seal(toHost), out var opened));
        Assert.Equal(toHost, opened);
        var sealedBack = host.Seal(toViewer);
        Assert.True(viewer.TryOpen(sealedBack, out opened));
        Assert.Equal(toViewer, opened);

        Assert.Equal(hostKey.Fingerprint, viewer.RemoteFingerprint);
        Assert.Equal(viewerKey.Fingerprint, host.RemoteFingerprint);
        Assert.True(sealedBack.Length <= PacketLimits.MaxDatagram);
        Assert.Equal(PacketLimits.MaxPlain + SecureSession.Overhead, sealedBack.Length);
    }

    /// <summary>Plan step 7 "Done when": a changed packet is rejected, whichever byte or bit is changed.</summary>
    [Fact]
    public void A_changed_packet_is_rejected_whichever_byte_changes()
    {
        using var viewerKey = DeviceKey.Create();
        using var hostKey = DeviceKey.Create();
        var (viewer, host) = Connect(viewerKey, hostKey);
        var packet = viewer.Seal(Plain(200, 3));

        for (int at = 0; at < packet.Length; at++)
        for (int bit = 0; bit < 8; bit += 3)
        {
            var changed = packet.ToArray();
            changed[at] ^= (byte)(1 << bit);
            Assert.False(host.TryOpen(changed, out _), $"byte {at} bit {bit} changed and still opened");
        }
        Assert.False(host.TryOpen(packet.AsSpan(0, packet.Length - 1), out _));
        Assert.True(host.TryOpen(packet, out _));   // the real one still opens: refusals changed nothing
    }

    [Fact]
    public void A_packet_sent_again_is_refused_but_late_ones_inside_the_window_are_taken()
    {
        using var viewerKey = DeviceKey.Create();
        using var hostKey = DeviceKey.Create();
        var (viewer, host) = Connect(viewerKey, hostKey);
        var packets = Enumerable.Range(0, 3000).Select(i => viewer.Seal(Plain(20, i))).ToList();

        Assert.True(host.TryOpen(packets[10], out _));
        Assert.False(host.TryOpen(packets[10], out _));        // replayed
        Assert.True(host.TryOpen(packets[3], out _));          // late, inside the window
        Assert.True(host.TryOpen(packets[2999], out _));
        Assert.False(host.TryOpen(packets[500], out _));       // 2,499 behind: older than the window
        Assert.True(host.TryOpen(packets[1000], out _));       // 1,999 behind: inside
        Assert.Equal(2, host.Refused);
    }

    [Fact]
    public void A_packet_from_another_session_does_not_open_even_with_the_same_device_keys()
    {
        using var viewerKey = DeviceKey.Create();
        using var hostKey = DeviceKey.Create();
        var first = Connect(viewerKey, hostKey);
        var second = Connect(viewerKey, hostKey);

        Assert.False(second.Host.TryOpen(first.Viewer.Seal(Plain(50, 4)), out _));
    }

    [Fact]
    public void A_host_reply_to_another_hello_is_refused()
    {
        using var viewerKey = DeviceKey.Create();
        using var hostKey = DeviceKey.Create();
        using var viewer = new ViewerHandshake(viewerKey);
        using var host = new HostHandshake(hostKey);
        var hello = viewer.Hello();

        // Someone on the way swaps the viewer's session key for their own: the host signs what it got, which is not
        // what the viewer sent.
        var swapped = hello.ToArray();
        using var other = new ViewerHandshake(DeviceKey.Create());
        other.Hello().AsSpan(66, 64).CopyTo(swapped.AsSpan(66));
        Assert.True(host.TryWelcome(swapped, out var welcome));

        Assert.False(viewer.TryFinish(welcome, out _, out _));
    }

    [Fact]
    public void A_welcome_with_a_changed_byte_or_a_forged_finish_is_refused()
    {
        using var viewerKey = DeviceKey.Create();
        using var hostKey = DeviceKey.Create();
        using var viewer = new ViewerHandshake(viewerKey);
        using var host = new HostHandshake(hostKey);
        Assert.True(host.TryWelcome(viewer.Hello(), out var welcome));

        for (int at = 1; at < welcome.Length; at += 7)
        {
            var changed = welcome.ToArray();
            changed[at] ^= 0x10;
            Assert.False(viewer.TryFinish(changed, out _, out _), $"welcome byte {at} changed and still taken");
        }

        // A Finish signed by another device key does not let the session start.
        using var impostor = DeviceKey.Create();
        byte[] forged = [(byte)PacketKind.Finish, Handshake.Version, .. impostor.Sign(new byte[10])];
        Assert.False(host.TryAccept(forged, out _));
        Assert.True(viewer.TryFinish(welcome, out var finish, out _));
        Assert.True(host.TryAccept(finish, out _));
    }

    [Fact]
    public void Keys_that_are_not_points_on_the_curve_are_refused()
    {
        using var hostKey = DeviceKey.Create();
        using var host = new HostHandshake(hostKey);
        using var viewer = new ViewerHandshake(DeviceKey.Create());
        var hello = viewer.Hello();
        hello[2 + 64 + 63] ^= 1;   // last byte of the session key's Y

        Assert.False(host.TryWelcome(hello, out _));
        Assert.False(host.TryWelcome(new byte[Handshake.HelloSize], out _));
    }

    /// <summary>
    /// Plan step 7 "Done when": a changed key raises the warning. Someone in the middle can finish a handshake with
    /// their own device key, but its fingerprint is not the one remembered for that ID.
    /// </summary>
    [Fact]
    public void A_different_host_key_for_a_known_ID_is_caught_by_key_pinning()
    {
        using var viewerKey = DeviceKey.Create();
        using var realHost = DeviceKey.Create();
        using var impostor = DeviceKey.Create();
        var known = new KnownKeys();

        Assert.Equal(KeyCheck.New, known.Check("123456789", Connect(viewerKey, realHost).Viewer.RemoteFingerprint));
        Assert.Equal(KeyCheck.Same, known.Check("123456789", Connect(viewerKey, realHost).Viewer.RemoteFingerprint));
        Assert.Equal(KeyCheck.Changed, known.Check("123456789", Connect(viewerKey, impostor).Viewer.RemoteFingerprint));
        Assert.Equal(KeyCheck.Changed, known.Check("123456789", impostor.Fingerprint));   // still changed until trusted

        known.Trust("123456789", impostor.Fingerprint);
        Assert.Equal(KeyCheck.Same, known.Check("123456789", impostor.Fingerprint));
    }
}
