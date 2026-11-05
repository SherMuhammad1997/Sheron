using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Sheron.Core.Capture;
using Sheron.Core.Net;
using Sheron.Core.Rendezvous;
using Sheron.Core.Security;
using Sheron.Core.Session;
using Sheron.Core.Video;
using LinkState = Sheron.Core.Session.LinkState;
using Sheron.Win.Capture;
using Sheron.Win.Encode;
using Sheron.Win.Input;
using Sheron.Win.Security;

namespace Sheron.Win;

/// <summary>How this PC stands with the Sheron server.</summary>
internal enum ServerState
{
    Connecting,
    Online,
    /// <summary>The link is lost or the server cannot be reached; tried again every 10 s.</summary>
    Offline,
    /// <summary>The server's pin does not match: not our server. Not tried again.</summary>
    WrongServer,
}

/// <summary>
/// This PC on the network (Plan step 15a; TRD › Connect by ID › One registration per PC, One session at a time): one
/// UDP socket, one registration with the server, this PC's own session that viewers connect to, and the sharing of
/// this screen once a viewer is let in. Viewing another PC goes through the same socket and registration
/// (<see cref="BeginViewing"/>). Events come on the thread that made it (the UI thread).
/// </summary>
internal sealed class ThisPc : IDisposable
{
    private readonly SynchronizationContext _ui;
    private readonly bool _allowHardwareEncoder;
    private readonly CancellationTokenSource _closing = new();
    private readonly InputInjector _input = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _qualityLock = new();
    private readonly CpuMeter _cpu = new();
    private System.Threading.Timer? _cpuGuard;
    private QualityController _quality = new();
    private DeviceKey? _key;
    private UdpTransport? _udp;
    private PacketMux? _mux;
    private Link? _link;
    private HostAccess? _access;
    private volatile RendezvousClient? _client;
    private volatile HostRendezvous? _rendezvous;
    private CaptureLoop? _capture;
    private volatile bool _viewing;
    private volatile bool _shared;
    private (uint Id, IPEndPoint From)? _lastCaller;

