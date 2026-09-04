// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Dtn;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Transport.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Sample.Shared.Services.Lab;

/// <summary>
/// Drives the real <see cref="DtnService"/> (delay-tolerant store-and-forward) with the real
/// <see cref="GeohashEpidemicStrategy"/> and <see cref="InMemoryDtnBundleStore"/> over an in-process
/// mesh. You leave a message for someone who is offline; it can't be delivered, so it waits. A carrier
/// that is geographically nearer the recipient (a longer shared geohash prefix) takes custody. When the
/// recipient reappears the carrier delivers directly, a delivery receipt flows back to you, and a
/// separate bundle is left to expire so its TTL sweep can be shown. Only the radio is simulated — the
/// custody handshake, the epidemic target selection and the receipt are the actual protocol.
/// </summary>
public sealed class DtnLabDemo : IDisposable
{
    private readonly object _gate = new();
    private readonly List<LogLine> _log = new();
    private readonly string _run = Guid.NewGuid().ToString("N")[..6];
    private readonly Dictionary<Guid, string> _text = new();

    // Coarse geohashes chosen so the carrier shares a longer prefix with the recipient than you do:
    //   you kd1p2rs  vs recipient ke7yc8p → shared "k"      (1)
    //   carrier ke7yc0m vs recipient ke7yc8p → shared "ke7yc" (5)  → the strategy picks the carrier.
    private const string YouGeo = "kd1p2rs";
    private const string CarrierGeo = "ke7yc0m";
    private const string RecipientGeo = "ke7yc8p";

    private Node _you = null!;
    private Node _carrier = null!;
    private Node? _recipient;            // created only when the recipient "reappears"
    private string _recipientUhid = "";
    private string _unreachableUhid = ""; // a recipient that never comes back — for the expiry demo

    private Guid _mainBundleId;
    private bool _started;
    private bool _disposed;

    private IReadOnlyList<NodeView> _view = Array.Empty<NodeView>();

    public event Action? Changed;

    public bool RecipientOnline => _recipient is not null;
    public bool HasMessage => _mainBundleId != Guid.Empty;
    public IReadOnlyList<NodeView> View => _view;

    public IReadOnlyList<LogLine> Log()
    {
        lock (_gate) return _log.ToArray();
    }

    // ─── Setup ──────────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_started) return;
        _started = true;

        _recipientUhid = $"lab:dtn:{_run}:Zanele";
        _unreachableUhid = $"lab:dtn:{_run}:Farai";

        _you = CreateNode("You", YouGeo);
        _carrier = CreateNode("Carrier", CarrierGeo);

        // You know both peers' addresses and rough locations, but neither the recipient nor the
        // unreachable peer has a transport yet — both read as offline.
        _you.Sender.AddPeer(_carrier.Uhid, CarrierGeo, NodeCapabilities.DtnCarrier, 0.9);
        _you.Sender.AddPeer(_recipientUhid, RecipientGeo, NodeCapabilities.None, 0.5);
        _you.Sender.AddPeer(_unreachableUhid, RecipientGeo, NodeCapabilities.None, 0.5);

        _carrier.Sender.AddPeer(_you.Uhid, YouGeo, NodeCapabilities.DtnCarrier, 0.9);
        _carrier.Sender.AddPeer(_recipientUhid, RecipientGeo, NodeCapabilities.None, 0.5);

        WireDispatch(_you);
        WireDispatch(_carrier);

        _you.Dtn.BundleDelivered += (_, receipt) =>
            Emit($"Delivery receipt reached you: bundle {Short(receipt.BundleId)} delivered after {receipt.TotalHops} hop(s), {receipt.TotalCustodyTransfers} custody transfer(s).", strong: true);

