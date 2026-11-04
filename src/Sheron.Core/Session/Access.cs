using System.Diagnostics;
using System.Net;
using Sheron.Core.Security;

namespace Sheron.Core.Session;

/// <summary>
/// A viewer waiting for the person at the remote PC to accept (TRD › Passwords and consent).
/// <see cref="Accept"/> or <see cref="Deny"/> once; with no answer it is refused by itself when the time is up.
/// </summary>
public sealed class ConsentRequest
{
    private readonly HostAccess _owner;
    private readonly long _startedAt = Stopwatch.GetTimestamp();

    internal ConsentRequest(HostAccess owner, int session, IPEndPoint? viewer, byte[]? viewerFingerprint, TimeSpan timeout)
    {
        _owner = owner;
        Session = session;
        Viewer = viewer;
        ViewerFingerprint = viewerFingerprint;
        Timeout = timeout;
    }

    internal int Session { get; }

    /// <summary>Where the viewer's packets come from (the relay, on a relayed path).</summary>
    public IPEndPoint? Viewer { get; }

    public byte[]? ViewerFingerprint { get; }

    public TimeSpan Timeout { get; }

    public TimeSpan TimeLeft
    {
        get
        {
            var left = Timeout - Stopwatch.GetElapsedTime(_startedAt);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    public void Accept() => _owner.Decide(this, AccessResult.LetIn);

    public void Deny() => _owner.Decide(this, AccessResult.Denied);
}

/// <summary>
/// The remote PC's side of letting a viewer in (Plan steps 16 and 16b, TRD › Passwords and consent): after the handshake
/// it waits for Access request, checks it with the <see cref="AccessGate"/>, asks the person when there is no
/// password, and answers the viewer. Until the viewer is let in, every other message is dropped; after,
/// they come out of <see cref="ControlReceived"/>. Events come on the link's thread, a timer's, or the caller's.
/// </summary>
public sealed class HostAccess : IDisposable
{
    public static readonly TimeSpan DefaultConsentTimeout = TimeSpan.FromSeconds(30);

    private readonly Link _link;
    private readonly TimeSpan _consentTimeout;
    private readonly object _lock = new();
    private int _session;
    private bool _letIn;
    private bool _checking;
    private ConsentRequest? _pending;
    private Timer? _noAnswer;

    public HostAccess(Link link, AccessGate gate, TimeSpan? consentTimeout = null)
    {
        _link = link;
        Gate = gate;
        _consentTimeout = consentTimeout ?? DefaultConsentTimeout;
        _link.Connected += OnSessionStarted;
        _link.Disconnected += OnSessionEnded;
        _link.ControlReceived += OnControl;
    }

    public AccessGate Gate { get; }

    /// <summary>Is the current viewer let in (capture and input allowed)?</summary>
    public bool IsLetIn
    {
        get
        {
            lock (_lock) return _letIn;
        }
    }

    /// <summary>The request waiting for the person, if any.</summary>
    public ConsentRequest? Pending
    {
        get
        {
            lock (_lock) return _pending;
        }
    }

    /// <summary>The viewer is let in: start capture.</summary>
    public event Action? LetIn;

    /// <summary>Show the request to the person.</summary>
    public event Action<ConsentRequest>? ConsentNeeded;

    /// <summary>The request was answered, timed out, or its viewer went: hide it.</summary>
    public event Action? ConsentEnded;

    /// <summary>A control message from a viewer who is let in: input and keyframe requests.</summary>
    public event Action<byte[]>? ControlReceived;

    private void OnSessionStarted() => NewSession();

    private void OnSessionEnded() => NewSession();

    /// <summary>A session starts or ends: nothing carries over to the next viewer.</summary>
    private void NewSession()
    {
        bool hadRequest;
        lock (_lock)
        {
            _session++;
            _letIn = _checking = false;
            hadRequest = EndRequest();
        }
        if (hadRequest) ConsentEnded?.Invoke();
    }

    private void OnControl(byte[] message)
    {
        if (ControlMessage.TypeOf(message) != ControlType.AccessRequest)
        {
            bool letIn;
            lock (_lock) letIn = _letIn;
            if (letIn) ControlReceived?.Invoke(message);
            return;
        }

        int session;
        lock (_lock)
        {
            // One request at a time, and none once let in or while the person is asked.
            if (_letIn || _checking || _pending is not null || !ControlMessage.TryReadAccessRequest(message, out _)) return;
            _checking = true;
            session = _session;
        }
        ControlMessage.TryReadAccessRequest(message, out string password);
        var viewer = _link.Snapshot().PeerFingerprint;
        // The fixed password takes one PBKDF2 (about 0.3 s): not on the link's thread.
        _ = Task.Run(() => OnChecked(session, Gate.Check(password, viewer)));
    }

    private void OnChecked(int session, AccessCheck check)
    {
        ConsentRequest? request = null;
        lock (_lock)
        {
            if (session != _session) return;
            _checking = false;
            if (check.Result == AccessResult.AskPerson)
            {
                var viewer = _link.Snapshot();
                request = new ConsentRequest(this, session, viewer.Peer, viewer.PeerFingerprint, _consentTimeout);
                _pending = request;
                _noAnswer = new Timer(_ => Decide(request, AccessResult.NoAnswer), null, _consentTimeout, System.Threading.Timeout.InfiniteTimeSpan);
            }
        }

        switch (check.Result)
        {
            case AccessResult.LetIn:
                Let(session);
                break;
            case AccessResult.AskPerson:
                Answer(AccessResult.AskPerson, check.TriesLeft, _consentTimeout);
                ConsentNeeded?.Invoke(request!);
                break;
            default:
                Answer(check.Result, check.TriesLeft, check.LockedFor);
                break;
        }
    }

    /// <summary>The person's answer, or no answer when the time ran out. Only the first counts.</summary>
    internal void Decide(ConsentRequest request, AccessResult result)
    {
        lock (_lock)
        {
            if (_pending != request || request.Session != _session) return;
            EndRequest();
        }
        if (result == AccessResult.LetIn)
        {
            Let(request.Session);
        }
        else
        {
            // Deny and no answer count against this viewer (TRD › Requests).
            if (request.ViewerFingerprint is { } viewer) Gate.Refused(viewer);
            Answer(result, 0, TimeSpan.Zero);
        }
        ConsentEnded?.Invoke();
    }

    private void Let(int session)
    {
        lock (_lock)
        {
            if (session != _session) return;
            _letIn = true;
        }
        // The answer goes first, so the viewer hears it before the video format.
        Answer(AccessResult.LetIn, AccessGate.MaxTries, TimeSpan.Zero);
        LetIn?.Invoke();
    }

    private void Answer(AccessResult result, int triesLeft, TimeSpan time) =>
        _link.SendControl(ControlMessage.Answer(new AccessAnswer(result, triesLeft, (int)Math.Ceiling(time.TotalSeconds))));

    /// <summary>Under the lock: drops the waiting request and its timer. True when there was one.</summary>
    private bool EndRequest()
    {
        _noAnswer?.Dispose();
        _noAnswer = null;
        bool had = _pending is not null;
        _pending = null;
        return had;
    }

    public void Dispose()
    {
        _link.Connected -= OnSessionStarted;
        _link.Disconnected -= OnSessionEnded;
        _link.ControlReceived -= OnControl;
        lock (_lock) EndRequest();
    }
}

/// <summary>
/// The viewer's side (Plan step 16): sends the password once the session is up, and hears the remote PC's answer.
/// <see cref="TryAgain"/> sends another password in the same session after a wrong one. With connect by ID (Plan step
/// 14) the remote PC's key from the server is checked first: a different key gets no password.
/// </summary>
public sealed class ViewerAccess : IDisposable
{
    private readonly Link _link;
    private readonly byte[]? _expectedFingerprint;
    private readonly object _lock = new();
    private string _password;
    private bool _sent;
    private AccessAnswer? _answer;
    private volatile bool _wrongKey;

    /// <param name="expectedHostKey">The remote PC's device public key as the server gave it; null for connect by IP.</param>
    public ViewerAccess(Link link, string password, byte[]? expectedHostKey = null)
    {
        _link = link;
        _password = password;
        _expectedFingerprint = expectedHostKey is null ? null : KeyFingerprint.Of(expectedHostKey);
        _link.Connected += Send;
        _link.ControlReceived += OnControl;
        // The session may already be up.
        if (_link.Snapshot().State == LinkState.Connected) Send();
    }

    /// <summary>The latest answer; null until one comes.</summary>
    public AccessAnswer? Answer
    {
        get
        {
            lock (_lock) return _answer;
        }
    }

    public bool IsLetIn => Answer?.Result == AccessResult.LetIn;

    /// <summary>The session's remote key is not the one the server gave for that ID: no password was sent.</summary>
    public bool WrongHostKey => _wrongKey;

    /// <summary>The remote key did not match: stop the session and warn.</summary>
    public event Action? HostKeyMismatch;

    /// <summary>An answer came: on the link's thread.</summary>
    public event Action<AccessAnswer>? Answered;

    /// <summary>After Wrong: send another password in the same session.</summary>
    public void TryAgain(string password)
    {
        lock (_lock)
        {
            if (_answer?.Result != AccessResult.Wrong) return;
            _password = password;
            _sent = false;
        }
        Send();
    }

    private void Send()
    {
        if (_expectedFingerprint is not null)
        {
            var actual = _link.Snapshot().PeerFingerprint;
            if (actual is null) return;
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, _expectedFingerprint))
            {
                if (!_wrongKey)
                {
                    _wrongKey = true;
                    HostKeyMismatch?.Invoke();
                }
                return;
            }
        }
        string password;
        lock (_lock)
        {
            if (_sent) return;
            _sent = true;
            password = _password;
        }
        _link.SendControl(ControlMessage.AccessRequest(password));
    }

    private void OnControl(byte[] message)
    {
        if (!ControlMessage.TryReadAnswer(message, out var answer)) return;
        lock (_lock) _answer = answer;
        Answered?.Invoke(answer);
    }

    public void Dispose()
    {
        _link.Connected -= Send;
        _link.ControlReceived -= OnControl;
    }
}
