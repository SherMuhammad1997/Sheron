using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Sheron.Core.Rendezvous;
using Sheron.Core.Net;
using Sheron.Core.Security;
using Sheron.Core.Video;
using Sheron.Core.Session;
using LinkState = Sheron.Core.Session.LinkState;
using Sheron.Win.Encode;
using Sheron.Win.Security;
using Sheron.Win.View;

namespace Sheron.Win;

/// <summary>
/// Developer window for Plan steps 8–9 and 16 on the viewer: connects to a remote PC, sends the password, and once let
/// in shows its screen and sends the mouse and keyboard used over the picture (TRD › Input path, Passwords and
/// consent). It connects by the remote PC's ID through the Sheron server (Plan step 14, TRD › Connect by ID) or by IP
/// on the same network, on this PC's own socket and registration (<see cref="ThisPc"/>, Plan step 15a). Opened from
/// the home window, where the password is typed. The session window of Plan step 15b replaces it.
/// </summary>
internal sealed class ViewerTestForm : DevForm
{
    /// <summary>Frames held while the video format is not yet known (TRD › Connect by IP).</summary>
    private const int MaxEarlyFrames = 60;

    private readonly ThisPc _pc;
    private readonly IPEndPoint? _remote;
    private readonly uint? _id;
    private readonly string _password;
    // The path found on this PC's socket.
    private readonly CancellationTokenSource _closing = new();
    private TcpRelayTransport? _tcp;
    private PathKind? _path;
    private volatile string? _stage;
    private volatile bool _stageBad;
    private readonly bool _allowHardwareDecoder;
    private DeviceKey? _key;
    private string? _keyError;
    private ViewerAccess? _access;
    private bool _toldLetIn;
    private long _answeredAt;
    private volatile bool _timedFromCapture;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _formatLock = new();
    private readonly List<AssembledFrame> _early = [];
    private (int Width, int Height)? _format;
    private VideoViewer? _viewer;
    private Link? _link;
    private LinkSnapshot? _linkSnapshot;
    private VideoViewerSnapshot? _viewerSnapshot;
    private (TimeSpan At, int Frames, long Bytes) _last;
    private double _framesPerSecond;
    private double _megabitsPerSecond;
    // Lost frames, on the link's thread under _formatLock (TRD › Connect by IP › After a lost frame).
    private uint? _lastFrameNumber;
    private bool _waitingForKeyFrame;
    private long _askedAt;
    private int _keyFramesAsked;
    // Remote control, on the UI thread.
    private AbsolutePoint? _lastMove;
    private RemoteCursor? _cursor;

    /// <summary>Connect by IP on the same network (Plan step 8).</summary>
    public ViewerTestForm(ThisPc pc, IPEndPoint remote, string password, bool allowHardwareDecoder = true)
        : base($"viewing {remote} (developer)", $"Viewing {remote} · by IP · click the picture to control",
            new Size(1040, 768), headerHeight: 252, rowsPerColumn: 7, withVideo: true)
    {
        _pc = pc;
        _remote = remote;
        _password = password;
        _allowHardwareDecoder = allowHardwareDecoder;
    }

    /// <summary>Connect by ID through the Sheron server (Plan step 14).</summary>
    public ViewerTestForm(ThisPc pc, uint id, string password, bool allowHardwareDecoder = true)
        : base($"viewing {RendezvousWire.Show(id)} (developer)", $"Viewing {RendezvousWire.Show(id)} · by ID · click the picture to control",
            new Size(1040, 768), headerHeight: 252, rowsPerColumn: 7, withVideo: true)
    {
        _pc = pc;
        _id = id;
        _password = password;
        _allowHardwareDecoder = allowHardwareDecoder;
    }

