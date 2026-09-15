// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Dtn;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Messaging;

/// <summary>
/// The inbound "last mile": raw bytes off a transport link → <see cref="PacketSerializer"/> →
/// carry-for-a-third-node relay decision → dispatch to the layer that understands each packet type.
///
/// <para>
/// This is the glue every host used to re-write by hand — the console demo, the mesh-web node, and the
/// Android app each hard-coded <c>DataReceived → PacketSerializer.Deserialize → switch(Type) →
/// HandleAsync</c>, and only the app also carried packets for other nodes. That is the "built, last mile
/// is the caller's" defect: <see cref="IMessagingService.HandleAsync"/> takes an already-deserialized
/// packet and never sees the wire. This type owns that mile once, for everyone.
/// </para>
///
/// <para>
/// It is transport-agnostic: a host wires its own receive event to <see cref="OnBytesAsync"/> (or, when it
/// already has a packet, <see cref="DispatchAsync"/>) and registers handlers for any types beyond the core
/// set via <see cref="Register"/>. Relaying is opt-in: supply a <see cref="MeshRelay"/> and an
/// <see cref="IWireAddressResolver"/> and non-local unicast is carried one hop toward a recognised
/// recipient; omit either and the node carries for nobody.
/// </para>
/// </summary>
public sealed class MeshInboundDispatcher
{
    // Types the library already forwards through their own layer — the relay must not double-forward
    // them. Routing re-floods RREQ and forwards RREP; DTN owns bundle custody hand-off. Everything else
    // with a destination is the end-to-end unicast plane the relay carries.
    private static readonly HashSet<PacketType> OwnedByOtherForwarders =
    [
        PacketType.RouteRequest, PacketType.RouteReply,
        PacketType.DtnBundle, PacketType.DtnCustodyAck, PacketType.DtnDeliveryReceipt,
    ];

    private readonly IMeshSender? _sender;
    private readonly IMessagingService? _messaging;
    private readonly IRoutingService? _routing;
    private readonly IDtnService? _dtn;
    private readonly MeshRelay? _relay;
    private readonly IWireAddressResolver? _resolver;
    private readonly ILogger<MeshInboundDispatcher> _logger;
    private readonly ConcurrentDictionary<PacketType, Func<MeshPacket, CancellationToken, Task>> _handlers = new();

    public MeshInboundDispatcher(
        IMeshSender? sender = null,
        IMessagingService? messaging = null,
        IRoutingService? routing = null,
        IDtnService? dtn = null,
        MeshRelay? relay = null,
        IWireAddressResolver? resolver = null,
        ILogger<MeshInboundDispatcher>? logger = null)
    {
        // A relay needs a sender to forward what it carries; a dispatch-only host (no relay) needs none.
        if (relay is not null && sender is null)
            throw new ArgumentException("A relay needs an IMeshSender to forward the packets it carries.", nameof(sender));
        _sender = sender;
        _messaging = messaging;
        _routing = routing;
        _dtn = dtn;
        _relay = relay;
        _resolver = resolver;
        _logger = logger ?? NullLogger<MeshInboundDispatcher>.Instance;
    }

    /// <summary>How many packets this node has carried for other nodes (0 when no relay is wired).</summary>
    public long Carried => _relay?.Carried ?? 0;

    /// <summary>
    /// Register a handler for a packet type. It wins over the built-in dispatch for that type, so it can
    /// both add a new type (SOS, presence, prekey, an app's own kinds) and override a core one.
    /// Re-registering replaces the previous handler.
    /// </summary>
    public void Register(PacketType type, Func<MeshPacket, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[type] = handler;
    }