        Emit("You and a carrier are on the mesh. Zanele is offline. Leave her a message and watch it find its way.");
        _ = RefreshAsync();
    }

    private Node CreateNode(string name, string geohash)
    {
        var uhid = $"lab:dtn:{_run}:{name}";
        var transport = new InProcessTransportService(uhid, NullLogger<InProcessTransportService>.Instance);
        var sender = new DtnMeshSender(uhid, geohash, transport);
        var dtn = new DtnService(sender);
        return new Node(name, uhid, geohash, transport, sender, dtn);
    }

    private void WireDispatch(Node node)
    {
        var n = node;
        n.Transport.DataReceived += (_src, bytes) =>
        {
            MeshPacket packet;
            try { packet = PacketSerializer.Deserialize(bytes); }
            catch { return; }
            _ = n.Dtn.HandleAsync(packet);
        };
    }

    // ─── Step 1: leave a message for an offline recipient ────────────────────────

    public async Task LeaveMessageAsync(string message)
    {
        if (_you is null || HasMessage) return;

        var payload = Encoding.UTF8.GetBytes(message); // opaque ciphertext to the DTN layer
        var bundle = await _you.Dtn.CreateBundleAsync(_recipientUhid, payload, BundlePriority.Normal, RecipientGeo)
            .ConfigureAwait(false);
        _mainBundleId = bundle.Id;
        _text[bundle.Id] = message;

        Emit($"You sent “{message}” to Zanele — but she's offline, so it can't be delivered. Bundle {Short(bundle.Id)} is stored, status {bundle.Status}, waiting for a carrier.", strong: true);
        await RefreshAsync().ConfigureAwait(false);
    }

    // ─── Step 2: epidemic-replicate to the carrier ───────────────────────────────

    public async Task ReplicateAsync()
    {
        if (_you is null || !HasMessage) return;

        Emit("You run a delivery scan. Zanele is still unreachable, so the epidemic strategy looks for a carrier nearer to her…");
        await _you.Dtn.RunDeliveryScanAsync().ConfigureAwait(false);
        await Task.Delay(150).ConfigureAwait(false); // let the custody handshake settle

        var carrierHolds = (await _carrier.Dtn.GetActiveBundlesAsync().ConfigureAwait(false))
            .Any(b => b.Id == _mainBundleId);
        Emit(carrierHolds
            ? "The carrier took custody: its geohash ke7yc… shares five characters with Zanele's, yours shares one — so it is nearer, and the strategy chose it. It now carries the message for her."
            : "No eligible carrier accepted custody this pass.", strong: carrierHolds);
        await RefreshAsync().ConfigureAwait(false);
    }

    // ─── Step 3: recipient reappears; carrier delivers; receipt returns ──────────

    public async Task RecipientReturnsAsync()
    {
        if (_recipient is not null) return;

        _recipient = CreateNode("Zanele", RecipientGeo);
        // Re-create with the recipient's real uhid (CreateNode namespaced it as ...:Zanele already).
        _recipient.Sender.AddPeer(_you.Uhid, YouGeo, NodeCapabilities.DtnCarrier, 0.9);
        _recipient.Sender.AddPeer(_carrier.Uhid, CarrierGeo, NodeCapabilities.DtnCarrier, 0.9);
        WireDispatch(_recipient);

        _recipient.Dtn.BundleReceived += (_, e) =>
        {
            var text = Encoding.UTF8.GetString(e.EncryptedPayload);
            Emit($"Zanele is back — and her app decrypted the bundle: “{text}” ({e.HopCount} hop(s)).", strong: true);
        };

        Emit("Zanele's phone is back on the mesh. The carrier runs its delivery scan…");
        await Task.Delay(50).ConfigureAwait(false);
        await _carrier.Dtn.RunDeliveryScanAsync().ConfigureAwait(false);
        await Task.Delay(150).ConfigureAwait(false); // let delivery + receipt propagate

        // Also let the sender sweep: if nothing was handed to a carrier, it can now deliver directly. In
        // the normal path its copy is already Delivered by the returning receipt, so this is a no-op.
        await _you.Dtn.RunDeliveryScanAsync().ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    // ─── Expiry: a bundle nobody ever collects ───────────────────────────────────

    public async Task ExpireDemoAsync()
    {
        if (_you is null) return;

        var payload = Encoding.UTF8.GetBytes("Meet me at noon — this one has a short fuse.");
        var bundle = await _you.Dtn.CreateBundleAsync(_unreachableUhid, payload, BundlePriority.Low, RecipientGeo)
            .ConfigureAwait(false);
        Emit($"You leave bundle {Short(bundle.Id)} for a peer who never returns. Its TTL is {AetherNet.Constants.ProtocolConstants.DtnBundleTtlHours} hours — we fast-forward past it.");

        // Simulate the TTL window elapsing (the store's IsExpired reads the wall clock).
        bundle.ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        var expired = await _you.Dtn.ExpireStaleAsync().ConfigureAwait(false);
        Emit($"TTL sweep expired {expired} bundle(s): {Short(bundle.Id)} is discarded and its custody slot freed.", strong: true);
        await RefreshAsync().ConfigureAwait(false);
    }

    public void ClearLog()
    {
        lock (_gate) _log.Clear();
        RaiseChanged();
    }

    // ─── Internals ────────────────────────────────────────────────────────────────

    private async Task RefreshAsync()
    {
        var views = new List<NodeView>();
        foreach (var node in new[] { _you, _carrier, _recipient })
        {
            if (node is null) continue;
            var active = await node.Dtn.GetActiveBundlesAsync().ConfigureAwait(false);
            var held = active
                .Select(b => new HeldBundle(Short(b.Id), b.Status.ToString(), _text.GetValueOrDefault(b.Id, "·")))
                .ToArray();
            views.Add(new NodeView(node.Name, Online: true, held));
        }
        if (_recipient is null)
            views.Add(new NodeView("Zanele", Online: false, Array.Empty<HeldBundle>()));

        _view = views;
        RaiseChanged();
    }

    private void Emit(string text, bool strong = false)
    {
        lock (_gate)
        {
            _log.Add(new LogLine(text, strong));
            if (_log.Count > 200) _log.RemoveRange(0, _log.Count - 200);
        }
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke();

    private static string Short(Guid id) => id.ToString("N")[..8];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _you?.Transport.Dispose();
        _carrier?.Transport.Dispose();
        _recipient?.Transport.Dispose();
    }

    // ─── View + node types ───────────────────────────────────────────────────────

    public sealed record LogLine(string Text, bool Strong);
    public sealed record HeldBundle(string Id8, string Status, string Text);
    public sealed record NodeView(string Name, bool Online, IReadOnlyList<HeldBundle> Held);

    private sealed class Node
    {
        public Node(string name, string uhid, string geo, InProcessTransportService transport,
            DtnMeshSender sender, DtnService dtn)
        {
            Name = name;
            Uhid = uhid;
            Geo = geo;
            Transport = transport;
            Sender = sender;
            Dtn = dtn;
        }

        public string Name { get; }
        public string Uhid { get; }
        public string Geo { get; }
        public InProcessTransportService Transport { get; }
        public DtnMeshSender Sender { get; }
        public DtnService Dtn { get; }
    }
}

