// SPDX-License-Identifier: MIT

namespace Aether.Reputation;

/// <summary>
/// JSON payload for <see cref="Protocol.PacketType.ReputationUpdate"/> packets.
/// Wire format: UTF-8 JSON with snake_case property names.
///
/// Gossip verification steps (recipient side):
/// <list type="number">
///   <item>Verify the enclosing <c>MeshPacket</c> signature via
///     <c>IPacketSigningService.VerifyPacketAsync</c>.</item>
///   <item>Check <see cref="TimestampMs"/> freshness (5-minute window, same as
///     standard packet freshness).</item>
///   <item>Look up the reporter's own reputation score R in the local
///     <c>INodeReputationService</c>.</item>
///   <item>Apply weighted delta: <c>effective_delta = ScoreDelta × R</c>.
///     A fully trusted reporter (R=1.0) applies the full claimed delta;
///     an untrusted reporter (R=0.2) applies only 20%.</item>
/// </list>
/// </summary>
public sealed class ReputationUpdatePayload
{
    /// <summary>UHID of the node reporting the observation (the gossip sender).</summary>
    public string ReporterUhid { get; set; } = string.Empty;

    /// <summary>UHID of the node whose reputation is being reported.</summary>
    public string TargetUhid { get; set; } = string.Empty;

    /// <summary>
    /// Raw score delta claimed by the reporter. Negative = degradation; positive = improvement.
    /// Clamped to [−1.0, +1.0] before application. The recipient MUST scale by the reporter's
    /// local reputation before applying (see class-level documentation).
    /// </summary>
    public double ScoreDelta { get; set; }

    /// <summary>Unix timestamp in milliseconds when the observation was made.</summary>
    public long TimestampMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Human-readable reason code for auditing and debugging.
    /// Examples: "sig_failure", "replay_attack", "rreq_flood", "delivery_failure".
    /// Not used in scoring logic.
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}
