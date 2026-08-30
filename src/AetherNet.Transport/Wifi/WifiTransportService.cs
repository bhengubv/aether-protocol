// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AetherNet.Transport.Abstractions;

namespace AetherNet.Transport.Wifi;

/// <summary>
/// Two phones already on the same Wi-Fi, talking straight to each other across it.
///
/// <para>
/// <b>Why this exists.</b> Wi-Fi Direct builds a network from nothing, which is the right answer in a
/// field and a slow, fragile answer in a kitchen where both phones are already three metres from the
/// same access point. Refusing to use a network that is already up, already fast and already carrying
/// both handsets is not principle, it is waste — and it left two phones in the same room unable to
/// reach each other for an afternoon.
/// </para>
///
/// <para>
/// <b>What it costs.</b> The router sees that two devices on it are exchanging bytes, how many and
/// when. It never sees what: everything crossing this is sealed above it, exactly as it is over every
/// other radio. So the difference against Wi-Fi Direct is metadata and a dependency on somebody
/// else's box, not secrecy — worth having as one option among several rather than as the only way
/// out, which is why it sits beside the others rather than replacing them.
/// </para>
///
/// <para>
/// <b>How the two find each other.</b> Not by scanning, and not by asking anything on the network.
/// Both phones already know where to meet — see <c>Meeting</c> in the sample — so the rendezvous
/// becomes a multicast group, a port and a token, and the only devices that can compute any of it are
/// the two that were handed each other's tags. Everything else on the LAN hears an opaque datagram
/// addressed to nobody it knows and drops it.
/// </para>
///
/// <para>
/// One of the pair opens a socket and waits; the other dials it. Which one is decided before either
/// touches the network, so there is no race and no negotiation.
/// </para>
/// </summary>
public sealed class WifiTransportService : ITransportService, IDisposable
{
    /// <summary>The multicast group announcements go to.</summary>
    /// <remarks>
    /// Administratively scoped — 239.0.0.0/8 is the block reserved for private use, so this never
    /// leaves the local network even where multicast routing is switched on.
    /// </remarks>
    private const string Group = "239.7.7.7";

    /// <summary>How often the waiting side says where it is.</summary>
    /// <remarks>
    /// A phone that joins the network later has to be able to find one that was already there, so this
    /// repeats rather than announcing once. Two seconds is cheap — one small datagram — and it is also
    /// how long somebody waits after walking into a room.
    /// </remarks>
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(2);

    /// <summary>The largest frame this will read from a peer.</summary>
    /// <remarks>
    /// A length prefix arriving from the network is a claim, not a fact. Without a ceiling, one peer
    /// saying "two gigabytes follow" is an allocation this process makes on their say-so.
    /// </remarks>
    private const int LargestFrame = 4 * 1024 * 1024;

    private readonly string _localUhid;
    private readonly ConcurrentDictionary<string, TcpClient> _peers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TcpListener? _listener;
    private UdpClient? _announcer;
    private bool _disposed;

    /// <summary>The meeting this is already keeping, so being asked again costs nothing.</summary>
    /// <remarks>
    /// <see cref="MeetAsync"/> is called on every pass of the radio bring-up — which repeats, on
    /// purpose, because there is no moment either phone can point to and say "the other one is ready
    /// now". Without this it opened a fresh socket and dialled again every time, so a link that was
    /// perfectly healthy re-handshook on a loop: harmless to look at, and a stream of connects on
    /// somebody's network for no reason.
    /// </remarks>
    private string? _keeping;

    /// <param name="localUhid">This node's wire address, sent so the far side knows who arrived.</param>
    public WifiTransportService(string localUhid) =>
        _localUhid = localUhid ?? throw new ArgumentNullException(nameof(localUhid));

    /// <inheritdoc />
    public string Name => "Wi-Fi";

