using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Sheron.Core.Security;
using Sheron.Core.Session;

namespace Sheron.Tests.Session;

/// <summary>
/// Plan steps 16 and 16b with two links on this machine (TRD › Passwords and consent): PRD A3 (the person is asked;
/// Deny and no answer refuse) and A9 (three wrong passwords lock even the right one; three refused requests stop a
/// viewer's requests), the fixed password, and nothing getting through before the viewer is let in.
/// </summary>
[Collection(Timing.Name)]
public class AccessTests : IDisposable
{
    private static readonly LinkTimings Quick = new(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(600));
    private static readonly PasswordHash Fixed = PasswordHash.Create("unattended 1");

    private readonly DeviceKey _hostKey = DeviceKey.Create();
    private readonly DeviceKey _viewerKey = DeviceKey.Create();
    private readonly Link _host;
    private readonly AccessGate _gate = new(Fixed);
    private readonly HostAccess _access;
    private readonly ConcurrentQueue<ConsentRequest> _asked = new();
    private readonly ConcurrentQueue<byte[]> _passedOn = new();
    private int _letIn;
    private int _requestsEnded;
    private readonly List<IDisposable> _viewers = [];

    public AccessTests()
    {
        _host = Link.Host(_hostKey, port: 0, Quick);
        _access = new HostAccess(_host, _gate, consentTimeout: TimeSpan.FromSeconds(1));
        _access.ConsentNeeded += _asked.Enqueue;
        _access.ConsentEnded += () => Interlocked.Increment(ref _requestsEnded);
        _access.LetIn += () => Interlocked.Increment(ref _letIn);
        _access.ControlReceived += _passedOn.Enqueue;
    }

    public void Dispose()
    {
        foreach (var viewer in _viewers) viewer.Dispose();
        _access.Dispose();
        _host.Dispose();
        _hostKey.Dispose();
        _viewerKey.Dispose();
    }

    /// <summary>A viewer that connects and sends <paramref name="password"/> once the session is up.</summary>
    private (Link Link, ViewerAccess Access) Viewer(string password)
    {
        var link = Link.Viewer(_viewerKey, new IPEndPoint(IPAddress.Loopback, _host.LocalEndPoint.Port), Quick);
        var access = new ViewerAccess(link, password);
        _viewers.Add(access);
        _viewers.Add(link);
        return (link, access);
    }

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

    private static AccessAnswer WaitFor(ViewerAccess access, AccessResult result)
    {
        Assert.True(Within(() => access.Answer?.Result == result), $"wanted {result}, got {access.Answer}");
        return access.Answer!.Value;
    }

    [Fact]
    public void With_no_password_the_person_is_asked_and_Accept_lets_the_viewer_in()
    {
        var (link, access) = Viewer("");

        var asking = WaitFor(access, AccessResult.AskPerson);
        Assert.Equal(1, asking.Seconds);   // the test's 1 s; 30 s in the app
        Assert.True(_asked.TryDequeue(out var request));
        Assert.Equal(_viewerKey.Fingerprint, request!.ViewerFingerprint);
        Assert.False(_access.IsLetIn);

        // Input before the person accepts is dropped, not applied.
        link.SendControl(InputMessage.ReleaseAll());
        Thread.Sleep(100);
        Assert.Empty(_passedOn);

        request.Accept();

        WaitFor(access, AccessResult.LetIn);
        Assert.Equal(1, _letIn);
        Assert.Equal(1, _requestsEnded);
        link.SendControl(InputMessage.ReleaseAll());
        Assert.True(Within(() => _passedOn.Count == 1));
        // A second answer is too late: the first counted.
        request.Deny();
        Thread.Sleep(100);
        Assert.Equal(AccessResult.LetIn, access.Answer!.Value.Result);
    }

    [Fact]
    public void The_person_sees_the_viewer_s_name_and_the_viewer_hears_this_PC_s_name()
    {
        _access.Name = "Office-PC";
        var link = Link.Viewer(_viewerKey, new IPEndPoint(IPAddress.Loopback, _host.LocalEndPoint.Port), Quick);
        var access = new ViewerAccess(link, "", name: "Sher-Laptop");
        _viewers.Add(access);
        _viewers.Add(link);

        WaitFor(access, AccessResult.AskPerson);
        Assert.True(_asked.TryDequeue(out var request));
        Assert.Equal("Sher-Laptop", request!.ViewerName);
        request.Accept();

        Assert.Equal("Office-PC", WaitFor(access, AccessResult.LetIn).Name);
        Assert.Equal("Sher-Laptop", _access.ViewerName);
    }

    [Fact]
    public void While_this_PC_views_another_a_request_is_answered_busy_and_nobody_is_asked()
    {
        _access.Busy = true;
        var (_, access) = Viewer("unattended 1");

        WaitFor(access, AccessResult.Busy);
        Assert.Empty(_asked);
        Assert.False(_access.IsLetIn);
        Assert.Equal(TimeSpan.Zero, _gate.LockedFor);
    }

    [Fact]
    public void Deny_refuses_the_viewer()
    {
        var (_, access) = Viewer("");
        WaitFor(access, AccessResult.AskPerson);
        Assert.True(_asked.TryDequeue(out var request));

        request!.Deny();

        WaitFor(access, AccessResult.Denied);
        Assert.False(_access.IsLetIn);
        Assert.Equal(0, _letIn);
    }

    [Fact]
    public void With_no_answer_the_request_is_refused_when_its_time_is_up()
    {
        var (_, access) = Viewer("");
        WaitFor(access, AccessResult.AskPerson);

        WaitFor(access, AccessResult.NoAnswer);
        Assert.Null(_access.Pending);
        Assert.Equal(1, _requestsEnded);
        Assert.Equal(0, _letIn);
    }

    [Fact]
    public void The_fixed_password_lets_in_at_once_with_nobody_asked()
    {
        var (_, access) = Viewer("unattended 1");

        WaitFor(access, AccessResult.LetIn);
        Assert.Empty(_asked);
        Assert.True(_access.IsLetIn);
    }

    [Fact]
    public void Three_wrong_passwords_lock_out_even_the_right_one()
    {
        var (_, access) = Viewer("guess 1");
        Assert.Equal(2, WaitFor(access, AccessResult.Wrong).TriesLeft);
        access.TryAgain("guess 2");
        Assert.True(Within(() => access.Answer?.TriesLeft == 1));
        access.TryAgain("guess 3");
        Assert.True(Within(() => access.Answer?.TriesLeft == 0));
        Assert.Equal(new AccessAnswer(AccessResult.Wrong, 0, 30), access.Answer);

        // A new viewer with the right password is locked out too.
        var (_, second) = Viewer("");
        var locked = WaitFor(second, AccessResult.Locked);
        Assert.InRange(locked.Seconds, 29, 30);
        Assert.Empty(_asked);
    }

    [Fact]
    public void A_viewer_denied_three_times_gets_no_more_requests_through_and_nobody_is_asked()
    {
        for (int i = 0; i < 3; i++)
        {
            var (_, access) = Viewer("");
            WaitFor(access, AccessResult.AskPerson);
            Assert.True(_asked.TryDequeue(out var request));
            request!.Deny();
            WaitFor(access, AccessResult.Denied);
        }

        var (_, fourth) = Viewer("");
        var refused = WaitFor(fourth, AccessResult.TooManyRequests);
        Assert.InRange(refused.Seconds, 590, 600);
        Assert.Empty(_asked);
    }
}
