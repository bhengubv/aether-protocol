// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Content;
using Android.Net.Wifi.P2p;
using Android.Net.Wifi.P2p.Nsd;
using Android.OS;
using AetherNet.Transport.Abstractions;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AetherNet.Sample.Platforms.Android.Transports;

/// <summary>
/// Real Wi-Fi Direct (Wi-Fi P2P) transport for AetherNet on Android — a native C#
/// <see cref="ITransportService"/>, not a bridge to any other app. One APK: this radio
/// ships inside the AetherNet app itself.
///
/// Flow: discover peers → the higher-addressed device deterministically calls connect()
/// (so both sides don't race) → the framework forms a group and elects a Group Owner →
/// GO opens a TCP server on 8888, the client dials it → a one-line UHID handshake tells
/// each side who the peer is → thereafter every frame is [4-byte LE length][payload] and
/// surfaces on <see cref="DataReceived"/> as (peerUhid, bytes).
/// </summary>
public sealed class AndroidWifiDirectTransportService : ITransportService, IRadio, IDisposable
{
    private const int TcpPort = 8888;
    private const string ServiceInstance = "aethernet";
    private const string ServiceType = "_aethernet._tcp";

    private readonly Context _context;
    private readonly string _localUhid;
    private readonly ILogger _logger;

    private readonly WifiP2pManager? _manager;
    private WifiP2pManager.Channel? _channel;
    private Receiver? _receiver;

    private string _thisDeviceAddress = string.Empty;
    private volatile bool _connecting;
    private volatile bool _groupFormed;
    private volatile bool _disposed;
    private Role _role = Role.None;

    private enum Role { None, Host, Join }

    // Live peer links, keyed by the peer UHID learned from the handshake.
    private readonly ConcurrentDictionary<string, PeerLink> _peers = new(StringComparer.Ordinal);
    private TcpListener? _server;

    public AndroidWifiDirectTransportService(Context context, string localUhid, ILogger logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _manager = context.GetSystemService(Context.WifiP2pService) as WifiP2pManager;
    }

    // ── ITransportService metadata ───────────────────────────────────────────
    public string Name => "Wi-Fi Direct";
    public bool IsAvailable => _manager is not null && !_disposed;
    public long MaxBandwidthBps => 250_000_000;   // ~250 Mbps practical
    public int MaxRangeMeters => 200;
    public int PowerCostRelative => 6;             // higher than BLE, lower than cellular
    public int MaxConcurrentPeers => 8;

    public event Action<string, byte[]>? DataReceived;

    /// <summary>Raised with the peer UHID once a peer completes the handshake and is linked.</summary>
    public event Action<string>? PeerLinked;

    /// <summary>Raised with a human-readable status line for the UI's radio log.</summary>
    public event Action<string>? Status;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void EnsureInitialized()
    {
        if (_channel is not null || _manager is null) return;
        _channel = _manager.Initialize(_context, Looper.MainLooper, null);
        _receiver = new Receiver(this);
        var filter = new IntentFilter();
        filter.AddAction(WifiP2pManager.WifiP2pStateChangedAction);
        filter.AddAction(WifiP2pManager.WifiP2pPeersChangedAction);
        filter.AddAction(WifiP2pManager.WifiP2pConnectionChangedAction);
        filter.AddAction(WifiP2pManager.WifiP2pThisDeviceChangedAction);
        _context.RegisterReceiver(_receiver, filter);
        L("radio initialised");
    }

