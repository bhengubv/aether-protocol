// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Extensibility;
using AetherNet.Incentive;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Security.Services;

/// <summary>
/// Default <see cref="IMeshTipService"/>. Sends and receives generic
/// <see cref="PacketType.TipPacket"/> packets.
///
/// <para>
/// Send path: build a <see cref="TipPacketPayload"/> → sign the payload's canonical
/// bytes with the local identity key (Ed25519) → serialise as snake_case JSON →
/// wrap in a <see cref="MeshPacket"/> → sign the enclosing packet via
/// <see cref="IPacketSigningService"/> → route toward the recipient (unicast over a
/// discovered route, falling back to broadcast).
/// </para>
///
/// <para>
/// Receive path: deserialise the payload → best-effort signature check → hand to the
/// host's <see cref="IAetherNetIncentiveProvider.SettleMeshTipAsync"/> → relay the
/// packet onward toward its addressed recipient (it is normal addressed traffic). A
/// malformed or unverifiable payload is logged and dropped, never thrown.
/// </para>
///
/// <para>
/// This service is purely a protocol mechanism. It attaches NO value semantics to
/// the amount and performs NO settlement — settlement is entirely the host's
/// business, expressed through the injected incentive provider. A bare node (default
/// no-op provider) accepts and relays tips but settles nothing.
/// </para>
/// </summary>
public sealed class MeshTipService : IMeshTipService
{
    /// <summary>Ed25519 signature length in bytes — used for the best-effort inbound check.</summary>
    private const int Ed25519SignatureLength = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IRoutingService _routing;
    private readonly IPacketSigningService _packetSigning;
    private readonly ISignalProtocolService _identity;
    private readonly IAetherNetIncentiveProvider _incentives;
    private readonly ILogger<MeshTipService> _logger;

    public MeshTipService(
        IMeshSender sender,
        IRoutingService routing,
        IPacketSigningService packetSigning,
        ISignalProtocolService identity,
        IAetherNetIncentiveProvider? incentives = null,
        ILogger<MeshTipService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _packetSigning = packetSigning ?? throw new ArgumentNullException(nameof(packetSigning));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _incentives = incentives ?? new DefaultIncentiveProvider();
        _logger = logger ?? NullLogger<MeshTipService>.Instance;
    }

    /// <inheritdoc />
    public async Task<MeshPacket> SendTipAsync(
        string recipientUhid,
        decimal amount,
        string trafficType,
        string ecosystemId,
        Guid? referenceId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(recipientUhid);
        ArgumentNullException.ThrowIfNull(trafficType);
        ArgumentNullException.ThrowIfNull(ecosystemId);

        // Build the payload. Amount is passed through verbatim — no policy applied.
        var payload = new TipPacketPayload
        {
            TipperUhid    = _sender.LocalUhid,
            RecipientUhid = recipientUhid,
            Amount        = amount,
            TrafficType   = trafficType,
            EcosystemId   = ecosystemId,
            ReferenceId   = referenceId,
            Timestamp     = DateTimeOffset.UtcNow,
        };

        // Sign the payload's canonical bytes with the local identity key (real Ed25519,
        // via the existing signing primitive that PacketSigningService itself delegates to).
        payload.Signature = await _identity
            .SignDataAsync(payload.BuildCanonicalData(), ct)
            .ConfigureAwait(false);

        var packet = new MeshPacket
        {
            Type            = PacketType.TipPacket,
            SourceUhid      = _sender.LocalUhid,
            DestinationUhid = recipientUhid,
            Ttl             = ProtocolConstants.DefaultTtl,
            Priority        = 0,
            Payload         = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
        };

        // Sign the enclosing MeshPacket (fills nonce/timestamp + envelope signature).
        var signed = await _packetSigning.SignPacketAsync(packet, ct).ConfigureAwait(false);

        // Route toward the recipient: unicast over a discovered route, else broadcast.
        var route = await _routing.FindRouteAsync(recipientUhid, ct).ConfigureAwait(false);
        if (route is not null)
            await _sender.SendAsync(signed, route.NextHopUhid, ct).ConfigureAwait(false);
        else
            await _sender.BroadcastAsync(signed, ct).ConfigureAwait(false);

        _logger.LogDebug(
            "Mesh tip sent: tipper={Tipper} recipient={Recipient} traffic={Traffic} ref={Ref} routed={Routed}",
            _sender.LocalUhid, recipientUhid, trafficType,
            referenceId, route is not null ? "unicast" : "broadcast");

        return signed;
    }

    /// <inheritdoc />
    public async Task HandleTipPacketAsync(MeshPacket packet, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.Type != PacketType.TipPacket)
        {
            _logger.LogDebug("HandleTipPacketAsync: unexpected packet type {Type} — ignored", packet.Type);
            return;
        }

        // 1. Deserialise the payload. A malformed payload is logged and dropped.
        TipPacketPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TipPacketPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Mesh tip from {Source}: JSON deserialization failed — dropped", packet.SourceUhid);
            return;
        }

        if (payload is null
            || string.IsNullOrEmpty(payload.TipperUhid)
            || string.IsNullOrEmpty(payload.RecipientUhid))
        {
            _logger.LogWarning(
                "Mesh tip from {Source}: payload missing required fields — dropped", packet.SourceUhid);
            return;
        }

        // 2. Best-effort signature check. The payload is self-signed by the tipper over its
        //    canonical bytes; without the tipper's published key we cannot do a full Ed25519
        //    verify here, so we require the signature to at least be present and well-formed
        //    (an Ed25519 signature is exactly 64 bytes). A payload carrying no signature, or a
        //    malformed one, is unverifiable — logged and dropped. The host's settlement
        //    provider is responsible for any stronger, key-bound verification it needs before
        //    crediting anything.
        if (payload.Signature is not { Length: Ed25519SignatureLength })
        {
            _logger.LogWarning(
                "Mesh tip from {Tipper}: missing or malformed signature — dropped", payload.TipperUhid);
            return;
        }

        // 3. Hand to the host's settlement provider. Default no-op settles nothing.
        await _incentives.SettleMeshTipAsync(payload, ct).ConfigureAwait(false);

        // 4. Relay onward toward the addressed recipient if this node is not the destination
        //    and the packet may still be forwarded. The tip is ordinary addressed traffic.
        if (!string.Equals(packet.DestinationUhid, _sender.LocalUhid, StringComparison.Ordinal)
            && packet.CanForward)
        {
            var route = await _routing.FindRouteAsync(packet.DestinationUhid, ct).ConfigureAwait(false);
            if (route is not null)
                await _sender.SendAsync(packet, route.NextHopUhid, ct).ConfigureAwait(false);
            else
                await _sender.BroadcastAsync(packet, ct).ConfigureAwait(false);

            await _incentives.RecordRelayAsync(_sender.LocalUhid, packet, ct).ConfigureAwait(false);
        }

        _logger.LogDebug(
            "Mesh tip handled: tipper={Tipper} recipient={Recipient} traffic={Traffic}",
            payload.TipperUhid, payload.RecipientUhid, payload.TrafficType);
    }

    /// <summary>Default no-op incentive provider — accepts and relays but settles nothing.</summary>
    private sealed class DefaultIncentiveProvider : IAetherNetIncentiveProvider
    {
    }
}
