// SPDX-License-Identifier: MIT

using Aether.Extensibility.Events;

namespace Aether.Extensibility;

// ─────────────────────────────────────────────────────────────────────────────
//  Security directive contract
//
//  The AI Security Layer (CircleAI / BhenguAI) reasons over telemetry emitted
//  by IAetherTelemetry and publishes SecurityDirectives back to Aether's policy
//  engine. Aether honours or ignores each directive — adoption is a policy
//  decision for each deployment.
//
//  The boundary is one-way: Aether → telemetry → AI → directives → Aether.
//  The AI never calls any Aether service directly.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The action the AI Security Layer is recommending to Aether's policy engine.
/// </summary>
public enum SecurityDirectiveKind : byte
{
    /// <summary>Adjust the recorded trust score for a specific node.</summary>
    UpdateNodeTrust,

    /// <summary>Exclude the node from routing decisions (soft block — traffic can still arrive inbound).</summary>
    AvoidNode,

    /// <summary>Hard block — no traffic to or from the node until released.</summary>
    QuarantineNode,

    /// <summary>Lift an <see cref="AvoidNode"/> or <see cref="QuarantineNode"/> directive.</summary>
    ReleaseNode,

    /// <summary>
    /// Request that the user re-authenticates before a sensitive operation.
    /// Platform implementations surface an <see cref="AuthMethod"/> challenge.
    /// </summary>
    RequestReauth,

    /// <summary>Increase telemetry verbosity for the target node for deeper AI analysis.</summary>
    ElevateMonitoring,
}

/// <summary>
/// An instruction published by the AI Security Layer to Aether's policy engine.
/// Aether is never required to honour a directive — adoption is a policy decision
/// for each deployment. The directive carries all context needed to act on it
/// without further round-trips to the AI layer.
/// </summary>
/// <param name="Kind">What the AI is recommending.</param>
/// <param name="TargetNodeId">UHID of the node the directive targets, or <c>null</c> for mesh-wide directives.</param>
/// <param name="TrustScoreOverride">New trust score (0.0–1.0) for <see cref="SecurityDirectiveKind.UpdateNodeTrust"/>, or <c>null</c>.</param>
/// <param name="ThreatLevel">The threat level that triggered this directive.</param>
/// <param name="Reason">Human-readable rationale from the AI.</param>
/// <param name="Duration">How long the directive should remain active, or <c>null</c> for permanent.</param>
/// <param name="IssuedAt">UTC timestamp when the AI issued this directive.</param>
public sealed record SecurityDirective(
    SecurityDirectiveKind   Kind,
    string?                 TargetNodeId,
    double?                 TrustScoreOverride,
    AetherThreatLevel       ThreatLevel,
    string                  Reason,
    TimeSpan?               Duration,
    DateTimeOffset          IssuedAt)
{
    /// <summary><c>true</c> when the directive targets a specific node.</summary>
    public bool HasTarget => !string.IsNullOrWhiteSpace(TargetNodeId);

    /// <summary><c>true</c> when <see cref="Duration"/> is <c>null</c> — the directive has no automatic expiry.</summary>
    public bool IsPermanent => Duration is null;

    /// <summary>The UTC time at which a time-limited directive expires, or <c>null</c> for permanent directives.</summary>
    public DateTimeOffset? ExpiresAt => Duration.HasValue ? IssuedAt + Duration.Value : null;
}

/// <summary>
/// Point-in-time summary of the AI Security Layer's current posture, returned
/// by <see cref="IAetherAiProvider.GetNetworkHealthAsync"/> when the AI is active.
/// </summary>
/// <param name="OverallThreatLevel">Highest threat level currently observed across the mesh.</param>
/// <param name="QuarantinedNodeCount">Number of nodes currently under a <see cref="SecurityDirectiveKind.QuarantineNode"/> directive.</param>
/// <param name="MonitoredNodeCount">Number of nodes under elevated monitoring.</param>
/// <param name="IsActive">Whether the AI Security Layer is running and processing telemetry.</param>
/// <param name="AssessedAt">UTC time of this snapshot.</param>
public sealed record SecurityPosture(
    AetherThreatLevel  OverallThreatLevel,
    int                QuarantinedNodeCount,
    int                MonitoredNodeCount,
    bool               IsActive,
    DateTimeOffset     AssessedAt);

/// <summary>
/// Receives security directives published by the AI Security Layer. Implement
/// this on Aether's policy engine (or any host that wishes to act on AI security
/// recommendations) to participate in AI-guided security decisions.
///
/// <para>
/// Register an implementation via DI. When no implementation is registered,
/// all directives are silently discarded and the mesh operates without
/// AI-guided security enforcement.
/// </para>
/// </summary>
public interface ISecurityDirectiveConsumer
{
    /// <summary>
    /// Called each time the AI Security Layer issues a <see cref="SecurityDirective"/>.
    /// Implementations decide whether and how to honour it.
    ///
    /// <para>
    /// This method is invoked synchronously. Implementations must not block or
    /// throw — a misbehaving consumer must not affect the AI layer's operation.
    /// </para>
    /// </summary>
    void OnDirective(SecurityDirective directive);
}

/// <summary>
/// No-op <see cref="ISecurityDirectiveConsumer"/> — used when no policy engine
/// is registered. All directives are silently discarded.
/// </summary>
public sealed class NullSecurityDirectiveConsumer : ISecurityDirectiveConsumer
{
    /// <summary>The singleton instance.</summary>
    public static readonly NullSecurityDirectiveConsumer Instance = new();

    private NullSecurityDirectiveConsumer() { }

    /// <inheritdoc/>
    public void OnDirective(SecurityDirective directive) { }
}