/// <summary>
/// A thin in-process <see cref="IMeshSender"/> that advertises each peer's DTN-carrier capability and
/// geohash — the metadata <see cref="GeohashEpidemicStrategy"/> needs to choose a carrier. A peer counts
/// as connected only while its transport is live in the simulated network, so "offline" is real: the
/// recipient's transport simply does not exist yet.
/// </summary>
internal sealed class DtnMeshSender : IMeshSender
{
    private readonly InProcessTransportService _transport;
    private readonly Dictionary<string, PeerInfo> _peers = new(StringComparer.Ordinal);

    public DtnMeshSender(string localUhid, string geohash, InProcessTransportService transport)
    {
        LocalUhid = localUhid;
        LocalGeohash = geohash;
        _transport = transport;
    }

    public string LocalUhid { get; }
    public string? LocalGeohash { get; }

    public void AddPeer(string uhid, string geohash, NodeCapabilities capabilities, double reliability)
        => _peers[uhid] = new PeerInfo
        {
            Uhid = uhid,
            Geohash = geohash,
            Capabilities = capabilities,
            ReliabilityScore = reliability,
            TransportType = "InProcess",
        };

    public IReadOnlyList<PeerInfo> GetConnectedPeers()
        => _peers.Values.Where(p => _transport.IsConnected(p.Uhid)).ToArray();

    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
        => _transport.SendAsync(nextHopUhid, PacketSerializer.Serialize(packet), cancellationToken);

    public async Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        var bytes = PacketSerializer.Serialize(packet);
        var delivered = 0;
        foreach (var peer in _peers.Values)
            if (await _transport.SendAsync(peer.Uhid, bytes, cancellationToken).ConfigureAwait(false))
                delivered++;
        return delivered;
    }
}