    /// <summary>The remote PC let this viewer in, with the name it gave (on the UI thread, once).</summary>
    public event Action<string?>? LetIn;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var video = Video!;
        _viewer = new VideoViewer(video.Handle, video.ClientSize.Width, video.ClientSize.Height, _allowHardwareDecoder);
        video.SizeChanged += (_, _) => _viewer.Resize(video.ClientSize.Width, video.ClientSize.Height);
        // This PC's key and socket belong to the home window's ThisPc.
        _key = _pc.Key;
        if (_key is null)
        {
            _keyError = _pc.Problem ?? "This PC's Sheron is not running";
            StartRefresh();
            return;
        }
        StartRefresh();
        WireControl(video);
        if (_remote is not null) StartSession(Link.Viewer(_key, _pc.OpenViewer(_remote), _remote), expectedHostKey: null);
        else _ = ConnectById(_key, _id!.Value);
    }

    /// <summary>
    /// Finds the ID through this PC's registration and works out the path on its socket: direct, relay or TCP 443
    /// (TRD › Connect by ID). On the UI thread between awaits.
    /// </summary>
    private async Task ConnectById(DeviceKey key, uint id)
    {
        try
        {
            _stage = $"Finding {RendezvousWire.Show(id)} …";
            var found = await _pc.FindAsync(id, _closing.Token);
            if (found.Peer is not { } peer)
            {
                Stop(found.Reason switch
                {
                    NotFoundReason.Offline => "That PC is not online: its Sheron is closed, or it has no internet",
                    NotFoundReason.TooManyTries => "Too many tries: wait a minute, then try again",
                    NotFoundReason.Busy => "That PC is in another session: try again when it is free",
                    _ => "No PC has that ID: check the ID",
                });
                return;
            }

            _stage = "Finding the fastest path (direct first, then the relay) …";
            var path = await _pc.ConnectAsync(peer, _closing.Token);
            // The window may have closed meanwhile; its socket is gone then.
            if (_closing.IsCancellationRequested)
            {
                (path.Transport as TcpRelayTransport)?.Dispose();
                return;
            }
            _path = path.Kind;
            _tcp = path.Transport as TcpRelayTransport;
            _stage = null;
            // The remote PC's key must be the one the server gave for that ID (TRD › Connect by ID › Remote PC's key).
            StartSession(Link.Viewer(key, path.Transport, path.Remote), peer.PublicKey);
            if (path.Kind != PathKind.Direct) return;

            // A direct path must finish the session handshake in time, or the relay takes over (TRD › Direct handshake check).
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < ConnectTimings.Default.HandshakeWait && _link?.Snapshot().State == LinkState.Connecting)
                await Task.Delay(100, _closing.Token);
            if (_link?.Snapshot().State != LinkState.Connecting || _access?.WrongHostKey == true) return;

            _stage = "The direct path did not answer both ways: trying the relay …";
            _access?.Dispose();
            _link?.Dispose();
            _access = null;
            _link = null;
            path = await _pc.RelayAsync(peer, _closing.Token);
            if (_closing.IsCancellationRequested)
            {
                (path.Transport as TcpRelayTransport)?.Dispose();
                return;
            }
            _path = path.Kind;
            _tcp = path.Transport as TcpRelayTransport;
            _stage = null;
            StartSession(Link.Viewer(key, path.Transport, path.Remote), peer.PublicKey);
        }
        catch (TimeoutException)
        {
            Stop("No path to that PC: it did not answer directly or through the relay");
        }
        catch (Exception error) when (error is SocketException or IOException or InvalidDataException)
        {
            Stop($"Cannot reach the other PC through the Sheron server: {error.Message}");
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
            // The window closed while connecting.
        }
    }

    private void Stop(string reason)
    {
        _stage = reason;
        _stageBad = true;
    }

    /// <summary>The session on the chosen path; the password goes once it is up, inside it (TRD › Passwords and consent).</summary>
    private void StartSession(Link link, byte[]? expectedHostKey)
    {
        _link = link;
        _access = new ViewerAccess(link, _password, expectedHostKey, _pc.Name);
        _access.Answered += answer =>
        {
            Interlocked.Exchange(ref _answeredAt, Stopwatch.GetTimestamp());
            if (answer.Result != AccessResult.LetIn) return;
            OnUiThread(() =>
            {
                if (_toldLetIn) return;
                _toldLetIn = true;
                LetIn?.Invoke(answer.Name);
            });
        };
        _access.HostKeyMismatch += () => OnUiThread(() =>
        {
            Stop("Stopped: the remote PC's key is not the one the server gave for that ID. The password was not sent");
            var link = _link;
            _link = null;
            link?.Dispose();
        });
        link.FrameReceived += OnFrame;
        link.ControlReceived += OnControl;
    }

    /// <summary>Mouse and keys over the picture go to the remote PC (TRD › Input path).</summary>
    private void WireControl(VideoPanel video)
    {
        video.TakesKeyboard = true;
        video.MouseMove += (_, e) =>
        {
            if (RemotePoint(e.Location) is not { } at || at == _lastMove) return;
            _lastMove = at;
            Send(InputMessage.Move(at));
        };
        video.MouseDown += (_, e) => SendButton(e, down: true);
        video.MouseUp += (_, e) => SendButton(e, down: false);
        video.MouseWheel += (_, e) => SendWheel(e.Delta, horizontal: false, e.Location);
        video.HorizontalWheel += (delta, at) => SendWheel(delta, horizontal: true, at);
        video.KeyPressed += key => Send(InputMessage.Key(key));
        // Anything still held is let go when the picture loses the keyboard (Alt+Tab, another window).
        video.LostFocus += (_, _) => Send(InputMessage.ReleaseAll());
        // Over the picture the viewer's own pointer, in the remote shape, shows the remote pointer.
        video.MouseEnter += (_, _) => _viewer?.DrawPointer(false);
        video.MouseLeave += (_, _) => _viewer?.DrawPointer(true);
    }

    /// <summary>Where a point in the video window lands on the remote screen; null until the picture size is known.</summary>
    private AbsolutePoint? RemotePoint(Point at)
    {
        (int Width, int Height)? format;
        lock (_formatLock) format = _format;
        if (format is not { } f || Video is not { } video) return null;
        var picture = Letterbox.Fit(f.Width, f.Height, video.ClientSize.Width, video.ClientSize.Height);
        return ScreenMapping.ToAbsolute(at.X, at.Y, picture, f.Width, f.Height);
    }

    private void SendButton(MouseEventArgs e, bool down)
    {
        MouseButton? button = e.Button switch
        {
            MouseButtons.Left => MouseButton.Left,
            MouseButtons.Right => MouseButton.Right,
            MouseButtons.Middle => MouseButton.Middle,
            MouseButtons.XButton1 => MouseButton.Back,
            MouseButtons.XButton2 => MouseButton.Forward,
            _ => null,
        };
        if (button is { } b && RemotePoint(e.Location) is { } at) Send(InputMessage.Button(new MouseButtonInput(b, down, at)));
    }

    private void SendWheel(int delta, bool horizontal, Point location)
    {
        if (RemotePoint(location) is not { } at) return;
        Send(InputMessage.Wheel(new MouseWheelInput((short)Math.Clamp(delta, short.MinValue, short.MaxValue), horizontal, at)));
    }

    /// <summary>Input goes only once the viewer is let in; the remote PC would drop it anyway.</summary>
    private void Send(byte[] message)
    {
        if (_access?.IsLetIn == true) _link?.SendControl(message);
    }

    /// <summary>On the UI thread: the viewer's own pointer over the picture takes the remote shape.</summary>
    private void UseRemoteShape(Core.Capture.PointerShape shape)
    {
        if (Video is not { } video) return;
        RemoteCursor cursor;
        try
        {
            cursor = RemoteCursor.From(shape);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return;   // keep the pointer there is; the drawn pointer still shows the shape
        }
        video.Cursor = cursor.Cursor;
        _cursor?.Dispose();
        _cursor = cursor;
    }

    /// <summary>
    /// On the link's thread. A gap in the frame numbers means a lost frame: the viewer keeps the last clean picture and
    /// asks for a keyframe until one comes. Frames wait until the video format is known; from a keyframe on, at most 60.
    /// </summary>
    private void OnFrame(AssembledFrame frame)
    {
        lock (_formatLock)
        {
            bool gap = _lastFrameNumber is { } last && frame.Number != unchecked(last + 1);
            _lastFrameNumber = frame.Number;
            if (frame.KeyFrame)
            {
                _waitingForKeyFrame = false;
            }
            else if (gap && !_waitingForKeyFrame)
            {
                _waitingForKeyFrame = true;
                _viewer?.WaitForKeyFrame();
                AskForKeyFrame();
            }
            else if (_waitingForKeyFrame && Stopwatch.GetElapsedTime(_askedAt) >= Link.AskKeyFrameAgainAfter(_link?.Snapshot().RoundTrip))
            {
                AskForKeyFrame();
            }

            if (_format is not { } format)
            {
                if (frame.KeyFrame) _early.Clear();
                if (_early.Count < MaxEarlyFrames) _early.Add(frame);
                return;
            }
            Show(frame, format);
        }
    }

    private void AskForKeyFrame()
    {
        _askedAt = Stopwatch.GetTimestamp();
        _keyFramesAsked++;
        _link?.SendControl(ControlMessage.KeyFrameRequest());
    }

    /// <summary>On the link's thread.</summary>
    private void OnControl(byte[] message)
    {
        switch (ControlMessage.TypeOf(message))
        {
            case ControlType.VideoFormat when ControlMessage.TryReadVideoFormat(message, out int width, out int height):
                lock (_formatLock)
                {
                    _format = (width, height);
                    foreach (var frame in _early) Show(frame, (width, height));
                    _early.Clear();
                }
                break;
            case ControlType.PointerShape when ControlMessage.TryReadPointerShape(message, out var shape):
                _viewer?.OnPointer(null, shape);
                OnUiThread(() => UseRemoteShape(shape!));
                break;
            case ControlType.PointerPosition when ControlMessage.TryReadPointerPosition(message, out var position):
                _viewer?.OnPointer(position, null);
                break;
        }
    }

    /// <summary>
    /// The viewer's timer starts at capture on the remote PC once the link has matched the two clocks (TRD › Automatic
    /// quality › Delay figure), until then when the frame arrived whole.
    /// </summary>
    private void Show(AssembledFrame frame, (int Width, int Height) format)
    {
        long arrived = Stopwatch.GetTimestamp();
        long start = frame.CapturedAt is { } captured && captured <= arrived ? captured : arrived;
        _timedFromCapture = frame.CapturedAt is not null;
        _viewer?.OnVideo(new EncodedFrame(frame.Data, frame.KeyFrame), format.Width, format.Height, start, arrived);
    }

    protected override void ReadFigures()
    {
        _linkSnapshot = _link?.Snapshot();
        _viewerSnapshot = _viewer?.Snapshot();
        if (_linkSnapshot is { } l)
        {
            var now = _clock.Elapsed;
            double seconds = (now - _last.At).TotalSeconds;
            _framesPerSecond = (l.FramesReceived - _last.Frames) / seconds;
            _megabitsPerSecond = (l.BytesReceived - _last.Bytes) * 8 / seconds / 1e6;
            _last = (now, l.FramesReceived, l.BytesReceived);
        }
    }

    protected override IEnumerable<(string Label, string Value, Color? Color)> Rows()
    {
        var l = _linkSnapshot;
        var v = _viewerSnapshot;
        if (_keyError is not null)
        {
            yield return ("Status", _keyError, Bad);
            yield break;
        }
        string remote = _id is { } id ? $"ID {RendezvousWire.Show(id)}" : $"{_remote}";
        if (_stage is not null || l is null || v is null)
        {
            yield return ("Status", _stage ?? "Starting", _stageBad ? Bad : null);
            yield return ("Remote PC", remote, null);
            yield return ("This PC's ID", _pc.Id is { } own ? RendezvousWire.Show(own) : "–", null);
            yield return ("This PC's key", _key is null ? "–" : KeyFingerprint.Show(_key.Fingerprint), null);
            yield break;
        }

        // Left column: who and what.
        var (status, color) = l.State switch
        {
            LinkState.Connecting => ($"Connecting to {remote} …", null),
            LinkState.Lost => ("Connection lost (nothing heard for 5 s)", Bad),
            // Ended after an answer that was not Let in (busy, denied): that answer says why.
            LinkState.Ended => (_access?.Answer is { Result: not AccessResult.LetIn } ? AccessStatus() : null) ?? ("The remote PC ended the session", Bad),
            _ => AccessStatus() ?? (v.Status, (Color?)null),
        };
        yield return ("Status", status, color);
        yield return ("Remote PC", remote, null);
        yield return ("Path", _path is null ? $"Direct by IP · {l.Peer}" : ServerConfig.PathOf(l.Peer), Accent);
        yield return ("Remote key", l.PeerFingerprint is { } fingerprint ? KeyFingerprint.Show(fingerprint) : "–", null);
        yield return ("This PC's key", _key is null ? "–" : KeyFingerprint.Show(_key.Fingerprint), null);
        yield return ("Decoder", v.HeldBack > 0 ? $"{v.Decoder} · held back {v.HeldBack}" : v.Decoder, null);
        yield return ("Picture", v.VideoWidth == 0 ? "–"
            : $"{v.VideoWidth} × {v.VideoHeight} shown at {v.Picture.Width} × {v.Picture.Height}", null);

        // Right column: how well.
        var t = v.Latency;
        yield return ("Frames /s", $"received {_framesPerSecond:0} · shown {t.FramesPerSecond}", null);
        // PRD F8: the delay from capture on the remote PC to this screen, once the clocks are matched.
        yield return (_timedFromCapture ? "Delay" : "Arrived → shown",
            t.FramesPerSecond > 0 ? $"avg {t.AverageMs:0.0} · max {t.MaxMs:0.0} ms" + (_timedFromCapture ? " (capture → shown)" : "") : "–",
            _timedFromCapture ? Accent : null);
        // Where the delay goes (PRD A7): before arrival (remote PC + network), then decode, draw and the wait for the screen.
        if (_timedFromCapture && t.FramesPerSecond > 0)
            yield return ("Delay parts", $"remote PC + network {t.EncodeMs:0.0} · decode {t.DecodeMs:0.0} · draw {t.DrawMs:0.0} · screen {t.PresentMs + t.ScreenMs:0.0} ms", null);
        int asked;
        lock (_formatLock) asked = _keyFramesAsked;
        yield return ("Lost frames", $"{l.LostFrames} · keyframes asked {asked} · shards rebuilt {l.RebuiltShards}", l.LostFrames > 0 ? Bad : null);
        yield return ("Data", $"{_megabitsPerSecond:0.0} Mbit/s", null);
        yield return ("Round trip", (l.RoundTrip is { } rtt ? $"{rtt.TotalMilliseconds:0.0} ms" : "–")
            + $" · loss {l.PacketLoss:P1} · jitter {l.JitterMs} ms", null);
        yield return ("Sheron CPU", $"{CpuPercent:0.0} % of this PC (last 3 s)", null);
    }

    /// <summary>What the remote PC answered, until the viewer is let in (TRD › Passwords and consent).</summary>
    private (string, Color?)? AccessStatus()
    {
        if (_access?.Answer is not { } answer) return ("Connected · sending the password …", null);
        double waited = Stopwatch.GetElapsedTime(Interlocked.Read(ref _answeredAt)).TotalSeconds;
        return answer.Result switch
        {
            AccessResult.AskPerson => ($"Waiting for the person at the remote PC to accept ({Math.Max(0, Math.Ceiling(answer.Seconds - waited))} s)", Accent),
            AccessResult.Wrong when answer.TriesLeft > 0 =>
                ($"Wrong unattended password · {answer.TriesLeft} {(answer.TriesLeft == 1 ? "try" : "tries")} left · close this window to try again", Bad),
            AccessResult.Wrong => ($"Wrong password · that PC is locked for {answer.Seconds} s", Bad),
            AccessResult.Locked => ($"That PC is locked for {Math.Max(0, Math.Ceiling(answer.Seconds - waited))} s after 3 wrong passwords", Bad),
            AccessResult.Denied => ("The person at the remote PC said no", Bad),
            AccessResult.TooManyRequests => ($"That PC said no 3 times: it takes no requests from this PC for {Math.Max(1, Math.Ceiling(answer.Seconds / 60.0))} min", Bad),
            AccessResult.NoAnswer => ("Nobody at the remote PC answered in 30 s", Bad),
            AccessResult.Busy => ("That PC is in another session", Bad),
            _ => null,
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Let go of anything held, end the session at once (Bye), then the link: it hands frames to the viewer.
            // The key and socket stay: they are this PC's (ThisPc).
            Send(InputMessage.ReleaseAll());
            _closing.Cancel();
            _link?.Disconnect();
            _access?.Dispose();
            _link?.Dispose();
            _tcp?.Dispose();
            _viewer?.Dispose();
            if (Video is { } video) video.Cursor = Cursors.Default;
            _cursor?.Dispose();
        }
        base.Dispose(disposing);
    }
}
