// SPDX-License-Identifier: MIT

namespace AetherMesh.Extensibility.Events;

/// <summary>
/// Protocol-level threat severity detected by Aether itself, before any AI
/// reasoning is applied.
///
/// <para>
/// Distinct from <see cref="AiThreatLevel"/>: this is raw protocol observation;
/// <see cref="AiThreatLevel"/> is the AI layer's reasoned verdict after consuming
/// these events via <see cref="IAetherMeshTelemetry"/>.
/// </para>
/// </summary>
public enum AetherMeshThreatLevel : byte
{
    /// <summary>No anomaly detected.</summary>
    None     = 0,

    /// <summary>Minor deviation — log and monitor.</summary>
    Low      = 1,

    /// <summary>Moderate anomaly — warrants AI review.</summary>
    Medium   = 2,

    /// <summary>Significant threat — AI should issue a security directive.</summary>
    High     = 3,

    /// <summary>Confirmed active attack — immediate AI security directive expected.</summary>
    Critical = 4,
}

/// <summary>
/// Categories of security-relevant observations Aether can detect at the
/// protocol layer, without requiring AI. The AI Security Layer consumes
/// these events to produce threat assessments and
/// <see cref="SecurityDirective"/> outputs.
/// </summary>
public enum AetherMeshSecurityEventKind : byte
{
    /// <summary>A node attempted to authenticate into the mesh.</summary>
    NodeAuthAttempt,

    /// <summary>Traffic was observed deviating from expected routing paths.</summary>
    RoutingAnomaly,

    /// <summary>A node's behaviour deviated from its established baseline.</summary>
    NodeBehaviourChange,

    /// <summary>A key exchange or certificate validation event occurred.</summary>
    EncryptionEvent,

    /// <summary>Active attack signature detected (e.g. replay, spoofing, MITM).</summary>
    IntrusionSignal,

    /// <summary>A node requested capabilities beyond its granted level.</summary>
    PrivilegeAttempt,
}

/// <summary>
/// Emitted by Aether when a security-relevant event occurs at the protocol
/// layer. This is the primary feed for the AI Security Layer.
///
/// <para>
/// Aether never calls into the AI — it only emits. The AI subscribes via
/// <see cref="IAetherMeshTelemetry"/> and may subsequently publish a
/// <see cref="SecurityDirective"/> back through
/// <see cref="ISecurityDirectiveConsumer"/>.
/// </para>
/// </summary>
/// <param name="NodeId">UHID of the node that triggered the event.</param>
/// <param name="Kind">The category of security event.</param>
/// <param name="ThreatLevel">Aether's own protocol-layer severity assessment.</param>
/// <param name="Description">Human-readable description of the event.</param>
/// <param name="Metadata">Additional key-value context pairs.</param>
/// <param name="OccurredAt">UTC timestamp of the event.</param>
public sealed record AetherMeshSecurityEvent(
    string                                  NodeId,
    AetherMeshSecurityEventKind                 Kind,
    AetherMeshThreatLevel                       ThreatLevel,
    string                                  Description,
    IReadOnlyDictionary<string, string>     Metadata,
    DateTimeOffset                          OccurredAt)
{
    /// <summary><c>true</c> when <see cref="ThreatLevel"/> is <see cref="AetherMeshThreatLevel.High"/> or <see cref="AetherMeshThreatLevel.Critical"/>.</summary>
    public bool IsHighSeverity =>
        ThreatLevel is AetherMeshThreatLevel.High or AetherMeshThreatLevel.Critical;
}
