// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using AetherNet.Transport.Abstractions;
using Microsoft.Extensions.Logging;
using Windows.Devices.Enumeration;
using Windows.Devices.WiFiDirect;
using Windows.Networking.Sockets;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace AetherNet.Transport.Windows.Services;

/// <summary>
/// Wi-Fi Direct transport for Aether Green.
///
/// <para>
/// Architecture:
/// <list type="bullet">
///   <item><b>Advertising (Group Owner)</b>:
///     Call <see cref="StartAdvertising"/> — the service publishes a
///     <see cref="WiFiDirectAdvertisementPublisher"/> with the local UHID embedded in a
///     custom <see cref="WiFiDirectInformationElement"/> (OUI <c>AE 71 48</c>, type 0x01),
///     and opens a <see cref="StreamSocketListener"/> on <see cref="TcpPort"/>.
///     When a peer connects the Wi-Fi Direct link, the listener accepts their TCP connection.
///   </item>
///   <item><b>Connecting (Client)</b>:
///     Call <see cref="ConnectAsync"/> — the service enumerates discovered
///     Wi-Fi Direct devices, connects the first one whose UHID IE matches
///     <paramref name="peerUhid"/>, and opens a TCP <see cref="StreamSocket"/> to the
///     Group Owner's endpoint on <see cref="TcpPort"/>.
///   </item>
/// </list>
/// </para>
///
/// <para>
/// Framing: each message is prefixed with a 4-byte little-endian payload length,
/// then the raw bytes. No additional chunking — Wi-Fi Direct supports up to 64 KB
/// per write natively.
/// </para>
/// </summary>
public sealed class WinWifiDirectTransportService : IWifiDirectService, IAsyncDisposable
{
    // ── Aether custom IE identity ─────────────────────────────────────────────
    private static readonly byte[] AetherNetOui = [0xAE, 0x71, 0x48];
    private const byte   AetherNetOuiType = 0x01;
    private const string TcpPort       = "8888";

    // ── Fields ────────────────────────────────────────────────────────────────
    private readonly string _localNodeId;
    private readonly ILogger<WinWifiDirectTransportService>? _logger;

    private WiFiDirectAdvertisementPublisher? _publisher;
    private WiFiDirectConnectionListener?     _connectionListener;
    private StreamSocketListener?             _socketListener;
    private DeviceWatcher?                    _watcher;

    // deviceInfo.Id → DeviceInformation (populated by the DeviceWatcher)
    private readonly ConcurrentDictionary<string, DeviceInformation> _discovered = new();

    // peerUhid → ConnectedPeer
    private readonly ConcurrentDictionary<string, ConnectedPeer> _peers = new();

    private readonly CancellationTokenSource _cts  = new();
    private volatile bool _disposed;

    // ── ITransportService ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public string Name => "Aether Green (Wi-Fi Direct)";

    /// <inheritdoc />
    public bool IsAvailable => !_disposed && IsWifiDirectSupported();

    /// <inheritdoc />
    public long MaxBandwidthBps => 250_000_000; // ~250 Mbps typical Wi-Fi Direct

    /// <inheritdoc />
    public int MaxRangeMeters => 200;

    /// <inheritdoc />
    public int PowerCostRelative => 15;

    /// <inheritdoc />
    public int MaxConcurrentPeers => 8;

    /// <inheritdoc />
    public event Action<string, byte[]>? DataReceived;

    // ── IWifiDirectService ────────────────────────────────────────────────────

    /// <inheritdoc />
    public event Action<string>? PeerConnected;

    /// <inheritdoc />
    public event Action<string>? PeerDisconnected;

    // ── Construction ──────────────────────────────────────────────────────────

    /// <param name="localNodeId">UHID of this node; embedded in the Wi-Fi Direct advertisement.</param>
    /// <param name="logger">Optional logger.</param>
    public WinWifiDirectTransportService(
        string localNodeId,
        ILogger<WinWifiDirectTransportService>? logger = null)
    {
        _localNodeId = localNodeId ?? throw new ArgumentNullException(nameof(localNodeId));
        _logger      = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Begins advertising this node via Wi-Fi Direct and opens a TCP listener for
    /// incoming peer connections. Safe to call more than once (no-op after first call).
    /// </summary>
    public void StartAdvertising()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_publisher is not null) return;

        // ── Advertisement publisher ───────────────────────────────────────────
        _publisher = new WiFiDirectAdvertisementPublisher();
        _publisher.Advertisement.ListenStateDiscoverability =
            WiFiDirectAdvertisementListenStateDiscoverability.Normal;

