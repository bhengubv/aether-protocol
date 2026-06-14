// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.ApiClients;
using AetherNet.Incentive;
using AetherNet.Protocol;
using AetherNet.Security.Services;
using AetherNet.Tipping.Models;
using Microsoft.Extensions.Logging;

namespace AetherNet.Tipping.Services;

/// <summary>
/// Auto-tips a relay node after a successful relay event, and settles inbound
/// <see cref="PacketType.TipPacket"/> (24) packets at gateway nodes.
///
/// <para>
/// The <c>On*Relayed/Served/Accepted/Delivered/Shared</c> helpers queue a
/// suggested-amount tip to the peer that performed the work (via
/// <see cref="ITippingService"/>), each gated on that traffic type's policy being
/// enabled. <see cref="HandleTipPacketAsync"/> is the gateway-settlement path: a node
/// with internet forwards the signed tip envelope to the backend for ledger
/// settlement into both SDPKT wallets; a non-gateway node simply relays it onward
/// like any other addressed packet.
/// </para>
///
/// <para>
/// The inbound payload is the generic protocol envelope
/// <see cref="AetherNet.Incentive.TipPacketPayload"/> (snake_case JSON, byte-identical
/// wire) — the same type the protocol-level <see cref="IMeshTipService"/> sends and
/// receives; this layer does not define a second, conflicting tip shape.
/// </para>
/// </summary>
public sealed class TipEventHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly ITippingService _tipping;
    private readonly IAetherApiClient _apiClient;
    private readonly ILogger<TipEventHandler> _logger;

    public TipEventHandler(
        ITippingService tipping,
        IAetherApiClient apiClient,
        ILogger<TipEventHandler> logger)
    {
        _tipping = tipping ?? throw new ArgumentNullException(nameof(tipping));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Called when this node successfully relays a message for another peer.
    /// The original sender tips the relay node.
    /// </summary>
    public async Task OnMessageRelayedAsync(string relayNodeUhid, Guid? referenceId = null)
    {
        var policy = await _tipping.GetPolicyAsync(TipTrafficType.MessageRelay);
        if (policy is null || !policy.IsEnabled) return;
        await _tipping.TipNodeAsync(relayNodeUhid, policy.SuggestedAmount, TipTrafficType.MessageRelay, referenceId);
    }

    /// <summary>
    /// Called when a peer serves a chunk to this node (BitTorrent-style).
    /// </summary>
    public async Task OnChunkServedAsync(string seederUhid, Guid? referenceId = null)
    {
        var policy = await _tipping.GetPolicyAsync(TipTrafficType.ChunkServe);
        if (policy is null || !policy.IsEnabled) return;
        await _tipping.TipNodeAsync(seederUhid, policy.SuggestedAmount, TipTrafficType.ChunkServe, referenceId);
    }

    /// <summary>
    /// Called when a peer relays a stream segment.
    /// </summary>
    public async Task OnStreamRelayedAsync(string relayNodeUhid, Guid? referenceId = null)
    {
        var policy = await _tipping.GetPolicyAsync(TipTrafficType.StreamRelay);
        if (policy is null || !policy.IsEnabled) return;
        await _tipping.TipNodeAsync(relayNodeUhid, policy.SuggestedAmount, TipTrafficType.StreamRelay, referenceId);
    }

    /// <summary>
    /// Called when a DTN node accepts custody of a bundle.
    /// </summary>
    public async Task OnDtnCustodyAcceptedAsync(string custodianUhid, Guid? bundleId = null)
    {
        var policy = await _tipping.GetPolicyAsync(TipTrafficType.DtnCustody);
        if (policy is null || !policy.IsEnabled) return;
        await _tipping.TipNodeAsync(custodianUhid, policy.SuggestedAmount, TipTrafficType.DtnCustody, bundleId);
    }

    /// <summary>
    /// Called when a DTN bundle is finally delivered.
    /// </summary>
    public async Task OnDtnDeliveredAsync(string delivererUhid, Guid? bundleId = null)
    {
        var policy = await _tipping.GetPolicyAsync(TipTrafficType.DtnDelivery);
        if (policy is null || !policy.IsEnabled) return;
        await _tipping.TipNodeAsync(delivererUhid, policy.SuggestedAmount, TipTrafficType.DtnDelivery, bundleId);
    }

    /// <summary>
    /// Called when a voice frame is relayed.
    /// </summary>
    public async Task OnVoiceRelayedAsync(string relayNodeUhid, Guid? callId = null)
    {
        var policy = await _tipping.GetPolicyAsync(TipTrafficType.VoiceRelay);
        if (policy is null || !policy.IsEnabled) return;
        await _tipping.TipNodeAsync(relayNodeUhid, policy.SuggestedAmount, TipTrafficType.VoiceRelay, callId);
    }

    /// <summary>
    /// Called when a gateway shares bandwidth.
    /// </summary>
    public async Task OnGatewayBandwidthSharedAsync(string gatewayUhid, Guid? referenceId = null)
    {
        var policy = await _tipping.GetPolicyAsync(TipTrafficType.GatewayShare);
        if (policy is null || !policy.IsEnabled) return;
        await _tipping.TipNodeAsync(gatewayUhid, policy.SuggestedAmount, TipTrafficType.GatewayShare, referenceId);
    }

    /// <summary>
    /// Handle an inbound <see cref="PacketType.TipPacket"/> from the mesh.
    /// If this node is a gateway with internet, forward it to the backend for ledger
    /// settlement into both SDPKT wallets. Otherwise it relays onward like any other
    /// packet (handled by normal routing — this method is a no-op for non-gateways).
    /// </summary>
    public async Task HandleTipPacketAsync(MeshPacket packet, bool isGateway)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (!isGateway)
        {
            _logger.LogDebug("TipPacket received but not a gateway — will relay onward");
            return;
        }

        try
        {
            var tipPayload = JsonSerializer.Deserialize<TipPacketPayload>(packet.Payload, JsonOptions);
            if (tipPayload is null)
            {
                _logger.LogWarning("Invalid TipPacket payload — cannot deserialize");
                return;
            }

            // Forward to the backend for mesh settlement
            await _apiClient.MeshSettleTipAsync(new
            {
                tipper_uhid = tipPayload.TipperUhid,
                recipient_uhid = tipPayload.RecipientUhid,
                amount = tipPayload.Amount,
                traffic_type = tipPayload.TrafficType,
                reference_id = tipPayload.ReferenceId,
                signature = tipPayload.Signature,
                timestamp = tipPayload.Timestamp
            });

            _logger.LogInformation("Gateway settled TipPacket: {Amount} ZAR from {Tipper} to {Recipient}",
                tipPayload.Amount,
                LogSanitizer.SanitizeUhid(tipPayload.TipperUhid),
                LogSanitizer.SanitizeUhid(tipPayload.RecipientUhid));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gateway TipPacket settlement failed — packet may be retried");
        }
    }
}
