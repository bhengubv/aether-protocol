// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Sample.Shared.Services;
using AetherNet.Transport.Abstractions;
using Android.Content;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AetherNet.Sample.Platforms.Android.Transports;

/// <summary>
/// The leg that was missing: two phones on the same network, talking directly.
///
/// <para>
/// Every other radio here builds its own link from nothing. Wi-Fi Direct forms a group — it elects a
/// group owner, hands out credentials, and takes the Wi-Fi chip away from whatever it was doing. That
/// is the right answer in a field with no infrastructure, and it is the wrong answer in a kitchen with
/// a router in it, where both phones are already associated with the same access point and could
/// simply open a socket. Until now they could not: the mesh had seven transports and not one of them
/// used a network the phones were already on, so two handsets a metre apart on the same Wi-Fi still
/// tore down their association to build a private one.
/// </para>
///
/// <para>
/// This costs nothing to establish. No group, no election, no credentials, nothing to negotiate, and
/// no disturbance to the connection the phone is already holding. It is what a LAN has always been:
/// you are reachable because you are on it, and a peer that already knows you just connects.
/// </para>
///
/// <h3>How it works</h3>
/// <list type="number">
///   <item><description>Each phone opens a TCP server on an OS-assigned port.</description></item>
///   <item><description>Each broadcasts a small UDP beacon carrying its rotating wire address and
///     that port, a few times a minute.</description></item>
///   <item><description>A beacon is only answered if <see cref="CircleDirectory"/> recognises the
///     address as somebody this phone has already added. Strangers are heard and ignored.</description></item>
///   <item><description>The higher address dials, so two phones never dial each other at once. From
///     there it is the same framing every other stream radio uses.</description></item>
/// </list>
///
/// <h3>What this deliberately does not do</h3>
/// <para>
/// It does not answer strangers. A LAN is a shared space — a café, an office, a block of flats — and
/// anything that connects to whoever is present is a cold connect by another name. Recognition is by
/// shared secret: a contact's routing key arrives inside an established session, so only somebody
/// already added can be resolved from a beacon, and revoking that is forgetting the key. An
/// unrecognised beacon is logged rather than silently dropped, because a leg that does nothing and
/// says nothing is indistinguishable from one that is broken.
/// </para>
///
/// <para>
/// It also does not turn Wi-Fi Direct off. Plenty of networks — guest Wi-Fi, hotel Wi-Fi, most public
/// hotspots — run client isolation, where two devices on the same access point cannot address each
/// other at all. On those this radio hears nothing and links nothing, and the group is what carries
/// the traffic. Preferring this leg is safe precisely because the other one is still there.
/// </para>
/// </summary>
internal sealed class AndroidLanTransportService : ITransportService, IRadio, IDisposable
{
    /// <summary>How often a phone says it is here.</summary>
    /// <remarks>
    /// Two seconds. This is a sixty-byte datagram, so the cost is not worth measuring, and the
    /// interval is what decides how long after walking into the house your phone is reachable. The
    /// standing call on this network is that reachability wins: a phone nobody can hear is a phone
    /// that cannot be told it has an incoming call.
    /// </remarks>
    private static readonly TimeSpan AnnounceEvery = TimeSpan.FromSeconds(2);

    /// <summary>The soonest a beacon may be sent again, when one is prompted by hearing somebody.</summary>
    /// <remarks>
    /// Answering a beacon with a beacon is what makes two phones find each other in well under a
    /// second instead of waiting out the interval. Without a floor it is also how two phones talk each
    /// other into a broadcast storm, each one's reply prompting the other's.
    /// </remarks>
    private static readonly TimeSpan AnnounceFloor = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to wait on a dial before deciding nothing is listening.</summary>
    /// <remarks>
    /// Short. On a LAN a peer that is there answers in milliseconds; anything slower is a firewall,
    /// client isolation, or an address that has moved on, and none of those get better with patience.
    /// </remarks>
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(3);

    private readonly Context _context;
    private readonly byte[] _routingKey;
    private readonly ILogger _logger;
    private readonly CircleDirectory? _circle;

    /// <summary>Live links, keyed by the peer's wire address as learned from the handshake.</summary>
    private readonly ConcurrentDictionary<string, MeshLink> _peers = new(StringComparer.Ordinal);