        // Embed local UHID in a custom information element so peers can identify us.
        var ie = new WiFiDirectInformationElement();
        ie.Oui      = CryptographicBuffer.CreateFromByteArray(AetherNetOui);
        ie.OuiType  = AetherNetOuiType;
        ie.Value    = CryptographicBuffer.ConvertStringToBinary(
            _localNodeId, BinaryStringEncoding.Utf8);
        _publisher.Advertisement.InformationElements.Add(ie);

        _publisher.StatusChanged += OnPublisherStatusChanged;
        _publisher.Start();

        // ── Connection listener (accepts WFD connection requests) ─────────────
        _connectionListener = new WiFiDirectConnectionListener();
        _connectionListener.ConnectionRequested += OnConnectionRequested;

        // ── TCP socket listener (accepts data connections from clients) ────────
        _ = BindSocketListenerAsync();

        // ── Device watcher (discovers connectable peers) ──────────────────────
        StartWatcher();

        _logger?.LogInformation("[WFD] Advertising as '{NodeId}' on TCP port {Port}",
            _localNodeId, TcpPort);
    }

    /// <inheritdoc />
    public async Task<bool> ConnectAsync(
        string peerUhid,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_peers.ContainsKey(peerUhid))
        {
            _logger?.LogDebug("[WFD] Already connected to {Peer}", peerUhid);
            return true;
        }

        StartAdvertising(); // ensure watcher is running

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _cts.Token);

        foreach (var (deviceId, _) in _discovered)
        {
            try
            {
                var wfd = await WiFiDirectDevice.FromIdAsync(deviceId)
                    .AsTask(linked.Token);

                var remoteUhid = ReadUhidFromIes(wfd);
                if (remoteUhid != null && remoteUhid != peerUhid)
                {
                    wfd.Dispose();
                    continue;
                }

                var endpoints = wfd.GetConnectionEndpointPairs();

                if (endpoints.Count == 0)
                {
                    wfd.Dispose();
                    continue;
                }

                var socket = new StreamSocket();
                await socket.ConnectAsync(endpoints[0].RemoteHostName, TcpPort)
                    .AsTask(linked.Token);

                AddPeer(peerUhid, wfd, socket);
                // Announce our identity as the first framed message. The Group Owner cannot read
                // our advertisement IEs after connection (see ReadUhidFromIes), so without this it
                // cannot attribute inbound packets to us or address replies back — it consumes
                // exactly one identity frame, then treats the stream as data. Unwind the peer if
                // the handshake can't be sent (the link is unusable; fall through to next device).
                try
                {
                    await SendIdentityHandshakeAsync(peerUhid, linked.Token);
                }
                catch
                {
                    await DisconnectAsync(peerUhid);
                    throw;
                }
                _ = ReadLoopAsync(peerUhid, _peers[peerUhid].Reader, _cts.Token);

                PeerConnected?.Invoke(peerUhid);
                _logger?.LogInformation("[WFD] Connected to {Peer}", peerUhid);
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[WFD] Could not connect via device {Id}", deviceId);
            }
        }

        _logger?.LogWarning("[WFD] Peer '{Peer}' not found in discovered devices", peerUhid);
        return false;
    }

    /// <inheritdoc />
    public Task DisconnectAsync(string peerUhid, CancellationToken ct = default)
    {
        if (_peers.TryRemove(peerUhid, out var peer))
        {
            peer.Dispose();
            PeerDisconnected?.Invoke(peerUhid);
            _logger?.LogInformation("[WFD] Disconnected from {Peer}", peerUhid);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<bool> SendAsync(
        string peerUhid,
        byte[] data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_peers.TryGetValue(peerUhid, out var peer))
        {
            _logger?.LogWarning("[WFD] SendAsync: not connected to {Peer}", peerUhid);
            return false;
        }

        try
        {
            // Framing: 4-byte LE length prefix + payload.
            peer.Writer.WriteInt32(data.Length);
            peer.Writer.WriteBytes(data);
            await peer.Writer.StoreAsync().AsTask(cancellationToken);
            await peer.Writer.FlushAsync().AsTask(cancellationToken);

            _logger?.LogDebug("[WFD] ► TX {Bytes}B → {Peer}", data.Length, peerUhid);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[WFD] TX error → {Peer}", peerUhid);
            _ = DisconnectAsync(peerUhid);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendStreamAsync(
        string peerUhid,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return await SendAsync(peerUhid, ms.ToArray(), cancellationToken);
    }

    // Sends the local UHID as a single length-prefixed UTF-8 frame so a Group Owner can learn
    // who connected (it cannot read our advertisement IEs post-connection). Framing matches
    // SendAsync; the receiver consumes exactly one such frame before treating the stream as data.
    private async Task SendIdentityHandshakeAsync(string peerUhid, CancellationToken ct)
    {
        if (!_peers.TryGetValue(peerUhid, out var peer)) return;
        var idBytes = System.Text.Encoding.UTF8.GetBytes(_localNodeId);
        peer.Writer.WriteInt32(idBytes.Length);
        peer.Writer.WriteBytes(idBytes);
        await peer.Writer.StoreAsync().AsTask(ct);
        await peer.Writer.FlushAsync().AsTask(ct);
    }

    /// <inheritdoc />
    public bool IsConnected(string peerUhid) => _peers.ContainsKey(peerUhid);

    // ── Advertisement callbacks ───────────────────────────────────────────────

    private void OnPublisherStatusChanged(
        WiFiDirectAdvertisementPublisher sender,
        WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
    {
        _logger?.LogDebug("[WFD] Publisher status → {Status}", args.Status);
        if (args.Status == WiFiDirectAdvertisementPublisherStatus.Aborted)
            _logger?.LogWarning("[WFD] Advertisement aborted (error {Err})", args.Error);
    }

    private async void OnConnectionRequested(
        WiFiDirectConnectionListener sender,
        WiFiDirectConnectionRequestedEventArgs args)
    {
        try
        {
            var request = args.GetConnectionRequest();
            _logger?.LogInformation("[WFD] Incoming WFD connection request from '{Name}'",
                request.DeviceInformation.Name);

            var wfd = await WiFiDirectDevice.FromIdAsync(request.DeviceInformation.Id)
                .AsTask(_cts.Token);

            var peerUhid = ReadUhidFromIes(wfd) ?? request.DeviceInformation.Id;

            // The client will TCP-connect to our StreamSocketListener.
            // Store the WFD device; the socket will arrive via OnSocketConnectionReceived.
            _logger?.LogDebug("[WFD] WFD link established with {Peer} — awaiting TCP connect",
                peerUhid);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[WFD] Error accepting connection request");
        }
    }

    // ── TCP socket listener (Group Owner side) ────────────────────────────────

    private async Task BindSocketListenerAsync()
    {
        try
        {
            _socketListener = new StreamSocketListener();
            _socketListener.ConnectionReceived += OnSocketConnectionReceived;
            await _socketListener.BindServiceNameAsync(TcpPort).AsTask(_cts.Token);
            _logger?.LogDebug("[WFD] TCP listener bound on port {Port}", TcpPort);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[WFD] Failed to bind TCP listener on port {Port}", TcpPort);
        }
    }

    private void OnSocketConnectionReceived(
        StreamSocketListener sender,
        StreamSocketListenerConnectionReceivedEventArgs args)
    {
        var remote = args.Socket.Information.RemoteAddress.CanonicalName;
        _logger?.LogInformation("[WFD] TCP connection received from {Remote}", remote);

        // Group-Owner accept path. Register the socket immediately — crucially WITH a writer —
        // under a provisional key so we can address replies, then resolve the real peer UHID from
        // the client's first framed message (the identity handshake, see SendIdentityHandshakeAsync)
        // and re-key the entry in ReadLoopAsync. WfdDevice is null: the GO owns the link itself, it
        // holds no client-side WiFiDirectDevice for this peer.
        var tempKey = $"pending:{remote}";
        var peer = new ConnectedPeer(tempKey, null, args.Socket);
        _peers[tempKey] = peer;

        _ = ReadLoopAsync(tempKey, peer.Reader, _cts.Token);
    }

    // ── Device watcher ────────────────────────────────────────────────────────

    private void StartWatcher()
    {
        var selector = WiFiDirectDevice.GetDeviceSelector(
            WiFiDirectDeviceSelectorType.AssociationEndpoint);
        _watcher = DeviceInformation.CreateWatcher(selector);
        _watcher.Added   += (_, info) => _discovered[info.Id] = info;
        _watcher.Updated += (_, upd)  =>
        {
            if (_discovered.TryGetValue(upd.Id, out var existing))
            {
                existing.Update(upd);
                _discovered[upd.Id] = existing;
            }
        };
        _watcher.Removed += (sender, info) => _discovered.TryRemove(info.Id, out _);
        _watcher.Start();
    }

    // ── Read loop ─────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(
        string peerKey,
        DataReader reader,
        CancellationToken ct)
    {
        // On the Group-Owner accept path the socket is registered under a provisional "pending:*"
        // key and the client's first framed message is its identity handshake (see
        // SendIdentityHandshakeAsync). Consume that one frame, re-key the peer entry to the real
        // UHID, and only then surface subsequent frames as application data. On the client side
        // peerKey is already the real UHID, so awaitingIdentity is false and every frame is data.
        var currentKey = peerKey;
        var awaitingIdentity = peerKey.StartsWith("pending:", StringComparison.Ordinal);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Read 4-byte length prefix.
                uint loaded = await reader.LoadAsync(sizeof(int)).AsTask(ct);
                if (loaded < sizeof(int)) break;

                int payloadLen = reader.ReadInt32();
                if (payloadLen <= 0 || payloadLen > 64 * 1024 * 1024) break;

                // Read payload.
                loaded = await reader.LoadAsync((uint)payloadLen).AsTask(ct);
                if (loaded < (uint)payloadLen) break;

                var payload = new byte[payloadLen];
                reader.ReadBytes(payload);

                if (awaitingIdentity)
                {
                    awaitingIdentity = false;
                    var declaredUhid = System.Text.Encoding.UTF8.GetString(payload);
                    if (!string.IsNullOrWhiteSpace(declaredUhid) &&
                        _peers.TryRemove(currentKey, out var pending))
                    {
                        _peers[declaredUhid] = pending;
                        currentKey = declaredUhid;
                        PeerConnected?.Invoke(declaredUhid);
                        _logger?.LogInformation(
                            "[WFD] Peer identity resolved: {Remote} → {Uhid}", peerKey, declaredUhid);
                    }
                    continue; // the identity handshake is not application data
                }

                _logger?.LogDebug("[WFD] ◄ RX {Bytes}B ← {Peer}", payloadLen, currentKey);
                DataReceived?.Invoke(currentKey, payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[WFD] Read loop ended for {Peer}", currentKey);
            if (_peers.ContainsKey(currentKey))
                _ = DisconnectAsync(currentKey);
        }
    }

    // ── Peer management ───────────────────────────────────────────────────────

    private void AddPeer(string uhid, WiFiDirectDevice? wfd, StreamSocket socket)
    {
        var peer = new ConnectedPeer(uhid, wfd, socket);
        _peers[uhid] = peer;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsWifiDirectSupported()
    {
        try { return !string.IsNullOrEmpty(WiFiDirectDevice.GetDeviceSelector()); }
        catch { return false; }
    }

    private static string? ReadUhidFromIes(WiFiDirectDevice device)
    {
        // WiFiDirectDevice does not expose the remote peer's advertisement IEs after connection
        // in the WinRT API — IEs are only accessible on the advertiser side via
        // WiFiDirectAdvertisementPublisher.Advertisement.InformationElements.
        // Peer UHID identity is established instead via TCP handshake in the read loop.
        return null;
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _cts.CancelAsync();
        _cts.Dispose();

        _watcher?.Stop();
        _publisher?.Stop();
        _socketListener?.Dispose();

        foreach (var peer in _peers.Values)
            peer.Dispose();
        _peers.Clear();

        _logger?.LogInformation("[WFD] Disposed");
    }

    // ── ConnectedPeer ─────────────────────────────────────────────────────────

    private sealed class ConnectedPeer : IDisposable
    {
        public string           Uhid      { get; }
        public WiFiDirectDevice? WfdDevice { get; }
        public StreamSocket     Socket    { get; }
        public DataReader       Reader    { get; }
        public DataWriter       Writer    { get; }

        private bool _disposed;

        // wfdDevice is null for Group-Owner-accepted sockets: the GO owns the Wi-Fi Direct
        // link itself and never holds a client-side WiFiDirectDevice handle for the peer.
        public ConnectedPeer(string uhid, WiFiDirectDevice? wfdDevice, StreamSocket socket)
        {
            Uhid      = uhid;
            WfdDevice = wfdDevice;
            Socket    = socket;
            Reader    = new DataReader(socket.InputStream)  { ByteOrder = ByteOrder.LittleEndian };
            Writer    = new DataWriter(socket.OutputStream) { ByteOrder = ByteOrder.LittleEndian };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Socket.Dispose();
            WfdDevice?.Dispose();
        }
    }
}