    /// <inheritdoc />
    /// <remarks>
    /// True when this device is on a network with an address other than loopback. It deliberately does
    /// not ask whether there is internet: this carries traffic between two phones on the same access
    /// point, and a router with its uplink unplugged does that perfectly well.
    /// </remarks>
    public bool IsAvailable => !_disposed && LocalAddress() is not null;

    /// <inheritdoc />
    /// <remarks>
    /// Deliberately below Wi-Fi Direct's. Both are fast enough for anything this app carries, and when
    /// two phones have both, the one with no third party in it should win.
    /// </remarks>
    public long MaxBandwidthBps => 100_000_000;

    /// <inheritdoc />
    public int MaxRangeMeters => 50;

    /// <inheritdoc />
    /// <remarks>Cheapest radio on the phone: the Wi-Fi is already associated and already awake.</remarks>
    public int PowerCostRelative => 1;

    /// <inheritdoc />
    public int MaxConcurrentPeers => 16;

    /// <inheritdoc />
    public event Action<string, byte[]>? DataReceived;

    /// <summary>Running commentary, for the radio log.</summary>
    public event Action<string>? Status;

    /// <summary>Raised with a peer's wire address once a connection is up.</summary>
    public event Action<string>? PeerLinked;

    /// <inheritdoc />
    public bool IsConnected(string peerUhid) =>
        _peers.TryGetValue(peerUhid, out var client) && client.Connected;

    /// <summary>
    /// Meet somebody at a place you both worked out.
    /// </summary>
    /// <param name="rendezvous">
    ///   What both phones derived from their two tags. Only they can compute it, so only they can find
    ///   each other here — nothing is broadcast that anyone else could answer.
    /// </param>
    /// <param name="iStart">
    ///   Whether this phone waits and the other dials, or the other way round. Decided before either
    ///   touches the network, so there is nothing to race over.
    /// </param>
    public async Task MeetAsync(string rendezvous, bool iStart, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(rendezvous)) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Already keeping this one. Asked again is the ordinary case, not an event.
            if (string.Equals(_keeping, rendezvous, StringComparison.Ordinal)) return;

            Say($"asked to meet at {rendezvous[..6]}… — {(iStart ? "waiting" : "looking")}");
            if (LocalAddress() is not { } me) { Say("no network on this phone"); return; }

            var port = PortFor(rendezvous);

            if (iStart) Wait(me, rendezvous, port);
            else Listen(rendezvous, port);

            _keeping = rendezvous;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ─── The side that waits ─────────────────────────────────────────────────────