    /// <summary>
    /// Where a contact was last reachable — the "ping, and access on 200" path.
    /// </summary>
    /// <remarks>
    /// Keyed by AetherTag rather than wire address on purpose: the address rotates every fifteen
    /// minutes and the person does not. It exists so a link that drops can be rebuilt at once instead
    /// of waiting for the next beacon, which matters on a network that drops a packet and recovers.
    /// </remarks>
    private readonly ConcurrentDictionary<string, IPEndPoint> _lastSeen = new(StringComparer.Ordinal);

    /// <summary>Addresses currently being dialled, so a burst of beacons opens one socket.</summary>
    private readonly ConcurrentDictionary<string, byte> _dialling = new(StringComparer.Ordinal);

    /// <summary>Beacons already reported as unrecognised, so the log says it once and not every two seconds.</summary>
    private readonly ConcurrentDictionary<string, byte> _strangers = new(StringComparer.Ordinal);

    /// <summary>Guards the announce floor only. Nothing else is serialised on it.</summary>
    private readonly object _announceGate = new();

    private global::Android.Net.Wifi.WifiManager.MulticastLock? _multicast;
    private TcpListener? _server;
    private UdpClient? _beacon;
    private CancellationTokenSource? _life;
    private int _tcpPort;
    private int _started;
    private DateTimeOffset _lastAnnounce = DateTimeOffset.MinValue;
    private volatile bool _disposed;

    public AndroidLanTransportService(Context context, ILogger logger, byte[] routingKey,
        CircleDirectory? circle = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Must come from the identity secret. The tag is public, so an address derived from it could
        // be computed by anyone holding it — private in appearance only.
        _routingKey = routingKey ?? throw new ArgumentNullException(nameof(routingKey),
            "A rotating wire address needs a key derived from the identity secret, not the public tag.");
        _circle = circle;
    }

    // ── ITransportService metadata ───────────────────────────────────────────

    public string Name => "LAN";

    /// <summary>
    /// Available means there is a network this phone shares with other devices — Wi-Fi or Ethernet,
    /// and never cellular.
    /// </summary>
    /// <remarks>
    /// Mobile data is not a LAN. Every handset on it sits behind carrier-grade NAT with no path to
    /// any other, broadcasts go nowhere, and a radio that offered to link over it would be promising
    /// something the network cannot do.
    /// </remarks>
    public bool IsAvailable => !_disposed && OnSharedNetwork && LocalAddress is not null;

    /// <inheritdoc />
    public string? UnavailableReason =>
        _disposed ? "stopped"
        : !OnSharedNetwork ? "not on a Wi-Fi network"
        : LocalAddress is null ? "no address on this network yet"
        : null;

    /// <inheritdoc />
    /// <remarks>Joining a Wi-Fi network is something the person holding the phone can do.</remarks>
    public bool IsFixable => true;

    /// <summary>
    /// What this leg can carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not measured, and ranked below Wi-Fi Direct on purpose.</b> Physically these are the same
    /// chip and the same air, so a figure claiming otherwise would be invention — and every advertised
    /// bandwidth in this app has been wrong at least once. What is different is not throughput: a
    /// group has to be formed, and this does not. That advantage belongs in which radio is preferred,
    /// which is a separate decision from which is widest, and stating it as bandwidth would be lying
    /// about the reason.
    /// </para>
    /// <para>
    /// The <see cref="Quality"/> meter measures what actually crosses, and the mesh trusts it over
    /// this number as soon as it has enough traffic to be worth trusting.
    /// </para>
    /// </remarks>
    public long MaxBandwidthBps => 100_000_000;

    public int MaxRangeMeters => 0;                // as far as the network reaches
    public int PowerCostRelative => 3;             // the radio is already up for the phone's own Wi-Fi
    public int MaxConcurrentPeers => 32;

    /// <inheritdoc />
    public LinkQuality Quality { get; } = new();

    public event Action<string, byte[]>? DataReceived;
    public event Action<string>? PeerLinked;
    public event Action<string>? Status;

    private void L(string m)
    {
        global::Android.Util.Log.Info("AetherLAN", m);
        _logger.LogInformation("{Msg}", m);
        Status?.Invoke(m);
    }

