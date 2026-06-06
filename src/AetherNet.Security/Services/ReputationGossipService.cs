// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Protocol;
using AetherNet.Reputation;
using AetherNet.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AetherNet.Security.Services;

/// <summary>
/// Broadcasts and handles signed <see cref="PacketType.ReputationUpdate"/> gossip packets.
///
/// Broadcast path: serialize <see cref="ReputationUpdatePayload"/> → wrap in a signed
/// <see cref="MeshPacket"/> → <see cref="IMeshSender.BroadcastAsync"/>.
///
/// Receive path: verify signature → check freshness → deserialize payload →
/// scale by reporter reputation (R) → apply <c>ScoreDelta × R</c> to target.
/// </summary>
public sealed class ReputationGossipService : IReputationGossipService
{
    private const int FreshnessWindowMs = 5 * 60 * 1000; // 5 minutes

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IPacketSigningService _signing;
    private readonly INodeReputationService _reputation;
    private readonly ILogger<ReputationGossipService> _logger;

    public ReputationGossipService(
        IMeshSender sender,
        IPacketSigningService signing,
        INodeReputationService reputation,
        ILogger<ReputationGossipService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _signing = signing ?? throw new ArgumentNullException(nameof(signing));
        _reputation = reputation ?? throw new ArgumentNullException(nameof(reputation));
        _logger = logger ?? NullLogger<ReputationGossipService>.Instance;
    }

    /// <inheritdoc />
    public async Task BroadcastReputationUpdateAsync(
        string targetUhid,
        double scoreDelta,
        string reason,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetUhid);

        var clampedDelta = Math.Clamp(scoreDelta, -1.0, 1.0);

        var payload = new ReputationUpdatePayload
        {
            ReporterUhid = _sender.LocalUhid,
            TargetUhid   = targetUhid,
            ScoreDelta   = clampedDelta,
            TimestampMs  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Reason       = reason ?? string.Empty,
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

        var packet = new MeshPacket
        {
            Type            = PacketType.ReputationUpdate,
            SourceUhid      = _sender.LocalUhid,
            DestinationUhid = "*",
            Ttl             = 3,  // short TTL — gossip is time-sensitive and local
            Payload         = payloadBytes,
        };

        var signed = await _signing.SignPacketAsync(packet, ct).ConfigureAwait(false);
        var delivered = await _sender.BroadcastAsync(signed, ct).ConfigureAwait(false);

        _logger.LogDebug(
            "Reputation gossip broadcast: reporter={Reporter} target={Target} delta={Delta:+0.00;-0.00} reason={Reason} peers={Peers}",
            _sender.LocalUhid, targetUhid, clampedDelta, reason, delivered);
    }

    /// <inheritdoc />
    public async Task<bool> HandleGossipPacketAsync(
        MeshPacket packet,
        byte[] senderPublicKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(senderPublicKey);

        if (packet.Type != PacketType.ReputationUpdate)
        {
            _logger.LogDebug("HandleGossipPacketAsync: unexpected packet type {Type} — ignored", packet.Type);
            return false;
        }

        // 1. Verify enclosing MeshPacket signature (also checks packet-level freshness
        //    and nonce deduplication inside VerifyPacketAsync).
        var signatureValid = await _signing.VerifyPacketAsync(packet, senderPublicKey, ct)
            .ConfigureAwait(false);
        if (!signatureValid)
        {
            _logger.LogWarning(
                "Reputation gossip from {Source}: packet signature invalid — dropped",
                packet.SourceUhid);
            return false;
        }

        // 2. Deserialise payload.
        ReputationUpdatePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ReputationUpdatePayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "Reputation gossip from {Source}: JSON deserialization failed — dropped",
                packet.SourceUhid);
            return false;
        }

        if (payload is null
            || string.IsNullOrEmpty(payload.ReporterUhid)
            || string.IsNullOrEmpty(payload.TargetUhid))
        {
            _logger.LogWarning(
                "Reputation gossip from {Source}: payload missing required fields — dropped",
                packet.SourceUhid);
            return false;
        }

        // 3. Payload-level freshness check (belt-and-suspenders on top of packet timestamp).
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payloadAgeMs = Math.Abs(nowMs - payload.TimestampMs);
        if (payloadAgeMs > FreshnessWindowMs)
        {
            _logger.LogDebug(
                "Reputation gossip from {Reporter}: payload timestamp too old ({AgeMs}ms) — dropped",
                payload.ReporterUhid, payloadAgeMs);
            return false;
        }

        // Do not apply our own gossip echoed back.
        if (string.Equals(payload.ReporterUhid, _sender.LocalUhid, StringComparison.Ordinal))
            return false;

        // Clamp claimed delta to valid range.
        var claimedDelta = Math.Clamp(payload.ScoreDelta, -1.0, 1.0);

        // 4. Fetch reporter's local reputation R and weight the delta.
        var reporterReputation = await _reputation
            .GetReputationScoreAsync(payload.ReporterUhid, ct)
            .ConfigureAwait(false);

        var effectiveDelta = claimedDelta * reporterReputation;

        // 5. Apply weighted delta to the target's score.
        await _reputation
            .ApplyWeightedDeltaAsync(payload.TargetUhid, effectiveDelta, ct)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "Reputation gossip applied: reporter={Reporter}(R={R:F2}) target={Target} " +
            "claimed={Claimed:+0.00;-0.00} effective={Effective:+0.00;-0.00} reason={Reason}",
            payload.ReporterUhid, reporterReputation, payload.TargetUhid,
            claimedDelta, effectiveDelta, payload.Reason);

        return true;
    }
}
