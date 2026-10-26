using System.Diagnostics;
using Sheron.Core.Net;
using Sheron.Core.Security;
using Xunit.Abstractions;

namespace Sheron.Tests.Security;

[Collection(Timing.Name)]
public class SealingSpeedTests(ITestOutputHelper output)
{
    /// <summary>
    /// Speed first (CLAUDE.md): sealing and opening a typical frame's 20 packets of 1,168 bytes stays far inside the
    /// TRD's encrypt budget of under 1 ms per frame.
    /// </summary>
    [Fact]
    public void Sealing_and_opening_a_frame_takes_well_under_a_millisecond()
    {
        using var viewerKey = DeviceKey.Create();
        using var hostKey = DeviceKey.Create();
        var (viewer, host) = HandshakeTests.Connect(viewerKey, hostKey);
        var plain = HandshakeTests.Plain(PacketLimits.MaxPlain, 5);
        var clock = new Stopwatch();
        const int frames = 300;
        for (int frame = -10; frame < frames; frame++)   // 10 frames of warm-up
        {
            if (frame == 0) clock.Restart();
            for (int i = 0; i < 20; i++) Assert.True(host.TryOpen(viewer.Seal(plain), out _));
        }
        double ms = clock.Elapsed.TotalMilliseconds / frames;
        output.WriteLine($"seal + open 20 packets of {PacketLimits.MaxPlain} bytes: {ms:0.000} ms");
        Assert.True(ms < 1.0, $"{ms:0.000} ms per frame");
    }
}