    /// <summary>
    /// Bring the radio up, advertise the AetherNet DNS-SD service, and discover it. When a peer
    /// advertising our service is found (never a printer/router), connect(). Both phones do
    /// exactly this, so they mutually invite each other and the framework forms a group with no
    /// system dialog and no wrong-device pairing — and no fragile autonomous Group Owner.
    /// </summary>
    public void Link()
    {
        if (_manager is null) { L("Wi-Fi Direct unavailable on this device"); return; }
        EnsureInitialized();
        L("linking — advertising + discovering the AetherNet service…");
        var record = new Dictionary<string, string> { ["tag"] = _localUhid };
        var info = WifiP2pDnsSdServiceInfo.NewInstance(ServiceInstance, ServiceType, record);
        _manager.AddLocalService(_channel, info, new ActionListener("addLocalService", _logger));
        _manager.SetDnsSdResponseListeners(_channel, new ServiceResponseListener(this), new TxtRecordListener());
        _manager.AddServiceRequest(_channel, WifiP2pDnsSdServiceRequest.NewInstance(),
            new ActionListener("addServiceRequest", _logger));
        _manager.DiscoverServices(_channel, new ActionListener("discoverServices", _logger,
            onFailure: r => L($"discoverServices failed reason={r}")));
        _ = Task.Run(RediscoverLoopAsync);
    }

    /// <summary>
    /// Service discovery is one-shot and timing-asymmetric — one phone often finds the other's
    /// service before the reverse. Re-running it every few seconds lets BOTH sides find each
    /// other and BOTH call connect(); mutual connect() forms the group with no accept dialog.
    /// </summary>
    private async Task RediscoverLoopAsync()
    {
        while (!_disposed && !_groupFormed)
        {
            await Task.Delay(6000).ConfigureAwait(false);
            if (_disposed || _groupFormed || _manager is null || _channel is null) break;
            L("re-discovering the AetherNet service…");
            try
            {
                _manager.DiscoverServices(_channel, new ActionListener("re-discoverServices", _logger));
            }
            catch (Exception ex) { _logger.LogDebug(ex, "re-discover error"); }
        }
    }

    /// <summary>A joiner found a peer advertising our service — connect to that one only.</summary>
    private void OnServiceFound(string? instanceName, string? registrationType, WifiP2pDevice? src)
    {
        L($"service seen: {instanceName} / {registrationType} @ {src?.DeviceAddress}");
        if (_connecting || _groupFormed || src is null || _manager is null || _channel is null) return;
        if (registrationType?.Contains("aethernet", StringComparison.OrdinalIgnoreCase) != true &&
            instanceName?.Contains("aethernet", StringComparison.OrdinalIgnoreCase) != true) return;

        _connecting = true;
        L($"connecting to AetherNet peer {src.DeviceAddress}");
        var config = new WifiP2pConfig { DeviceAddress = src.DeviceAddress };
        _manager.Connect(_channel, config, new ActionListener("connect", _logger,
            onFailure: r => { _connecting = false; L($"connect() failed reason={r}"); }));
    }

    public void Discover()
    {
        if (_manager is null || _channel is null) return;
        Status?.Invoke("Discovering Wi-Fi Direct peers…");
        _manager.DiscoverPeers(_channel, new ActionListener("discoverPeers", _logger));
    }

    // ── Broadcast handling ─────────────────────────────────────────────────────

    /// <summary>Trace to logcat (tag AetherWFD), the ILogger, and the UI status line at once.</summary>
    private void L(string m)
    {
        global::Android.Util.Log.Info("AetherWFD", m);
        _logger.LogInformation("{Msg}", m);
        Status?.Invoke(m);
    }

    private void OnPeersChanged()
    {
        if (_manager is null || _channel is null) return;
        _manager.RequestPeers(_channel, new PeerListListener(list =>
        {
            L($"{list.DeviceList.Count} peer(s) discovered");
            foreach (var d in list.DeviceList)
                global::Android.Util.Log.Info("AetherWFD", $"  peer {d.DeviceAddress} status={d.Status}");
            // Connecting is driven by DNS-SD service discovery (OnServiceFound), never this raw
            // peer list — so we can't accidentally dial a printer/router that also shows up here.
        }));
    }

    private void OnThisDeviceChanged(WifiP2pDevice? self)
    {
        if (self?.DeviceAddress is { } addr) { _thisDeviceAddress = addr; L($"this device address = {addr}"); }
    }

    private void OnConnectionChanged(WifiP2pInfo? info)
    {
        L($"connection changed: groupFormed={info?.GroupFormed} isGO={info?.IsGroupOwner}");
        if (info is null || !info.GroupFormed) return;
        _groupFormed = true;
        if (info.IsGroupOwner)
        {
            L("role: Group Owner — starting TCP server");
            _ = Task.Run(RunServerAsync);
        }
        else
        {
            var go = info.GroupOwnerAddress?.HostAddress ?? "192.168.49.1";
            L($"role: Client — dialing GO {go}");
            _ = Task.Run(() => RunClientAsync(go));
        }
    }

