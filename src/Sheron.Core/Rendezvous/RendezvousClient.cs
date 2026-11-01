using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Sheron.Core.Security;

namespace Sheron.Core.Rendezvous;

/// <summary>What the server answered to a Find: the other PC, or why there is none.</summary>
public sealed record FindResult(PeerInfo? Peer, NotFoundReason? Reason);

/// <summary>
/// A PC's link to the Sheron server (TRD › Server › Rendezvous protocol): TLS checked by the server's pinned key,
/// registration with the device key, finding other PCs by ID, and being found. Pings every 25 s to stay online.
/// Events are raised on the client's own reading task.
/// </summary>
public sealed class RendezvousClient : IAsyncDisposable
{
    public static readonly TimeSpan DefaultPingEvery = TimeSpan.FromSeconds(25);

    private readonly TcpClient _tcp;
    private readonly SslStream _tls;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<FindResult>> _finding = new();
    private readonly TimeSpan _pingEvery;
    private Task? _reading;
    private Task? _pinging;
    private int _closed;

    private RendezvousClient(TcpClient tcp, SslStream tls, TimeSpan pingEvery)
    {
        _tcp = tcp;
        _tls = tls;
        _pingEvery = pingEvery;
    }

    /// <summary>This PC's 9-digit ID, tied to its device key.</summary>
    public uint Id { get; private set; }

    /// <summary>Another PC wants to connect to this one.</summary>
    public event Action<PeerInfo>? Incoming;

    /// <summary>The server closed the link, or it broke.</summary>
    public event Action? Disconnected;

    /// <summary>
    /// Connects, checks the server's certificate against <paramref name="serverPin"/> (SHA-256 of its public key) and
    /// registers <paramref name="device"/>. Throws <see cref="AuthenticationException"/> when the pin does not match.
    /// </summary>
    /// <param name="addresses">This PC's LAN addresses, with the UDP port of its link socket.</param>
    public static async Task<RendezvousClient> ConnectAsync(string host, int port, byte[] serverPin, DeviceKey device,
        IReadOnlyList<PeerAddress> addresses, CancellationToken cancel = default, TimeSpan? pingEvery = null)
    {
        var tcp = new TcpClient { NoDelay = true };
        SslStream? tls = null;
        try
        {
            await tcp.ConnectAsync(host, port, cancel).ConfigureAwait(false);
            tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                // Trusted by its pinned key alone, not by a certificate authority or its name (TRD).
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && CryptographicOperations.FixedTimeEquals(RendezvousWire.Pin(certificate), serverPin),
            }, cancel).ConfigureAwait(false);

            var client = new RendezvousClient(tcp, tls, pingEvery ?? DefaultPingEvery);
            await client.RegisterAsync(device, addresses, cancel).ConfigureAwait(false);
            client._reading = client.ReadLoop();
            client._pinging = client.PingLoop();
            return client;
        }
        catch
        {
            tls?.Dispose();
            tcp.Dispose();
            throw;
        }
    }

    private async Task RegisterAsync(DeviceKey device, IReadOnlyList<PeerAddress> addresses, CancellationToken cancel)
    {
        var challenge = await RendezvousWire.ReadAsync(_tls, cancel).ConfigureAwait(false);
        if (challenge is not { Type: RendezvousType.Challenge, Body: var body }
            || body.Length != 1 + RendezvousWire.ChallengeSize || body[0] != RendezvousWire.Version)
            throw new InvalidDataException("The server did not send a challenge.");

        byte[] signature = device.Sign(RendezvousWire.RegisterSigned(body.AsSpan(1)));
        byte[] register = [.. device.PublicKey, .. signature, .. RendezvousWire.Addresses(addresses)];
        await SendAsync(RendezvousType.Register, register, cancel).ConfigureAwait(false);

        var registered = await RendezvousWire.ReadAsync(_tls, cancel).ConfigureAwait(false);
        if (registered is not { Type: RendezvousType.Registered, Body: var idBody }
            || !RendezvousWire.TryReadId(idBody, out uint id) || !RendezvousWire.IsId(id))
            throw new InvalidDataException("The server refused to register this PC.");
        Id = id;
    }

    /// <summary>Asks the server for the PC with <paramref name="id"/>; the server also tells that PC about this one.</summary>
    public async Task<FindResult> FindAsync(uint id, CancellationToken cancel = default)
    {
        var answer = _finding.GetOrAdd(id, _ => new TaskCompletionSource<FindResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        await SendAsync(RendezvousType.Find, RendezvousWire.Id(id), cancel).ConfigureAwait(false);
        using (cancel.Register(() => answer.TrySetCanceled(cancel)))
            return await answer.Task.ConfigureAwait(false);
    }

    private async Task ReadLoop()
    {
        try
        {
            while (await RendezvousWire.ReadAsync(_tls, _closing.Token).ConfigureAwait(false) is { } message)
            {
                switch (message.Type)
                {
                    case RendezvousType.Found when RendezvousWire.TryReadPeer(message.Body, out var peer):
                        if (_finding.TryRemove(peer!.Id, out var found)) found.TrySetResult(new FindResult(peer, null));
                        break;
                    case RendezvousType.NotFound when message.Body.Length == 5 && RendezvousWire.TryReadId(message.Body, out uint id):
                        if (_finding.TryRemove(id, out var missing)) missing.TrySetResult(new FindResult(null, (NotFoundReason)message.Body[4]));
                        break;
                    case RendezvousType.Incoming when RendezvousWire.TryReadPeer(message.Body, out var caller):
                        Incoming?.Invoke(caller!);
                        break;
                    case RendezvousType.Pong:
                        break;
                    default:
                        return;   // the server breaks its own protocol: stop
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            Close();
        }
    }

    private async Task PingLoop()
    {
        try
        {
            while (!_closing.IsCancellationRequested)
            {
                await Task.Delay(_pingEvery, _closing.Token).ConfigureAwait(false);
                await SendAsync(RendezvousType.Ping, ReadOnlyMemory<byte>.Empty, _closing.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task SendAsync(RendezvousType type, ReadOnlyMemory<byte> body, CancellationToken cancel)
    {
        await _writing.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            await RendezvousWire.WriteAsync(_tls, type, body, cancel).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>Ends the link once: waiting Finds fail, and <see cref="Disconnected"/> is raised.</summary>
    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _closing.Cancel();
        foreach (var waiting in _finding.Values) waiting.TrySetException(new IOException("The link to the server closed."));
        _tls.Dispose();
        _tcp.Dispose();
        Disconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        if (_reading is not null) await _reading.ConfigureAwait(false);
        if (_pinging is not null) await _pinging.ConfigureAwait(false);
        _writing.Dispose();
        _closing.Dispose();
    }
}
