// SPDX-License-Identifier: MIT
#if ANDROID
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Android.Content;
using Android.Net;
using Android.Net.Wifi.Aware;
using Android.OS;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Platforms.Android.Transports;

/// <summary>
/// Wi-Fi Aware (NAN) — the radio this mesh actually wants.
///
/// <para>
/// Every other radio here has a shape the mesh has to work around. Wi-Fi Direct elects a group owner,
/// so the topology is decided by the radio rather than the routing layer, and forming a group can
/// drop the phone's existing internet. BLE is always on and carries about eleven kilobits. Aware has
/// neither problem: it is many-to-many with no owner, needs no pairing dialog, does not disturb the
/// phone's AP connection, and its discovery is designed to run continuously — which is the job BLE is
/// currently doing on a radio that then cannot carry the call it found.
/// </para>
///
/// <para>
/// <b>Untested. No phone this project has can run it.</b> <c>android.hardware.wifi.aware</c> is absent
/// on all three handsets (PROTOCOL_SPEC §5.6) — MediaTek and Kirin mid-range parts omit the HAL, and
/// it is Qualcomm flagships that carry it. So this is written from the documented API and will report
/// itself unavailable on every device here, which is the honest outcome: it appears in the picker
/// saying the phone does not have it, rather than appearing ready and failing silently.
/// </para>
///
/// <para>
/// Unlike NearLink, which is Huawei silicon on HarmonyOS and has no portable form, Aware is an open
/// Wi-Fi Alliance standard with a standard Android API. Code written once runs on any phone that
/// ships the HAL, which is why it is worth building before there is hardware to prove it on.
/// </para>
/// </summary>
internal sealed class AndroidWifiAwareTransportService : IRadio, IDisposable
{
    /// <summary>
    /// The service two copies of this app look for each other under.
    ///
    /// <para>
    /// Aware limits a service name to 15 bytes, lowercase, so this is as descriptive as it is allowed
    /// to be. Anything longer is rejected at publish time rather than truncated.
    /// </para>
    /// </summary>
    private const string ServiceName = "aethernet";

    /// <summary>
    /// The port on the Aware data path. Aware gives each link its own private network, so this does
    /// not collide with the Wi-Fi Direct radio using 8888 on the phone's ordinary network.
    /// </summary>
    private const int TcpPort = 8889;

    private readonly ILogger _logger;
    private readonly Func<string> _address;
    private readonly ConcurrentDictionary<string, PeerLink> _peers = new();

    private WifiAwareSession? _session;
    private PublishDiscoverySession? _publish;
    private SubscribeDiscoverySession? _subscribe;
    private ConnectivityManager? _connectivity;
    private ConnectivityManager.NetworkCallback? _networkCallback;
    private TcpListener? _server;
    private bool _disposed;

    private sealed record PeerLink(TcpClient Client, NetworkStream Stream);

    /// <param name="address">
    /// This device's current rotating wire address. A function rather than a value because it rotates:
    /// the radio must announce whatever it is now, never whatever it was when the radio was built.
    /// </param>
    public AndroidWifiAwareTransportService(Func<string> address, ILogger? logger = null)
    {
        _address = address ?? throw new ArgumentNullException(nameof(address));
        _logger = logger ?? NullLogger.Instance;
    }

    private static Context Ctx => global::Android.App.Application.Context;

    public string Name => "Wi-Fi Aware";