    /// <summary>
    /// This phone's address for the current epoch. A rotating id, not an identity — unlinkable across
    /// epochs to anyone without the routing key.
    /// </summary>
    /// <remarks>
    /// It goes out in clear in the beacon, and that is what it is for. The whole point of an ephemeral
    /// routing id is that it is safe to say out loud: someone watching the network sees an opaque
    /// sixteen characters that change every fifteen minutes with no linkage between windows, while a
    /// contact holding the routing key resolves it to a person. Putting the AetherTag here instead
    /// would hand every device on the café Wi-Fi a permanent name for this phone.
    /// </remarks>
    private string MyAddress() => WireAddress.For(_routingKey);

    // ── Network facts ────────────────────────────────────────────────────────

    private bool OnSharedNetwork
    {
        get
        {
            try
            {
                if (_context.GetSystemService(Context.ConnectivityService)
                    is not global::Android.Net.ConnectivityManager cm) return false;

                var caps = cm.GetNetworkCapabilities(cm.ActiveNetwork);
                return caps is not null &&
                       (caps.HasTransport(global::Android.Net.TransportType.Wifi) ||
                        caps.HasTransport(global::Android.Net.TransportType.Ethernet));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read connectivity");
                return false;
            }
        }
    }

    /// <summary>This phone's IPv4 address on the shared network, or null when it has none.</summary>
    private static IPAddress? LocalAddress
    {
        get
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(ua.Address))
                            return ua.Address;
                }
            }
            catch { }
            return null;
        }
    }

    /// <summary>
    /// Every address a beacon should be sent to.
    /// </summary>
    /// <remarks>
    /// Both the limited broadcast (255.255.255.255) and the subnet's own — because which of the two
    /// survives is a property of the access point rather than of the phone. Some routers drop limited
    /// broadcast; some Android builds refuse to route the subnet one until an interface is fully up.
    /// Sending both is two datagrams and removes a whole class of "it works on that phone" from the
    /// problem.
    /// </remarks>
    private static IReadOnlyList<IPEndPoint> BroadcastTargets()
    {
        var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, LanBeacon.Port) };

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.IPv4Mask is not { } mask) continue;

                    var ip = ua.Address.GetAddressBytes();
                    var mb = mask.GetAddressBytes();
                    if (ip.Length != 4 || mb.Length != 4) continue;

                    var bc = new byte[4];
                    for (var i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | (byte)~mb[i]);
                    targets.Add(new IPEndPoint(new IPAddress(bc), LanBeacon.Port));
                }
            }
        }
        catch { }

        return targets;
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Open the server, start beaconing, start listening. Idempotent — a second call does nothing.
    /// </summary>
    public void Link()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        if (!OnSharedNetwork)
        {
            L("not on a Wi-Fi network — nothing to reach anyone over");
            Interlocked.Exchange(ref _started, 0);
            return;
        }

        _life = new CancellationTokenSource();

        // Android drops broadcast and multicast traffic while the Wi-Fi chip is power-saving, unless
        // something is holding this lock. Without it the beacons go out perfectly and none ever
        // arrive — which reads exactly like a network that blocks them.
        try
        {
            if (_context.GetSystemService(Context.WifiService) is global::Android.Net.Wifi.WifiManager wifi &&
                wifi.CreateMulticastLock("aethernet-lan") is { } held)
            {
                held.SetReferenceCounted(false);
                held.Acquire();
                _multicast = held;
            }
        }
        catch (Exception ex) { L($"could not hold the multicast lock: {ex.Message}"); }

        try
        {
            // Port 0: the OS picks. A hardcoded port is a port that is already in use on somebody's
            // phone, and the beacon carries whichever one we got, so nothing needs to agree in advance.
            _server = new TcpListener(IPAddress.Any, 0);
            _server.Start();
            _tcpPort = ((IPEndPoint)_server.LocalEndpoint).Port;
        }
        catch (Exception ex)
        {
            L($"could not open a server on this network: {ex.Message}");
            Interlocked.Exchange(ref _started, 0);
            return;
        }

        try
        {
            // ReuseAddress so a restart does not have to wait out the previous socket, and so this
            // works if anything else on the phone has the port open.
            _beacon = new UdpClient();
            _beacon.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _beacon.Client.Bind(new IPEndPoint(IPAddress.Any, LanBeacon.Port));
            _beacon.EnableBroadcast = true;
        }
        catch (Exception ex)
        {
            L($"could not open the beacon socket: {ex.Message}");
        }

        L($"on {LocalAddress} — listening on {_tcpPort}, beacon on {LanBeacon.Port}");

        _ = Task.Run(() => AcceptAsync(_life.Token), CancellationToken.None);
        _ = Task.Run(() => ListenForBeaconsAsync(_life.Token), CancellationToken.None);
        _ = Task.Run(() => AnnounceAsync(_life.Token), CancellationToken.None);
    }

    // ── Beaconing ────────────────────────────────────────────────────────────

    private async Task AnnounceAsync(CancellationToken life)
    {
        while (!life.IsCancellationRequested && !_disposed)
        {
            Announce();
            try { await Task.Delay(AnnounceEvery, life).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Announce()
    {
        var beacon = _beacon;
        if (beacon is null || _tcpPort == 0) return;
        if (!OnSharedNetwork) return;

        lock (_announceGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastAnnounce < AnnounceFloor) return;
            _lastAnnounce = now;
        }

        var datagram = Encoding.ASCII.GetBytes(LanBeacon.Compose(MyAddress(), _tcpPort));

        foreach (var target in BroadcastTargets())
        {
            try { beacon.Send(datagram, datagram.Length, target); }
            catch (Exception ex) { _logger.LogDebug(ex, "beacon to {Target} failed", target); }
        }
    }

    private async Task ListenForBeaconsAsync(CancellationToken life)
    {
        var beacon = _beacon;
        if (beacon is null) return;

        while (!life.IsCancellationRequested && !_disposed)
        {
            UdpReceiveResult got;
            try { got = await beacon.ReceiveAsync(life).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex) { _logger.LogDebug(ex, "beacon receive failed"); continue; }

            try { Heard(got); }
            catch (Exception ex) { _logger.LogDebug(ex, "could not act on a beacon"); }
        }
    }

    private void Heard(UdpReceiveResult got)
    {
        if (got.Buffer.Length > LanBeacon.MaxLength) return;
        if (!LanBeacon.TryParse(Encoding.ASCII.GetString(got.Buffer), out var theirAddress, out var port)) return;

        // Our own broadcast comes straight back to us. Recognising ourselves is not a bug to guard
        // against once — it happens twice every two seconds, forever.
        if (WireAddress.IsMine(theirAddress, _routingKey)) return;

        if (_peers.ContainsKey(theirAddress)) return;

        // Who is that? A rotating address means nothing on its own; only a contact who has shared a
        // routing key inside a session can be resolved from one.
        var who = _circle?.Recognise(theirAddress);
        if (who is null)
        {
            // Said once per address, not every two seconds. A stranger on the café Wi-Fi should be
            // visible in the log — otherwise a leg that is working and a leg that is broken look
            // identical — but it should not drown it.
            if (_strangers.TryAdd(theirAddress, 0))
            {
                if (_strangers.Count > 64) _strangers.Clear();
                L($"heard {theirAddress} on the network — not anyone this phone has added");
            }
            return;
        }

        var peer = new IPEndPoint(got.RemoteEndPoint.Address, port);
        _lastSeen[who] = peer;

        // Answer at once, so the other phone finds us in well under a second rather than waiting out
        // its own interval. The floor inside Announce() keeps this from becoming a storm.
        Announce();

        // Both phones can see each other, so both would dial. The higher address does it.
        if (!LanBeacon.ShouldDial(MyAddress(), theirAddress)) return;

        _ = DialAsync(theirAddress, who, peer);
    }

    // ── Dialling and accepting ───────────────────────────────────────────────

    private async Task DialAsync(string theirAddress, string who, IPEndPoint peer)
    {
        if (!_dialling.TryAdd(theirAddress, 0)) return;

        try
        {
            if (_peers.ContainsKey(theirAddress)) return;
            if (AlreadyLinkedTo(peer.Address)) return;

            var client = new TcpClient();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_life?.Token ?? default);
                timeout.CancelAfter(DialTimeout);
                await client.ConnectAsync(peer.Address, peer.Port, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                client.Dispose();
                // Worth saying plainly. On a network with client isolation — most guest and hotel
                // Wi-Fi — the beacon arrives and the dial never completes, and that pair of facts is
                // the only way to tell isolation apart from an empty network.
                L($"heard {who} at {peer} but could not reach them: {ex.Message}");
                return;
            }

            L($"dialling {who} at {peer}");
            await HandleSocketAsync(client).ConfigureAwait(false);
        }
        finally { _dialling.TryRemove(theirAddress, out _); }
    }

    private async Task AcceptAsync(CancellationToken life)
    {
        var server = _server;
        if (server is null) return;

        while (!life.IsCancellationRequested && !_disposed)
        {
            TcpClient client;
            try { client = await server.AcceptTcpClientAsync(life).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex) { _logger.LogDebug(ex, "accept failed"); continue; }

            _ = Task.Run(() => HandleSocketAsync(client), CancellationToken.None);
        }
    }

    /// <summary>Is a live socket to this address already open?</summary>
    /// <remarks>
    /// One device is one address here, so a second link to the same one is a duplicate however it
    /// arose — and it can arise: the dial rule is decided from an address that rotates, so on the
    /// boundary of an epoch both phones can briefly believe they are the higher one.
    /// </remarks>
    private bool AlreadyLinkedTo(IPAddress address)
    {
        foreach (var link in _peers.Values)
        {
            try
            {
                if (link.Client.Connected &&
                    link.Client.Client.RemoteEndPoint is IPEndPoint ep &&
                    ep.Address.Equals(address))
                    return true;
            }
            catch { }
        }
        return false;
    }

    private async Task HandleSocketAsync(TcpClient client)
    {
        if (!Framing.Tighten(client))
            L("could not tune the socket — video may run late before the link reports strain");

        var stream = client.GetStream();
        string? theirAddress = null;

        // The key this socket actually owns in _peers, which is NOT the same as knowing the peer's
        // address. Two sockets to the same phone can exist for a moment — the dial rule is decided
        // from an address that rotates, so on an epoch boundary both sides can briefly believe they
        // are the higher one — and the loser must tear down its own socket without evicting the
        // winner's entry on the way out.
        string? owned = null;

        try
        {
            // Announce our rotating address. Who we are arrives later, inside the session.
            await Framing.WriteFrameAsync(stream,
                Encoding.UTF8.GetBytes("ERID:" + MyAddress())).ConfigureAwait(false);

            while (!_disposed)
            {
                var frame = await Framing.ReadFrameAsync(stream).ConfigureAwait(false);
                if (frame is null) break;

                if (theirAddress is null)
                {
                    if (frame.Length <= 5 || Encoding.UTF8.GetString(frame, 0, 5) != "ERID:") continue;
                    theirAddress = Encoding.UTF8.GetString(frame, 5, frame.Length - 5);

                    // A socket from somebody this phone has not added is dropped here rather than at
                    // the accept, because until the handshake there is nothing to judge — a TCP
                    // connection carries no identity of any kind.
                    var who = _circle?.Recognise(theirAddress);
                    if (who is null && _circle is not null)
                    {
                        L($"{theirAddress} connected but is not anyone this phone has added — dropped");
                        break;
                    }

                    if (client.Client.RemoteEndPoint is IPEndPoint ep &&
                        AlreadyLinkedTo(ep.Address))
                    {
                        // Both sides dialled across an epoch boundary. Keep the one already running.
                        break;
                    }

                    var link = new MeshLink(client, stream);

                    // TryAdd, not an assignment. An assignment lets a second socket to the same peer
                    // silently replace the first, and the replaced one's pump keeps running against a
                    // dictionary entry that says it is still linked — so frames go out on a socket
                    // nothing is reading. Losing this race means closing our own socket, not stealing.
                    if (!_peers.TryAdd(theirAddress, link)) break;

                    owned = theirAddress;
                    _strangers.TryRemove(theirAddress, out _);

                    if (who is not null && client.Client.RemoteEndPoint is IPEndPoint seen)
                        _lastSeen[who] = seen;

                    var carrying = theirAddress;
                    _ = Task.Run(() => PumpAsync(carrying, link), CancellationToken.None);

                    L($"linked with {who ?? theirAddress} over the LAN");
                    PeerLinked?.Invoke(theirAddress);
                    continue;
                }

                // Logged because a packet that leaves one phone and never appears on the other is
                // otherwise indistinguishable from one that was never sent — and both ends reported
                // success while a message quietly went nowhere.
                L($"◀ {frame.Length}B from {theirAddress}");
                DataReceived?.Invoke(theirAddress, frame);
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            L($"link to {theirAddress ?? "a peer"} closed: {ex.Message}");
        }
        finally
        {
            if (owned is not null) _peers.TryRemove(owned, out _);
            try { client.Dispose(); } catch { }

            // Come straight back rather than waiting out the beacon interval. This is the whole point
            // of remembering where somebody was: on a LAN a peer that dropped a moment ago is almost
            // always still at the same address.
            //
            // Only for a link this socket owned. A socket that lost the race never carried anything
            // and the winner is still up — redialling from here would be rebuilding a link that was
            // never down.
            if (owned is not null && !_disposed) _ = RedialAsync(owned);
        }
    }

    /// <summary>Try the address a contact was last reachable at, once, immediately.</summary>
    private async Task RedialAsync(string theirAddress)
    {
        var who = _circle?.Recognise(theirAddress);
        if (who is null || !_lastSeen.TryGetValue(who, out var peer)) return;

        // A moment, so a link torn down by the far side is fully gone before it is rebuilt.
        try { await Task.Delay(TimeSpan.FromMilliseconds(400), _life?.Token ?? default).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        if (_disposed || _peers.ContainsKey(theirAddress)) return;
        await DialAsync(theirAddress, who, peer).ConfigureAwait(false);
    }

    // ── Sending ──────────────────────────────────────────────────────────────

    public Task<bool> SendAsync(string peerUhid, byte[] data, CancellationToken cancellationToken = default) =>
        SendAsync(peerUhid, data, SendLane.Interactive, cancellationToken);

    public Task<bool> SendAsync(string peerUhid, byte[] data, SendLane lane,
        CancellationToken cancellationToken = default)
    {
        if (!_peers.TryGetValue(peerUhid, out var link))
        {
            L($"▶ nowhere to send {data.Length}B — {peerUhid} is not one of the {_peers.Count} linked");
            Quality.Record(data.Length, TimeSpan.Zero, sent: false);
            return Task.FromResult(false);
        }

        var dropped = link.Enqueue(data, lane);
        if (dropped > 0 && lane == SendLane.Video)
            L($"▶ video backed up — dropped {dropped} frames to the next keyframe");

        return Task.FromResult(true);
    }

    private async Task PumpAsync(string peerUhid, MeshLink link)
    {
        while (!_disposed && _peers.ContainsKey(peerUhid))
        {
            try { await link.Ready.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (ObjectDisposedException) { return; }

            if (link.NextFrame() is not { } frame) continue;

            // Timed across the whole write, because how long the wire takes to accept a frame is the
            // congestion signal everything else sizes itself from.
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await Framing.WriteFrameAsync(link.Stream, frame).ConfigureAwait(false);
                Quality.Record(frame.Length, System.Diagnostics.Stopwatch.GetElapsedTime(started), sent: true);
            }
            catch (Exception ex)
            {
                L($"▶ send to {peerUhid} failed: {ex.Message}");
                Quality.Record(frame.Length, System.Diagnostics.Stopwatch.GetElapsedTime(started), sent: false);
                return;   // the socket is gone; the reader's finally rebuilds it
            }
        }
    }

    public async Task<bool> SendStreamAsync(string peerUhid, Stream stream,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return await SendAsync(peerUhid, ms.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public bool IsConnected(string peerUhid) => _peers.ContainsKey(peerUhid);

    /// <summary>Wire addresses of peers currently linked over this radio.</summary>
    public IReadOnlyCollection<string> ConnectedPeers => _peers.Keys.ToArray();

    // ── IRadio ───────────────────────────────────────────────────────────────

    public bool IsLinked => !_peers.IsEmpty;
    public string? PeerTag => _peers.Keys.FirstOrDefault();

    public Task<bool> SendAsync(byte[] data) => SendAsync(data, SendLane.Interactive);

    public Task<bool> SendAsync(byte[] data, SendLane lane)
        => PeerTag is { } p ? SendAsync(p, data, lane) : Task.FromResult(false);

    public void Stop() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _life?.Cancel(); } catch { }
        try { _server?.Stop(); } catch { }
        try { _beacon?.Dispose(); } catch { }

        foreach (var link in _peers.Values)
        {
            try { link.Client.Dispose(); } catch { }
        }
        _peers.Clear();

        // Released explicitly. Held past the radio it exists for, this keeps the Wi-Fi chip out of its
        // power-saving mode for as long as the app is installed and running.
        try { if (_multicast is { } m && m.IsHeld) m.Release(); } catch { }
        _multicast = null;

        _life?.Dispose();
        _life = null;
        _server = null;
        _beacon = null;
    }
}
#endif