    public ThisPc(int port = Link.DefaultPort, bool allowHardwareEncoder = true)
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        Port = port;
        _allowHardwareEncoder = allowHardwareEncoder;
        Name = PcName.Clean(Environment.MachineName);
    }

    public int Port { get; }

    /// <summary>This PC's name as others see it (TRD › Windows UI › PC name).</summary>
    public string Name { get; }

    /// <summary>Why this PC cannot share or connect (no secrets, port in use); null when all is well.</summary>
    public string? Problem { get; private set; }

    public DeviceKey? Key => _key;

    public AccessGate? Gate { get; private set; }

    /// <summary>This PC's ID; null until registered.</summary>
    public uint? Id => _client is { Id: not 0 } client ? client.Id : null;

    public ServerState Server { get; private set; } = ServerState.Connecting;

    /// <summary>A line about the server link for the developer window.</summary>
    public string ServerDetail { get; private set; } = "–";

    /// <summary>The address check's answer: UDP works here (false: viewers come over TCP 443).</summary>
    public bool UdpWorks => _rendezvous?.Connector.UdpWorks ?? false;

    public IPEndPoint? PublicUdp => _rendezvous?.Connector.PublicUdp;

    /// <summary>A viewer is let in: this screen is shared and the banner shows.</summary>
    public bool IsShared => _shared;

    /// <summary>This PC is viewing another.</summary>
    public bool IsViewing => _viewing;

    /// <summary>The request waiting for the person here, if any.</summary>
    public ConsentRequest? Pending => _access?.Pending;

    /// <summary>When the current viewer was let in.</summary>
    public DateTime SharedSince { get; private set; }

    /// <summary>Who is viewing (or asking): name, then ID or address.</summary>
    public (string Name, string Where) Viewer
    {
        get
        {
            var link = _link?.Snapshot();
            string where = link?.Peer is not { } peer ? "–"
                : _lastCaller is { } caller && caller.From.Equals(peer) ? $"ID {RendezvousWire.Show(caller.Id)}" : $"{peer.Address}";
            string name = _access?.ViewerName is { Length: > 0 } given ? given : "A viewer";
            return (name, where);
        }
    }

    /// <summary>The ID (or address) a consent request comes from.</summary>
    public string WhereFrom(ConsentRequest request) =>
        request.Viewer is { } from && _lastCaller is { } caller && caller.From.Equals(from) ? $"ID {RendezvousWire.Show(caller.Id)}"
        : request.Viewer is { } address ? $"{address.Address}" : "–";

    /// <summary>Something shown on the home changed: ID, server state, sharing.</summary>
    public event Action? Changed;

    public event Action<ConsentRequest>? ConsentNeeded;

    public event Action? ConsentEnded;

    /// <summary>A viewer was let in: show the banner.</summary>
    public event Action? SharingStarted;

    /// <summary>The viewer left, or Disconnect was pressed: hide the banner.</summary>
    public event Action? SharingEnded;

    /// <summary>Opens the socket and this PC's session, then registers with the server and stays registered.</summary>
    public void Start()
    {
        try
        {
            var secrets = SecretStore.ThisPc();
            _key = DeviceKey.Import(secrets.DevicePrivateKey);
            Gate = new AccessGate(secrets.FixedPassword);
            _udp = new UdpTransport(Port);
            _mux = new PacketMux(_udp);
            _link = Link.Host(_key, _mux.Session);
            _access = new HostAccess(_link, Gate) { Name = Name };
            // Capture runs only once the viewer is let in, so its first frame is a keyframe and nothing shows before.
            _access.LetIn += () => Post(StartSharing);
            _access.ConsentNeeded += request => Post(() => ConsentNeeded?.Invoke(request));
            _access.ConsentEnded += () => Post(() => ConsentEnded?.Invoke());
            _link.ReportReceived += report =>
            {
                if (_shared) OnReport(report);
            };
            // Nothing the viewer held stays pressed once it is gone (TRD › Input path › Stuck keys).
            _link.Disconnected += () =>
            {
                _input.ReleaseAll();
                Post(StopSharing);
            };
            // Only messages from a viewer who is let in come through.
            _access.ControlReceived += message =>
            {
                if (ControlMessage.TypeOf(message) == ControlType.KeyFrameRequest) Volatile.Read(ref _capture)?.RequestKeyFrame();
                else _input.Apply(message);
            };
            _cpuGuard = new System.Threading.Timer(_ => GuardCpu(), null, 500, 500);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Problem = $"Cannot read this PC's Sheron secrets: {error.Message}";
            return;
        }
        catch (SocketException)
        {
            Problem = $"UDP port {Port} is in use: is Sheron already open on this PC?";
            return;
        }
        _ = KeepRegistered(_key, _mux);
    }

    /// <summary>
    /// Registers with the Sheron server and stays registered, so viewers find this PC by its ID; a lost link is tried
    /// again every 10 s (TRD › Connect by ID).
    /// </summary>
    private async Task KeepRegistered(DeviceKey key, PacketMux mux)
    {
        while (!_closing.IsCancellationRequested)
        {
            RendezvousClient? client = null;
            HostRendezvous? rendezvous = null;
            try
            {
                SetServer(ServerState.Connecting, $"connecting to {ServerConfig.Host} …");
                var lan = LocalNetwork.IPv4Addresses()
                    .Select(ip => new PeerAddress(AddressKind.Lan, new IPEndPoint(ip, _udp!.LocalEndPoint.Port))).ToList();
                client = await RendezvousClient.ConnectAsync(ServerConfig.Host, ServerConfig.Port, ServerConfig.Pin, key, lan, _closing.Token);
                _closing.Token.ThrowIfCancellationRequested();
                var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                client.Disconnected += () => gone.TrySetResult();
                rendezvous = new HostRendezvous(client, mux, ServerConfig.UdpEndPoint) { Viewing = _viewing };
                rendezvous.PathFound += (caller, _, from) => _lastCaller = (caller.Id, from);
                _client = client;
                _rendezvous = rendezvous;
                if (_viewing || _shared) await client.SetBusyAsync(true, _closing.Token);
                bool udp = await rendezvous.CheckAddressAsync(_closing.Token);
                SetServer(ServerState.Online, udp
                    ? $"registered · UDP works, seen as {rendezvous.Connector.PublicUdp}"
                    : "registered · UDP blocked here: viewers come over TCP 443");
                await gone.Task.WaitAsync(_closing.Token);
                SetServer(ServerState.Offline, "link to the server lost · trying again in 10 s");
            }
            catch (AuthenticationException)
            {
                SetServer(ServerState.WrongServer, "the server's pin does not match: not our server");
                return;
            }
            catch (Exception error) when (error is SocketException or IOException or InvalidDataException)
            {
                SetServer(ServerState.Offline, $"cannot reach the server ({error.Message}) · trying again in 10 s");
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
            {
                return;   // closing
            }
            finally
            {
                _rendezvous = null;
                _client = null;
                rendezvous?.Dispose();
                if (client is not null) await client.DisposeAsync();
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _closing.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void SetServer(ServerState state, string detail)
    {
        ServerDetail = detail;
        Post(() =>
        {
            Server = state;
            Changed?.Invoke();
        });
    }

    // ---- Sharing this screen ----

    private void StartSharing()
    {
        StopCapture();
        if (_link is null || _access?.IsLetIn != true) return;
        // Each viewer starts at the best level; the viewer's reports and this PC's CPU move it (TRD › Automatic quality).
        lock (_qualityLock) _quality = new QualityController();
        _capture = new CaptureLoop(_allowHardwareEncoder, new LinkSender(_link));
        _shared = true;
        SharedSince = DateTime.Now;
        _ = TellServerBusy();
        SharingStarted?.Invoke();
        Changed?.Invoke();
    }

    private void StopSharing()
    {
        StopCapture();
        if (!_shared) return;
        _shared = false;
        _ = TellServerBusy();
        SharingEnded?.Invoke();
        Changed?.Invoke();
    }

    private void StopCapture()
    {
        _capture?.Dispose();
        _capture = null;
    }

    /// <summary>The banner's Disconnect: ends the viewer's session at once (PRD A6).</summary>
    public void EndSharing() => _link?.Disconnect();

    /// <summary>On the link's thread, every 250 ms while a viewer is let in.</summary>
    private void OnReport(LinkReport report)
    {
        lock (_qualityLock)
            if (_quality.OnReport(report, _clock.Elapsed)) Volatile.Read(ref _capture)?.SetQuality(_quality.Current);
    }

    /// <summary>The CPU guard: Sheron's own share of this PC (TRD › Automatic quality). On a timer thread.</summary>
    private void GuardCpu()
    {
        double percent;
        lock (_cpu) percent = _cpu.Read();
        CpuPercent = percent;
        if (Volatile.Read(ref _capture) is not { } capture) return;
        lock (_qualityLock)
            if (_quality.OnCpu(percent, _clock.Elapsed)) capture.SetQuality(_quality.Current);
    }

    /// <summary>Sheron's own CPU use over the last 3 s, in percent of this PC.</summary>
    public double CpuPercent { get; private set; }

    // ---- Viewing another PC ----

    /// <summary>
    /// This PC starts viewing another (TRD › Connect by ID › One session at a time): no one else gets in meanwhile,
    /// and a viewer still in its handshake here is dropped. False while this PC is shared or a request waits.
    /// </summary>
    public bool BeginViewing()
    {
        if (_shared || _viewing || _access?.Pending is not null || _mux is null) return false;
        _viewing = true;
        if (_access is not null) _access.Busy = true;
        if (_rendezvous is { } rendezvous) rendezvous.Viewing = true;
        _link?.Disconnect();
        _ = TellServerBusy();
        Changed?.Invoke();
        return true;
    }

    /// <summary>The viewing session ended: this PC can be viewed again.</summary>
    public void EndViewing()
    {
        if (!_viewing) return;
        _viewing = false;
        if (_access is not null) _access.Busy = false;
        if (_rendezvous is { } rendezvous) rendezvous.Viewing = false;
        _mux?.CloseViewer();
        _ = TellServerBusy();
        Changed?.Invoke();
    }

    private async Task TellServerBusy()
    {
        try
        {
            if (_client is { IsConnected: true } client) await client.SetBusyAsync(_viewing || _shared, _closing.Token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The link is gone; the next registration sends the state again.
        }
    }

    /// <summary>The server link while registered; throws when there is none.</summary>
    private (RendezvousClient Client, HostRendezvous Rendezvous) Registered() =>
        _client is { IsConnected: true } client && _rendezvous is { } rendezvous
            ? (client, rendezvous)
            : throw new IOException(Server == ServerState.WrongServer ? "the server's pin does not match" : "this PC is not connected to the Sheron server");

    public Task<FindResult> FindAsync(uint id, CancellationToken cancel) => Registered().Client.FindAsync(id, cancel);

    public Task<ConnectedPath> ConnectAsync(PeerInfo peer, CancellationToken cancel) => Registered().Rendezvous.ConnectAsync(peer, cancel);

    public Task<ConnectedPath> RelayAsync(PeerInfo peer, CancellationToken cancel) => Registered().Rendezvous.RelayAsync(peer, cancel);

    /// <summary>Connect by IP on the same network: packets from that address go to the viewer's session.</summary>
    public PacketQueue OpenViewer(IPEndPoint remote) => (_mux ?? throw new InvalidOperationException("Not started.")).OpenViewer(remote);

    /// <summary>Which of these PCs are online (the Recent list); empty when not registered or on error.</summary>
    public async Task<IReadOnlyList<(uint Id, PcState State)>> StatusAsync(IReadOnlyList<uint> ids, CancellationToken cancel)
    {
        if (ids.Count == 0 || _client is not { IsConnected: true } client) return [];
        try
        {
            return await client.StatusAsync(ids.Take(RendezvousWire.MaxStatusIds).ToList(), cancel);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return [];
        }
    }

    // ---- Figures for the developer window ----

    public LinkSnapshot? LinkSnapshot() => _link?.Snapshot();

    public CaptureLoopSnapshot? CaptureSnapshot() => Volatile.Read(ref _capture)?.Snapshot();

    public string Quality()
    {
        lock (_qualityLock) return $"{_quality.Current}{(_quality.CpuCap > 0 ? " · CPU guard" : "")}";
    }

    public InputInjector Input => _input;

    private void Post(Action action) => _ui.Post(_ => action(), null);

    public void Dispose()
    {
        _closing.Cancel();
        _cpuGuard?.Dispose();
        StopCapture();
        _rendezvous?.Dispose();
        _client?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _access?.Dispose();
        _link?.Dispose();
        _mux?.Dispose();
        _udp?.Dispose();
        _input.ReleaseAll();
        _key?.Dispose();
        lock (_cpu) _cpu.Dispose();
    }

    /// <summary>Hands what the capture loop makes to the link: the video format first, then frames and pointer changes.</summary>
    private sealed class LinkSender(Link link) : ICaptureSink
    {
        private (int Width, int Height) _format;

        public void OnVideo(EncodedFrame frame, int width, int height, long capturedAt, long encodedAt)
        {
            if ((width, height) != _format)
            {
                link.SendControl(ControlMessage.VideoFormat(width, height));
                _format = (width, height);
            }
            link.SendFrame(frame.Data, frame.KeyFrame, capturedAt);
        }

        public void OnPointer(PointerPosition? position, PointerShape? shape)
        {
            if (shape is not null) link.SendControl(ControlMessage.Pointer(shape));
            if (position is { } at) link.SendControl(ControlMessage.Pointer(at));
        }
    }
}