    /// <summary>
    /// The pump entry point: raw inbound bytes off a transport link. A malformed frame is logged and
    /// dropped — a bad frame must never take the pump down. <paramref name="linkSenderUhid"/> is the
    /// immediate link-layer neighbour the bytes arrived from (routing uses it to install a reverse
    /// route); null if the transport does not surface it.
    /// </summary>
    public async Task OnBytesAsync(string? linkSenderUhid, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (!PacketSerializer.TryDeserialize(bytes, out var packet) || packet is null)
        {
            _logger.LogDebug("Dropped an inbound frame ({Bytes} B) — not a valid MeshPacket", bytes.Length);
            return;
        }

        await DispatchAsync(packet, linkSenderUhid, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Dispatch an already-deserialized packet: run the relay decision first (carry for a third node),
    /// then hand what is genuinely local to messaging / routing / DTN / a registered handler.
    /// </summary>
    public async Task DispatchAsync(MeshPacket packet, string? linkSenderUhid = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // 1) Relay plane — only when wired, and only for types the library does not already forward.
        if (_relay is not null && _resolver is not null && !OwnedByOtherForwarders.Contains(packet.Type))
        {
            var mine = _resolver.IsLocal(packet.DestinationUhid);
            var from = _resolver.Recognise(packet.SourceUhid);
            var to = _resolver.Recognise(packet.DestinationUhid);
            var decision = _relay.Look(packet, mine, from, to);

            if (decision.ShouldCarry)
            {
                var onward = MeshRelay.OneHopShorter(packet);
                var reached = await _sender!.SendAsync(onward, decision.To!, cancellationToken).ConfigureAwait(false);
                if (reached)
                    _logger.LogDebug("Carried {Type} {Id} for {To} — {Ttl} hops left", packet.Type, packet.Id, decision.To, onward.Ttl);
                else
                    _logger.LogDebug("Could not reach {To} to carry {Type} {Id} — {Ttl} hops left", decision.To, packet.Type, packet.Id, onward.Ttl);
                return; // carried (or attempted) — never also delivered locally
            }

            if (decision.Verdict != MeshRelay.Verdict.ForMe)
                return; // expired / already-carried / not-ours — drop; do not dispatch locally
            // ForMe → fall through to local dispatch
        }

        // 1.5) Normalise rotating wire addresses to stable identities before anything downstream keys on
        // them. A resolver present means this node speaks ERID: the source may be a contact's current
        // rotating address (resolve it to their stable tag so the ratchet, routing, and handlers key on
        // the identity), and the destination may be one of our own rotating addresses (rewrite it to our
        // stable UHID so the messaging layer's "is this for me?" check passes). Without a resolver the
        // namespace is already flat and nothing here moves. A carried packet never reaches this point —
        // it keeps its wire addresses so the next hop can resolve them in turn.
        if (_resolver is not null)
            NormalizeInbound(packet);

        // 2) Local dispatch. A registered handler wins for its type; otherwise the core routes apply.
        if (_handlers.TryGetValue(packet.Type, out var handler))
        {
            await handler(packet, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (packet.Type)
        {
            case PacketType.Data:
            case PacketType.Ack:
                if (_messaging is not null)
                    await _messaging.HandleAsync(packet, cancellationToken).ConfigureAwait(false);
                else
                    _logger.LogDebug("No messaging service wired — dropped {Type} {Id}", packet.Type, packet.Id);
                break;

            case PacketType.RouteRequest:
                if (_routing is not null)
                    await _routing.HandleRouteRequestAsync(packet, linkSenderUhid, cancellationToken).ConfigureAwait(false);
                break;

            case PacketType.RouteReply:
                if (_routing is not null)
                    await _routing.HandleRouteReplyAsync(packet, cancellationToken).ConfigureAwait(false);
                break;

            case PacketType.DtnBundle:
            case PacketType.DtnCustodyAck:
            case PacketType.DtnDeliveryReceipt:
                if (_dtn is not null)
                    await _dtn.HandleAsync(packet, cancellationToken).ConfigureAwait(false);
                break;

            default:
                _logger.LogDebug("No handler for {Type} {Id} — register one via Register(PacketType, …)", packet.Type, packet.Id);
                break;
        }
    }

    /// <summary>
    /// Turn a packet's rotating wire addresses into the stable identities the messaging layer, routing,
    /// and registered handlers key on. Only an address the resolver actually recognises is rewritten, so
    /// a packet already on stable tags — a peer that has not exchanged routing keys yet, a flat-namespace
    /// host — passes through untouched.
    /// </summary>
    private void NormalizeInbound(MeshPacket packet)
    {
        var senderTag = _resolver!.Recognise(packet.SourceUhid);
        if (senderTag is not null && !string.Equals(senderTag, packet.SourceUhid, StringComparison.Ordinal))
            packet.SourceUhid = senderTag;

        // Only the sender knows its own stable UHID, so a dest rewrite needs one wired; a dispatch-only
        // host without a sender simply leaves the destination as the resolver saw it.
        if (_sender is not null
            && _resolver.IsLocal(packet.DestinationUhid)
            && !string.Equals(packet.DestinationUhid, _sender.LocalUhid, StringComparison.Ordinal))
            packet.DestinationUhid = _sender.LocalUhid;
    }
}