    /// <summary>
    /// Both sides call connect() to the discovered peer; Android's framework negotiates a single
    /// group and elects the Group Owner. (The local device address is privacy-masked on modern
    /// Android, so a "who initiates" rule based on it can't work — both initiating is the robust path.)
    /// </summary>
    private void MaybeConnect(WifiP2pDeviceList list)
    {
        if (_connecting || _groupFormed || _manager is null || _channel is null) return;
        WifiP2pDevice? target = null;
        foreach (var d in list.DeviceList) { target = d; break; } // first (only, in a 2-phone test) peer
        if (target is null) return;

        _connecting = true;
        L($"initiating connect() to {target.DeviceAddress}");
        var config = new WifiP2pConfig { DeviceAddress = target.DeviceAddress };
        _manager.Connect(_channel, config, new ActionListener("connect", _logger,
            onFailure: r => { _connecting = false; L($"connect() failed reason={r}"); }));
    }

    // ── Sockets ────────────────────────────────────────────────────────────────

    private async Task RunServerAsync()
    {
        try
        {
            _server = new TcpListener(IPAddress.Any, TcpPort);
            _server.Start();
            L($"GO: TCP server listening on {TcpPort}");
            while (!_disposed)
            {
                var client = await _server.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleSocketAsync(client));
            }
        }
        catch (Exception ex) when (!_disposed) { _logger.LogError(ex, "Wi-Fi Direct server error"); }
    }

    private async Task RunClientAsync(string goAddress)
    {
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Parse(goAddress), TcpPort).ConfigureAwait(false);
            L("client: TCP connected to GO");
            await HandleSocketAsync(client).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_disposed) { _logger.LogError(ex, "Wi-Fi Direct client connect failed"); }
    }

    private async Task HandleSocketAsync(TcpClient client)
    {
        var stream = client.GetStream();
        // Handshake: announce our UHID first so the peer can key the link.
        await WriteFrameAsync(stream, System.Text.Encoding.UTF8.GetBytes("UHID:" + _localUhid)).ConfigureAwait(false);
        string? peerUhid = null;
        try
        {
            while (!_disposed)
            {
                var frame = await ReadFrameAsync(stream).ConfigureAwait(false);
                if (frame is null) break;
                if (peerUhid is null && frame.Length > 5 &&
                    System.Text.Encoding.UTF8.GetString(frame, 0, 5) == "UHID:")
                {
                    peerUhid = System.Text.Encoding.UTF8.GetString(frame, 5, frame.Length - 5);
                    _peers[peerUhid] = new PeerLink(client, stream);
                    L($"linked with {peerUhid}");
                    PeerLinked?.Invoke(peerUhid);
                    continue;
                }
                if (peerUhid is not null) DataReceived?.Invoke(peerUhid, frame);
            }
        }
        catch (Exception ex) when (!_disposed) { _logger.LogDebug(ex, "Wi-Fi Direct socket closed"); }
        finally
        {
            if (peerUhid is not null) _peers.TryRemove(peerUhid, out _);
            client.Dispose();
        }
    }

    // ── ITransportService send/query ────────────────────────────────────────────

    public async Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default)
    {
        if (!_peers.TryGetValue(peerUhid, out var link)) return false;
        try { await WriteFrameAsync(link.Stream, data).ConfigureAwait(false); return true; }
        catch (Exception ex) { _logger.LogDebug(ex, "Wi-Fi Direct send failed to {Uhid}", peerUhid); return false; }
    }

    public async Task<bool> SendStreamAsync(string peerUhid, Stream stream, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return await SendAsync(peerUhid, ms.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public bool IsConnected(string peerUhid) => _peers.ContainsKey(peerUhid);

    /// <summary>UHIDs of peers currently linked over this radio.</summary>
    public IReadOnlyCollection<string> ConnectedPeers => _peers.Keys.ToArray();

    // ── IRadio ──────────────────────────────────────────────────────────────────
    public bool IsLinked => !_peers.IsEmpty;
    public string? PeerTag => _peers.Keys.FirstOrDefault();
    public Task<bool> SendAsync(byte[] data)
        => PeerTag is { } p ? SendAsync(p, data) : Task.FromResult(false);
    public void Stop() => Dispose();

    // ── Framing: [4-byte little-endian length][payload] ─────────────────────────

    private static async Task WriteFrameAsync(NetworkStream s, byte[] payload)
    {
        var header = new byte[4];
        BitConverter.TryWriteBytes(header, payload.Length); // little-endian on all supported platforms
        await s.WriteAsync(header).ConfigureAwait(false);
        await s.WriteAsync(payload).ConfigureAwait(false);
        await s.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadFrameAsync(NetworkStream s)
    {
        var header = await ReadExactAsync(s, 4).ConfigureAwait(false);
        if (header is null) return null;
        var len = BitConverter.ToInt32(header, 0);
        if (len <= 0 || len > 64 * 1024 * 1024) return null;
        return await ReadExactAsync(s, len).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream s, int count)
    {
        var buf = new byte[count];
        var off = 0;
        while (off < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(off, count - off)).ConfigureAwait(false);
            if (n <= 0) return null;
            off += n;
        }
        return buf;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (_receiver is not null) _context.UnregisterReceiver(_receiver); } catch { }
        try { _server?.Stop(); } catch { }
        foreach (var link in _peers.Values) link.Client.Dispose();
        _peers.Clear();
        if (_manager is not null && _channel is not null)
            _manager.RemoveGroup(_channel, null);
    }

    private sealed record PeerLink(TcpClient Client, NetworkStream Stream);

    // ── Android listener/receiver adapters ──────────────────────────────────────

    private sealed class Receiver(AndroidWifiDirectTransportService owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            switch (intent?.Action)
            {
                case WifiP2pManager.WifiP2pPeersChangedAction:
                    owner.OnPeersChanged();
                    break;
                case WifiP2pManager.WifiP2pConnectionChangedAction:
                    owner.OnConnectionChanged(
                        intent.GetParcelableExtra(WifiP2pManager.ExtraWifiP2pInfo) as WifiP2pInfo);
                    break;
                case WifiP2pManager.WifiP2pThisDeviceChangedAction:
                    owner.OnThisDeviceChanged(
                        intent.GetParcelableExtra(WifiP2pManager.ExtraWifiP2pDevice) as WifiP2pDevice);
                    break;
            }
        }
    }

    private sealed class ActionListener(string op, ILogger logger, Action<int>? onFailure = null)
        : Java.Lang.Object, WifiP2pManager.IActionListener
    {
        public void OnSuccess() => global::Android.Util.Log.Info("AetherWFD", $"{op} ok");
        public void OnFailure(WifiP2pFailureReason reason)
        {
            global::Android.Util.Log.Warn("AetherWFD", $"{op} failed ({reason})");
            onFailure?.Invoke((int)reason);
        }
    }

    private sealed class PeerListListener(Action<WifiP2pDeviceList> onPeers)
        : Java.Lang.Object, WifiP2pManager.IPeerListListener
    {
        public void OnPeersAvailable(WifiP2pDeviceList peers) => onPeers(peers);
    }

    private sealed class ServiceResponseListener(AndroidWifiDirectTransportService owner)
        : Java.Lang.Object, WifiP2pManager.IDnsSdServiceResponseListener
    {
        public void OnDnsSdServiceAvailable(string? instanceName, string? registrationType, WifiP2pDevice? srcDevice)
            => owner.OnServiceFound(instanceName, registrationType, srcDevice);
    }

    private sealed class TxtRecordListener : Java.Lang.Object, WifiP2pManager.IDnsSdTxtRecordListener
    {
        public void OnDnsSdTxtRecordAvailable(string? fullDomainName,
            IDictionary<string, string>? txtRecordMap, WifiP2pDevice? srcDevice) { }
    }
}
#endif