    /// <summary>Open a socket, then say where it is until somebody turns up.</summary>
    private void Wait(IPAddress me, string rendezvous, int port)
    {
        if (_listener is not null) return;

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Say($"waiting on {me}:{port}");

        _ = Task.Run(() => AcceptAsync(_listener, _stopping.Token));
        _ = Task.Run(() => AnnounceAsync(me, rendezvous, port, _stopping.Token));
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(stopping).ConfigureAwait(false);
                _ = Task.Run(() => ServeAsync(client, stopping), stopping);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Say($"stopped accepting: {ex.Message}"); return; }
        }
    }

    /// <summary>
    /// Say where the socket is, over and over.
    /// </summary>
    /// <remarks>
    /// The datagram carries the rendezvous and nothing else identifying. A device that cannot compute
    /// that value has no idea what it just heard or who it was for, which is the point: nothing here
    /// is discoverable by anybody who was not handed both tags.
    /// </remarks>
    private async Task AnnounceAsync(IPAddress me, string rendezvous, int port, CancellationToken stopping)
    {
        try
        {
            _announcer = new UdpClient(AddressFamily.InterNetwork);
            _announcer.JoinMulticastGroup(IPAddress.Parse(Group), me);

            var to = new IPEndPoint(IPAddress.Parse(Group), PortFor(rendezvous + "-say"));
            var said = Encoding.UTF8.GetBytes($"AETHERWIFI1 {rendezvous} {me} {port}");

            while (!stopping.IsCancellationRequested)
            {
                await _announcer.SendAsync(said, said.Length, to).ConfigureAwait(false);
                await Task.Delay(Beat, stopping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Say($"could not announce: {ex.Message}"); }
    }

    // ─── The side that dials ─────────────────────────────────────────────────────

    /// <summary>Listen for the other phone saying where it is, then go there.</summary>
    private void Listen(string rendezvous, int port) =>
        _ = Task.Run(() => HearAsync(rendezvous, port, _stopping.Token));

    private async Task HearAsync(string rendezvous, int port, CancellationToken stopping)
    {
        UdpClient? ears = null;
        try
        {
            ears = new UdpClient(AddressFamily.InterNetwork) { ExclusiveAddressUse = false };
            ears.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            ears.Client.Bind(new IPEndPoint(IPAddress.Any, PortFor(rendezvous + "-say")));
            ears.JoinMulticastGroup(IPAddress.Parse(Group));

            Say("listening for them on the network");

            while (!stopping.IsCancellationRequested)
            {
                var heard = await ears.ReceiveAsync(stopping).ConfigureAwait(false);
                var words = Encoding.UTF8.GetString(heard.Buffer).Split(' ');

                // Ours, and about this meeting. Anything else on the group is somebody else's business.
                if (words.Length != 4 || words[0] != "AETHERWIFI1") continue;
                if (!string.Equals(words[1], rendezvous, StringComparison.Ordinal)) continue;
                if (!IPAddress.TryParse(words[2], out var them)) continue;
                if (!int.TryParse(words[3], out var theirPort)) continue;

                if (await DialAsync(them, theirPort, stopping).ConfigureAwait(false)) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Say($"could not listen: {ex.Message}"); }
        finally { ears?.Dispose(); }
    }

    private async Task<bool> DialAsync(IPAddress them, int port, CancellationToken stopping)
    {
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(them, port, stopping).ConfigureAwait(false);

            Say($"connected to {them}:{port}");
            _ = Task.Run(() => ServeAsync(client, stopping), stopping);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            Say($"could not reach {them}: {ex.Message}");
            return false;
        }
    }

    // ─── Once there is a socket ──────────────────────────────────────────────────

    /// <summary>
    /// Say who we are, learn who they are, then carry frames until the socket closes.
    /// </summary>
    /// <remarks>
    /// The address exchanged here is a claim about identity and is treated as one — it names the
    /// sender for the layer above, which checks signatures against a key it already holds. Nothing
    /// here grants anybody anything.
    /// </remarks>
    private async Task ServeAsync(TcpClient client, CancellationToken stopping)
    {
        string? peer = null;
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                await WriteAsync(stream, Encoding.UTF8.GetBytes(_localUhid), stopping).ConfigureAwait(false);

                if (await ReadAsync(stream, stopping).ConfigureAwait(false) is not { } hello) return;

                peer = Encoding.UTF8.GetString(hello);
                if (peer.Length == 0) return;

                _peers[peer] = client;
                Say($"linked with {peer}");
                PeerLinked?.Invoke(peer);

                while (!stopping.IsCancellationRequested)
                {
                    if (await ReadAsync(stream, stopping).ConfigureAwait(false) is not { } frame) return;
                    DataReceived?.Invoke(peer, frame);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Say($"link ended: {ex.Message}"); }
        finally
        {
            if (peer is not null) _peers.TryRemove(peer, out _);

            // The far side went away. Whatever this was keeping is over, so the next time the radio
            // comes round it sets it up again rather than believing it is still there.
            if (_peers.IsEmpty) _keeping = null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendAsync(
        string peerUhid, byte[] data, CancellationToken cancellationToken = default)
    {
        if (!_peers.TryGetValue(peerUhid, out var client) || !client.Connected) return false;

        try
        {
            await WriteAsync(client.GetStream(), data, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            // The socket has gone. Saying so beats reporting success for bytes nobody will read — the
            // layer above uses this to decide whether anything is ringing at the far end.
            _peers.TryRemove(peerUhid, out _);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SendStreamAsync(
        string peerUhid, Stream stream, CancellationToken cancellationToken = default)
    {
        using var held = new MemoryStream();
        await stream.CopyToAsync(held, cancellationToken).ConfigureAwait(false);

        return await SendAsync(peerUhid, held.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    // ─── Frames ──────────────────────────────────────────────────────────────────

    private static async Task WriteAsync(Stream stream, byte[] data, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);

        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One frame, or null when the socket closed or the peer described one we will not take.</summary>
    private static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        if (!await FillAsync(stream, length, cancellationToken).ConfigureAwait(false)) return null;

        var size = BinaryPrimitives.ReadInt32BigEndian(length);
        if (size is <= 0 or > LargestFrame) return null;

        var data = new byte[size];
        return await FillAsync(stream, data, cancellationToken).ConfigureAwait(false) ? data : null;
    }

    private static async Task<bool> FillAsync(
        Stream stream, byte[] into, CancellationToken cancellationToken)
    {
        var at = 0;
        while (at < into.Length)
        {
            var read = await stream
                .ReadAsync(into.AsMemory(at), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0) return false;
            at += read;
        }

        return true;
    }

    // ─── Where on the network ────────────────────────────────────────────────────

    /// <summary>
    /// A port both phones work out from the rendezvous, so neither has to be told one.
    /// </summary>
    /// <remarks>
    /// Kept high, above everything a phone is likely to be running, and stable for a given pair so a
    /// reconnection lands in the same place.
    /// </remarks>
    private static int PortFor(string rendezvous)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(rendezvous));
        return 40000 + (BinaryPrimitives.ReadUInt16BigEndian(hash) % 20000);
    }

    /// <summary>
    /// This phone's address on the network it is actually on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <b>access point's</b> network, not the peer-to-peer one. A phone in a Wi-Fi Direct group
    /// has two interfaces up at once, and taking whichever came first found <c>p2p0</c> — so this
    /// transport announced itself at 192.168.49.1, inside the very group it exists to be an
    /// alternative to. It still linked, which is what made it easy to miss: the whole point is to use
    /// the connection that is already there, and it was quietly riding the other radio instead.
    /// </para>
    /// <para>
    /// Android names those interfaces <c>p2p0</c> / <c>p2p-wlan0-…</c> and puts them on 192.168.49/24,
    /// so they are skipped by name and by range. Anything left is a real network this phone is on.
    /// </para>
    /// </remarks>
    private static IPAddress? LocalAddress()
    {
        try
        {
            foreach (var card in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (card.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (card.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                    continue;

                if (card.Name.Contains("p2p", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var address in card.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(address.Address)) continue;
                    if (IsPeerToPeer(address.Address)) continue;

                    return address.Address;
                }
            }
        }
        catch (Exception)
        {
            // A phone that will not describe its own network is a phone with no usable one.
        }

        return null;
    }

    /// <summary>Whether this address belongs to a Wi-Fi Direct group rather than a network.</summary>
    /// <remarks>
    /// 192.168.49/24 is what Android hands out inside a P2P group, on every device, always. Checked as
    /// well as the interface name because the name varies by vendor and the range does not.
    /// </remarks>
    private static bool IsPeerToPeer(IPAddress address)
    {
        var octets = address.GetAddressBytes();
        return octets is [192, 168, 49, _];
    }

    /// <summary>
    /// Says what it is doing, without saying its own name.
    /// </summary>
    /// <remarks>
    /// Whoever is listening knows which radio this is — the mesh puts the name on. Saying it here too
    /// produced "[Wi-Fi] [Wi-Fi] waiting on …", which is the sort of thing nobody fixes and everybody
    /// reads past.
    /// </remarks>
    private void Say(string what) => Status?.Invoke(what);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stopping.Cancel();

        foreach (var client in _peers.Values) try { client.Dispose(); } catch (Exception) { }
        _peers.Clear();

        try { _listener?.Stop(); } catch (Exception) { }
        _announcer?.Dispose();
        _stopping.Dispose();
        _gate.Dispose();
    }
}