    /// <summary>
    /// Present, switched on, and new enough. All three matter separately, which is why the reason
    /// below distinguishes them — "not available" would send someone to the wrong settings screen.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return false;
            if (Ctx.PackageManager?.HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureWifiAware) != true)
                return false;

            var manager = (WifiAwareManager?)Ctx.GetSystemService(Context.WifiAwareService);
            return manager?.IsAvailable ?? false;
        }
    }

    public string? UnavailableReason
    {
        get
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(26))
                return "Wi-Fi Aware needs Android 8 or newer";

            if (Ctx.PackageManager?.HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureWifiAware) != true)
                return "this phone does not have Wi-Fi Aware — most mid-range chipsets leave it out";

            var manager = (WifiAwareManager?)Ctx.GetSystemService(Context.WifiAwareService);
            if (manager?.IsAvailable != true)
                return "Wi-Fi Aware is switched off — turn Wi-Fi and location on";

            return null;
        }
    }

    /// <summary>
    /// Only the switched-off case. Missing silicon is not something anyone can fix by tapping, and
    /// inviting them to try is a lie with a friendly tone.
    /// </summary>
    public bool IsFixable =>
        OperatingSystem.IsAndroidVersionAtLeast(26) &&
        Ctx.PackageManager?.HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureWifiAware) == true &&
        !IsAvailable;

    /// <summary>
    /// Deliberately declared BELOW Wi-Fi Direct until somebody measures it.
    ///
    /// <para>
    /// Aware data paths run on the same silicon as Wi-Fi Direct and should be comparable, and this
    /// number decides which radio a call goes out on. Claiming parity on reasoning alone is exactly
    /// the mistake BLE's throughput figure made twice (see <see cref="IRadio.MaxBandwidthBps"/>). It
    /// is set high enough to carry voice and video comfortably and low enough that a phone with both
    /// radios linked still prefers the one that has actually been counted.
    /// </para>
    /// </summary>
    public long MaxBandwidthBps => 50_000_000;

    public bool IsLinked => !_peers.IsEmpty;

    public string? PeerTag => _peers.Keys.FirstOrDefault();

    public event Action<string>? PeerLinked;
    public event Action<string, byte[]>? DataReceived;
    public event Action<string>? Status;

    private void L(string m)
    {
        global::Android.Util.Log.Info("AetherAware", m);
        _logger.LogInformation("{M}", m);
        Status?.Invoke(m);
    }

    // ── Bring-up ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Attach to the Aware cluster, then both publish and subscribe.
    ///
    /// <para>
    /// Both, on every phone, deliberately. Aware has no notion of a host: two phones that both only
    /// publish never see each other, and two that both only subscribe never do either. Doing both
    /// means whichever one comes up second finds the first, with nobody having to decide a role — the
    /// exact thing Wi-Fi Direct forces and Aware does not.
    /// </para>
    /// </summary>
    public void Link()
    {
        if (_disposed || !IsAvailable)
        {
            L(UnavailableReason ?? "Wi-Fi Aware is not usable here");
            return;
        }

        try
        {
            var manager = (WifiAwareManager?)Ctx.GetSystemService(Context.WifiAwareService);
            if (manager is null) { L("no Wi-Fi Aware service"); return; }

            _connectivity = (ConnectivityManager?)Ctx.GetSystemService(Context.ConnectivityService);
            manager.Attach(new Attached(this), new Handler(Looper.MainLooper!));
            L("attaching to the Aware cluster");
        }
        catch (Exception ex)
        {
            L("could not attach: " + ex.Message);
        }
    }

    private sealed class Attached(AndroidWifiAwareTransportService radio) : AttachCallback
    {
        public override void OnAttached(WifiAwareSession session)
        {
            radio._session = session;
            radio.L("attached");
            radio.PublishAndSubscribe(session);
        }

        public override void OnAttachFailed()
        {
            radio.L("the Aware cluster refused the attach");
        }
    }

    private void PublishAndSubscribe(WifiAwareSession session)
    {
        try
        {
            var handler = new Handler(Looper.MainLooper!);

            // The rotating address rides in the service info, so a peer knows who it found before any
            // connection exists. It is a wire address, not an identity — who someone is only ever
            // arrives later, inside the session.
            var info = System.Text.Encoding.UTF8.GetBytes(_address());

            session.Publish(
                new PublishConfig.Builder()
                    .SetServiceName(ServiceName)!
                    .SetServiceSpecificInfo(info)!
                    .Build()!,
                new Published(this),
                handler);

            session.Subscribe(
                new SubscribeConfig.Builder()
                    .SetServiceName(ServiceName)!
                    .Build()!,
                new Subscribed(this),
                handler);
        }
        catch (Exception ex)
        {
            L("could not publish or subscribe: " + ex.Message);
        }
    }

    private sealed class Published(AndroidWifiAwareTransportService radio) : DiscoverySessionCallback
    {
        public override void OnPublishStarted(PublishDiscoverySession session)
        {
            radio._publish = session;
            radio.L("publishing " + ServiceName);
        }

        /// <summary>
        /// A subscriber said hello. The publisher is the one that listens on the data path, so this is
        /// where the server side comes up — and it comes up per peer, because Aware links are
        /// many-to-many and each one is its own network.
        /// </summary>
        public override void OnMessageReceived(PeerHandle peerHandle, byte[]? message)
            => radio.OpenDataPath(radio._publish, peerHandle, listen: true);
    }

    private sealed class Subscribed(AndroidWifiAwareTransportService radio) : DiscoverySessionCallback
    {
        public override void OnSubscribeStarted(SubscribeDiscoverySession session)
        {
            radio._subscribe = session;
            radio.L("looking for " + ServiceName);
        }

        /// <summary>
        /// Found one. A message has to go out before a data path can be requested: Aware will not give
        /// the publisher a peer handle for us until it has heard from us, and without that handle it
        /// cannot accept the network we are about to ask for.
        /// </summary>
        public override void OnServiceDiscovered(
            PeerHandle peerHandle, byte[]? serviceSpecificInfo, IList<byte[]>? matchFilter)
        {
            var who = serviceSpecificInfo is { Length: > 0 }
                ? System.Text.Encoding.UTF8.GetString(serviceSpecificInfo)
                : "someone";

            radio.L("found " + who);

            try { radio._subscribe?.SendMessage(peerHandle, 1, System.Text.Encoding.UTF8.GetBytes(radio._address())); }
            catch (Exception ex) { radio.L("could not greet the peer: " + ex.Message); }

            radio.OpenDataPath(radio._subscribe, peerHandle, listen: false);
        }
    }

    // ── The data path ─────────────────────────────────────────────────────────

    /// <summary>
    /// Ask Android for an Aware network to this peer, and put a socket on it.
    ///
    /// <para>
    /// Discovery and data are separate in Aware, which is the part that surprises people: finding a
    /// peer gives you a handle and a 255-byte message channel, not a link. The bytes need a network,
    /// requested here, and it is its own private network per peer rather than anything the phone's
    /// ordinary Wi-Fi knows about.
    /// </para>
    /// </summary>
    private void OpenDataPath(DiscoverySession? session, PeerHandle peer, bool listen)
    {
        if (_disposed || session is null || _connectivity is null) return;

        try
        {
            var builder = new WifiAwareNetworkSpecifier.Builder(session, peer);

            // The publisher listens, so it names the port; the subscriber does not, and naming one
            // there makes Android reject the request outright.
            if (listen) builder.SetPort(TcpPort);

            var request = new NetworkRequest.Builder()
                .AddTransportType(global::Android.Net.TransportType.WifiAware)!
                .SetNetworkSpecifier(builder.Build()!)!
                .Build()!;

            _networkCallback = new NetworkUp(this, listen);
            _connectivity.RequestNetwork(request, _networkCallback);
            L(listen ? "waiting for a data path" : "asking for a data path");
        }
        catch (Exception ex)
        {
            L("could not set up the data path: " + ex.Message);
        }
    }

    private sealed class NetworkUp(AndroidWifiAwareTransportService radio, bool listen)
        : ConnectivityManager.NetworkCallback
    {
        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities capabilities)
        {
            // The address to connect to lives in the capabilities, not in the network — this callback
            // is the only place it is ever handed over, which is why the connect happens here rather
            // than in OnAvailable.
            if (listen) { _ = radio.ListenAsync(); return; }

            if (capabilities.TransportInfo is not WifiAwareNetworkInfo info) return;
            if (info.PeerIpv6Addr is not { } address) return;

            _ = radio.ConnectAsync(network, address, info.Port);
        }

        public override void OnLost(Network network) => radio.L("the data path went away");
    }

    private async Task ListenAsync()
    {
        if (_server is not null) return;

        try
        {
            _server = new TcpListener(IPAddress.IPv6Any, TcpPort);
            _server.Server.DualMode = true;
            _server.Start();
            L("listening on " + TcpPort);

            while (!_disposed)
            {
                var client = await _server.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleSocketAsync(client));
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            L("stopped listening: " + ex.Message);
        }
    }

    /// <summary>
    /// Connect to the peer over the Aware network.
    ///
    /// <para>
    /// Two things here are easy to get wrong and would each fail in a way that looks like a dead
    /// radio.
    /// </para>
    ///
    /// <para>
    /// First, the address is <b>link-local IPv6</b>, and a link-local address is meaningless without
    /// its scope — the same <c>fe80::</c> address exists on every interface. Android hands it over as
    /// an <c>Inet6Address</c> carrying the scope id, so the address is rebuilt from its raw bytes and
    /// that id rather than parsed from a string; the string form ends in something like
    /// <c>%aware_data0</c>, which .NET cannot parse at all.
    /// </para>
    ///
    /// <para>
    /// Second, the socket has to go out on the Aware network rather than whatever the phone considers
    /// default. <c>Network.BindSocket</c> takes a Java socket and cannot be handed a .NET one, so the
    /// process is bound for the length of the connect and put back afterwards. Process-wide is a
    /// blunter instrument than per-socket and the window is deliberately as small as possible: the
    /// connect only, never the conversation that follows.
    /// </para>
    /// </summary>
    private async Task ConnectAsync(Network network, Java.Net.InetAddress address, int port)
    {
        var raw = address.GetAddress();
        if (raw is null) return;

        var scope = address is Java.Net.Inet6Address v6 ? v6.ScopeId : 0;
        var peer = new IPAddress(raw, scope);

        var client = new TcpClient(AddressFamily.InterNetworkV6);
        var bound = false;

        try
        {
            bound = _connectivity?.BindProcessToNetwork(network) ?? false;
            if (!bound) L("could not bind to the Aware network — trying anyway");

            await client.ConnectAsync(peer, port).ConfigureAwait(false);
            L("connected to " + peer);
        }
        catch (Exception ex) when (!_disposed)
        {
            L("could not connect: " + ex.Message);
            client.Dispose();
            return;
        }
        finally
        {
            // Put the process back whatever happened. Leaving it bound would send every other
            // connection this app makes down a private Aware network, which is a far worse failure
            // than the one being handled.
            if (bound)
            {
                try { _connectivity?.BindProcessToNetwork(null); } catch { /* nothing to restore */ }
            }
        }

        await HandleSocketAsync(client).ConfigureAwait(false);
    }

    /// <summary>
    /// Same handshake and framing as the Wi-Fi Direct radio, on purpose.
    ///
    /// <para>
    /// Both announce a rotating wire address first and let identity arrive later inside the session.
    /// Keeping them identical means a link is a link whichever radio carried it, and the layers above
    /// never learn which one they are on.
    /// </para>
    /// </summary>
    private async Task HandleSocketAsync(TcpClient client)
    {
        var stream = client.GetStream();
        await WriteFrameAsync(stream, System.Text.Encoding.UTF8.GetBytes("ERID:" + _address())).ConfigureAwait(false);

        string? peer = null;
        try
        {
            while (!_disposed)
            {
                var frame = await ReadFrameAsync(stream).ConfigureAwait(false);
                if (frame is null) break;

                if (peer is null && frame.Length > 5 &&
                    System.Text.Encoding.UTF8.GetString(frame, 0, 5) == "ERID:")
                {
                    peer = System.Text.Encoding.UTF8.GetString(frame, 5, frame.Length - 5);
                    _peers[peer] = new PeerLink(client, stream);
                    L("linked with " + peer);
                    PeerLinked?.Invoke(peer);
                    continue;
                }

                if (peer is not null) DataReceived?.Invoke(peer, frame);
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            _logger.LogDebug(ex, "Wi-Fi Aware socket closed");
        }
        finally
        {
            if (peer is not null) _peers.TryRemove(peer, out _);
            client.Dispose();
        }
    }

    // ── Sending ───────────────────────────────────────────────────────────────

    public async Task<bool> SendAsync(byte[] data)
    {
        if (PeerTag is not { } peer || !_peers.TryGetValue(peer, out var link)) return false;

        try
        {
            await WriteFrameAsync(link.Stream, data).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wi-Fi Aware send failed");
            return false;
        }
    }

    // ── Framing: [4-byte little-endian length][payload] ───────────────────────

    private static async Task WriteFrameAsync(NetworkStream s, byte[] payload)
    {
        var header = new byte[4];
        BitConverter.TryWriteBytes(header, payload.Length);
        await s.WriteAsync(header).ConfigureAwait(false);
        await s.WriteAsync(payload).ConfigureAwait(false);
        await s.FlushAsync().ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadFrameAsync(NetworkStream s)
    {
        var header = new byte[4];
        if (!await FillAsync(s, header).ConfigureAwait(false)) return null;

        var length = BitConverter.ToInt32(header);

        // A length that could not be real is a corrupt or hostile stream, and allocating on it is how
        // one bad frame becomes an out-of-memory kill. Four megabytes is far above any packet this
        // mesh sends.
        if (length <= 0 || length > 4 * 1024 * 1024) return null;

        var payload = new byte[length];
        return await FillAsync(s, payload).ConfigureAwait(false) ? payload : null;
    }

    private static async Task<bool> FillAsync(NetworkStream s, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read)).ConfigureAwait(false);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    // ── Going away ────────────────────────────────────────────────────────────

    public void Stop() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var link in _peers.Values)
        {
            try { link.Client.Dispose(); } catch { /* already gone */ }
        }
        _peers.Clear();

        try { _server?.Stop(); } catch { /* already stopped */ }
        _server = null;

        if (_networkCallback is not null)
        {
            try { _connectivity?.UnregisterNetworkCallback(_networkCallback); } catch { /* never registered */ }
            _networkCallback = null;
        }

        try { _publish?.Close(); } catch { /* already closed */ }
        try { _subscribe?.Close(); } catch { /* already closed */ }
        try { _session?.Close(); } catch { /* already closed */ }

        _publish = null;
        _subscribe = null;
        _session = null;
    }
}
#endif
