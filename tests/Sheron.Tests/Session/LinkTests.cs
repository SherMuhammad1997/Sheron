using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Sheron.Core.Capture;
using Sheron.Core.Net;
using Sheron.Core.Security;
using Sheron.Core.Session;

namespace Sheron.Tests.Session;

/// <summary>
/// Plan step 8 in Core: a remote PC's link and a viewer's link talking over UDP on this machine (127.0.0.1), with
/// the handshake, sealed video and control messages (TRD › Connect by IP).
/// </summary>
[Collection(Timing.Name)]
public class LinkTests
{
    private static readonly LinkTimings Quick = new(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(600));

    private static IPEndPoint Local(Link host) => new(IPAddress.Loopback, host.LocalEndPoint.Port);

    /// <summary>Waits up to 5 s for <paramref name="condition"/>.</summary>
    private static bool Within(Func<bool> condition, int ms = 5000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > ms) return false;
            Thread.Sleep(5);
        }
        return true;
    }

    [Fact]
    public void A_viewer_connects_and_gets_every_frame_and_control_message_in_order()
    {
        using var hostKey = DeviceKey.Create();
        using var viewerKey = DeviceKey.Create();
        using var host = Link.Host(hostKey, port: 0);
        using var viewer = Link.Viewer(viewerKey, Local(host));
        var frames = new ConcurrentQueue<AssembledFrame>();
        var messages = new ConcurrentQueue<byte[]>();
        viewer.FrameReceived += frames.Enqueue;
        viewer.ControlReceived += messages.Enqueue;
        int hostConnected = 0;
        host.Connected += () => Interlocked.Increment(ref hostConnected);

        Assert.True(Within(() => hostConnected == 1 && viewer.Snapshot().State == LinkState.Connected), "no session");
        Assert.Equal(hostKey.Fingerprint, viewer.Snapshot().PeerFingerprint);
        Assert.Equal(viewerKey.Fingerprint, host.Snapshot().PeerFingerprint);

        var shape = new PointerShape(PointerShapeKind.Color, 32, 32, 128, 1, 2, new byte[128 * 32]);
        host.SendControl(ControlMessage.VideoFormat(1920, 1200));
        host.SendControl(ControlMessage.Pointer(shape));
        host.SendControl(ControlMessage.Pointer(new PointerPosition(true, -3, 700)));

        var random = new Random(8);
        var sent = new List<byte[]>();
        for (int i = 0; i < 120; i++)
        {
            var frame = new byte[i == 0 ? 150_000 : random.Next(5_000, 30_000)];
            random.NextBytes(frame);
            sent.Add(frame);
            host.SendFrame(frame, keyFrame: i == 0);
            Thread.Sleep(2);
        }

        Assert.True(Within(() => frames.Count == sent.Count && messages.Count == 3), $"{frames.Count} frames, {messages.Count} messages");
        var received = frames.ToList();
        for (int i = 0; i < sent.Count; i++) Assert.Equal(sent[i], received[i].Data);
        Assert.True(received[0].KeyFrame);

        var control = messages.ToList();
        Assert.True(ControlMessage.TryReadVideoFormat(control[0], out int width, out int height));
        Assert.Equal((1920, 1200), (width, height));
        Assert.True(ControlMessage.TryReadPointerShape(control[1], out var shapeBack));
        Assert.Equal((32, 32, 1, 2), (shapeBack!.Width, shapeBack.Height, shapeBack.HotSpotX, shapeBack.HotSpotY));
        Assert.True(ControlMessage.TryReadPointerPosition(control[2], out var position));
        Assert.Equal(new PointerPosition(true, -3, 700), position);

        // Pings measure the round trip on both sides.
        Assert.True(Within(() => host.Snapshot().RoundTrip is not null && viewer.Snapshot().RoundTrip is not null));
    }

    [Fact]
    public void When_the_remote_PC_goes_the_viewer_loses_the_session_and_the_reverse()
    {
        using var hostKey = DeviceKey.Create();
        var host = Link.Host(hostKey, port: 0, Quick);
        var viewer = Link.Viewer(DeviceKey.Create(), Local(host), Quick);
        int viewerLost = 0;
        viewer.Disconnected += () => Interlocked.Increment(ref viewerLost);
        Assert.True(Within(() => host.Snapshot().State == LinkState.Connected && viewer.Snapshot().State == LinkState.Connected));

        host.Dispose();
        Assert.True(Within(() => viewerLost == 1 && viewer.Snapshot().State == LinkState.Lost), "viewer did not notice");
        viewer.Dispose();

        using var host2 = Link.Host(hostKey, port: 0, Quick);
        int hostLost = 0;
        host2.Disconnected += () => Interlocked.Increment(ref hostLost);
        var viewer2 = Link.Viewer(DeviceKey.Create(), Local(host2), Quick);
        Assert.True(Within(() => host2.Snapshot().State == LinkState.Connected));
        viewer2.Dispose();
        Assert.True(Within(() => hostLost == 1 && host2.Snapshot().State == LinkState.Waiting), "remote PC did not notice");
    }

    [Fact]
    public void A_second_viewer_takes_over_from_the_first()
    {
        using var hostKey = DeviceKey.Create();
        using var host = Link.Host(hostKey, port: 0);
        int connected = 0, disconnected = 0;
        host.Connected += () => Interlocked.Increment(ref connected);
        host.Disconnected += () => Interlocked.Increment(ref disconnected);
        using var first = Link.Viewer(DeviceKey.Create(), Local(host));
        Assert.True(Within(() => connected == 1));

        using var secondKey = DeviceKey.Create();
        using var second = Link.Viewer(secondKey, Local(host));
        var frames = new ConcurrentQueue<AssembledFrame>();
        second.FrameReceived += frames.Enqueue;
        Assert.True(Within(() => connected == 2 && disconnected == 1));
        Assert.Equal(secondKey.Fingerprint, host.Snapshot().PeerFingerprint);

        host.SendFrame(new byte[1000], keyFrame: true);
        Assert.True(Within(() => frames.Count == 1));
    }

    [Fact]
    public void Packets_from_a_stranger_change_nothing()
    {
        using var host = Link.Host(DeviceKey.Create(), port: 0);
        using var stranger = new UdpClient();
        var random = new Random(3);
        for (int i = 0; i < 200; i++)
        {
            var junk = new byte[random.Next(1, 1200)];
            random.NextBytes(junk);
            if (i % 4 == 0) junk[0] = (byte)PacketKind.Finish;
            if (i % 4 == 1) junk[0] = (byte)PacketKind.Sealed;
            stranger.Send(junk, junk.Length, Local(host));
        }

        Assert.True(Within(() => host.Snapshot().PacketsReceived >= 150));
        var snapshot = host.Snapshot();
        Assert.Equal(LinkState.Waiting, snapshot.State);
        Assert.Null(snapshot.PeerFingerprint);
    }
}
