// SPDX-License-Identifier: MIT

using AetherMesh.Protocol;

namespace AetherMesh.Extensibility;

// ─────────────────────────────────────────────────────────────────────────────
//  Security audit contract (Claude-BugHunter integration)
//
//  Claude-BugHunter (github.com/bhengubv/Claude-BugHunter) covers 51 skills
//  across 24 vulnerability classes. This interface maps the classes relevant
//  to a mesh protocol into a programmatic audit contract that:
//    1. Security researchers can implement to report findings against live nodes.
//    2. CI pipelines can implement to run static audit passes over packet captures.
//    3. The AI Security Layer (CircleAI) can implement to correlate telemetry
//       events with known CVE/vuln patterns.
//
//  Mesh-relevant BugHunter skill mappings:
//    hunt-auth-bypass       → AetherVulnerabilityClass.AuthBypass
//    hunt-race-condition    → AetherVulnerabilityClass.RaceCondition
//    hunt-idor              → AetherVulnerabilityClass.InformationDisclosure
//    hunt-rce               → AetherVulnerabilityClass.RemoteCodeExecution
//    hunt-business-logic    → AetherVulnerabilityClass.BusinessLogic (Sybil, free-rider)
//    hunt-api-misconfig     → AetherVulnerabilityClass.ProtocolMisconfiguration
//    hunt-ssrf              → AetherVulnerabilityClass.RelayAbuse
//    hunt-dos               → AetherVulnerabilityClass.DenialOfService
//    supply-chain-attack-recon → AetherVulnerabilityClass.SupplyChain
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Vulnerability classes relevant to a mesh protocol, mapped to their
/// Claude-BugHunter skill counterparts.
/// </summary>
public enum AetherVulnerabilityClass : byte
{
    /// <summary>
    /// Handshake bypass, UHID spoofing, or fake node identity.
    /// BugHunter: <c>hunt-auth-bypass</c>.
    /// </summary>
    AuthBypass,

    /// <summary>
    /// Packet replay attack — replaying a previously captured valid packet
    /// to re-trigger an action (e.g. re-route, re-deliver content).
    /// BugHunter: <c>hunt-race-condition</c> (packet-ordering variant).
    /// </summary>
    ReplayAttack,

    /// <summary>
    /// Time-of-check/time-of-use race in packet processing or chunk assembly.
    /// BugHunter: <c>hunt-race-condition</c>.
    /// </summary>
    RaceCondition,

    /// <summary>
    /// UHID or content-hash enumeration exposes peer identity or content catalogue.
    /// BugHunter: <c>hunt-idor</c>.
    /// </summary>
    InformationDisclosure,

    /// <summary>
    /// Malformed packet triggers a parser crash or code execution.
    /// BugHunter: <c>hunt-rce</c>.
    /// </summary>
    RemoteCodeExecution,

    /// <summary>
    /// Sybil attack, free-rider routing abuse, or reputation manipulation.
    /// BugHunter: <c>hunt-business-logic</c>.
    /// </summary>
    BusinessLogic,

    /// <summary>
    /// NodeCapability escalation or protocol version downgrade.
    /// BugHunter: <c>hunt-api-misconfig</c>.
    /// </summary>
    ProtocolMisconfiguration,

    /// <summary>
    /// Relay node abused as a proxy to access services not reachable by the attacker.
    /// BugHunter: <c>hunt-ssrf</c>.
    /// </summary>
    RelayAbuse,

    /// <summary>
    /// Mesh flooding or resource exhaustion to partition or degrade the network.
    /// BugHunter: <c>hunt-dos</c>.
    /// </summary>
    DenialOfService,

    /// <summary>
    /// Malicious content chunk advertised with a valid root hash but corrupted data.
    /// BugHunter: <c>hunt-business-logic</c> (content poisoning variant).
    /// </summary>
    ContentPoisoning,

    /// <summary>
    /// Timing or size correlation attack deanonymising mesh participants.
    /// BugHunter: <c>hunt-misc</c>.
    /// </summary>
    TrafficAnalysis,

    /// <summary>
    /// Compromised dependency in a language implementation or build chain.
    /// BugHunter: <c>supply-chain-attack-recon</c>.
    /// </summary>
    SupplyChain,
}

/// <summary>
/// Severity of a security audit finding, aligned to the BugHunter
/// 7-Question Gate and CVSS 4.0 qualitative ratings.
/// </summary>
public enum AuditFindingSeverity : byte
{
    /// <summary>Informational — worth noting but no direct exploitability.</summary>
    Info,

    /// <summary>Low — exploitable but requires significant attacker advantage.</summary>
    Low,

    /// <summary>Medium — exploitable under realistic conditions.</summary>
    Medium,

    /// <summary>High — easily exploitable with meaningful impact.</summary>
    High,

    /// <summary>Critical — remotely exploitable with severe mesh-wide impact.</summary>
    Critical,
}

/// <summary>
/// A single security finding produced by <see cref="IAetherSecurityAudit"/>.
/// </summary>
/// <param name="Id">Unique identifier for this finding (stable across re-runs for deduplication).</param>
/// <param name="VulnerabilityClass">The vulnerability class from the BugHunter taxonomy.</param>
/// <param name="Severity">Assessed severity.</param>
/// <param name="AffectedNodeId">UHID of the node exhibiting the vulnerability, or <c>null</c> for protocol-wide findings.</param>
/// <param name="Title">Short title (≤ 80 chars) suitable for a bug report heading.</param>
/// <param name="Description">Detailed description of the finding including reproduction steps.</param>
/// <param name="Recommendation">Concrete recommended remediation.</param>
/// <param name="Evidence">Relevant packet bytes or log excerpts. May be empty.</param>
/// <param name="DetectedAt">UTC timestamp of discovery.</param>
public sealed record AetherAuditFinding(
    string                   Id,
    AetherVulnerabilityClass VulnerabilityClass,
    AuditFindingSeverity     Severity,
    string?                  AffectedNodeId,
    string                   Title,
    string                   Description,
    string                   Recommendation,
    IReadOnlyList<byte[]>    Evidence,
    DateTimeOffset           DetectedAt)
{
    /// <summary><c>true</c> when severity is <see cref="AuditFindingSeverity.High"/> or <see cref="AuditFindingSeverity.Critical"/>.</summary>
    public bool IsHighSeverity =>
        Severity is AuditFindingSeverity.High or AuditFindingSeverity.Critical;
}

/// <summary>
/// Pluggable security audit provider for the Aether mesh protocol.
///
/// <para>
/// Implementations analyse packet captures, node behaviour, or live mesh state
/// and surface findings mapped to the <b>Claude-BugHunter</b> vulnerability
/// taxonomy (<see cref="AetherVulnerabilityClass"/>). Three intended callers:
/// </para>
/// <list type="number">
///   <item>Security researchers running offline audits against packet captures.</item>
///   <item>CI pipelines running static audit passes after integration tests.</item>
///   <item>The AI Security Layer (CircleAI) correlating live telemetry with known CVE patterns.</item>
/// </list>
///
/// <para>
/// Register an implementation via DI. When none is registered,
/// <see cref="NullAetherSecurityAudit"/> is used — all audits return empty finding lists
/// and the mesh operates normally.
/// </para>
///
/// <para>
/// See <c>docs/security/THREAT_MODEL.md</c> for the full mesh attack surface and
/// the mapping to BugHunter <c>hunt-*</c> skills.
/// </para>
/// </summary>
public interface IAetherSecurityAudit
{
    /// <summary>
    /// Analyse a sequence of observed packets for known attack patterns.
    /// Returns all findings, sorted by <see cref="AuditFindingSeverity"/> descending.
    /// </summary>
    /// <param name="packets">Packets to analyse (e.g. a captured session).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<AetherAuditFinding>> AuditPacketsAsync(
        IReadOnlyList<MeshPacket> packets,
        CancellationToken         cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AetherAuditFinding>>([]);

    /// <summary>
    /// Audit the behaviour history of a specific node for Sybil, free-rider,
    /// or reputation-manipulation patterns.
    /// </summary>
    /// <param name="nodeId">UHID of the node to audit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<AetherAuditFinding>> AuditNodeAsync(
        string            nodeId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AetherAuditFinding>>([]);

    /// <summary>
    /// Run a full protocol configuration audit: NodeCapability flags, transport
    /// security settings, encryption parameters, and known misconfiguration patterns.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<AetherAuditFinding>> AuditProtocolConfigAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AetherAuditFinding>>([]);
}

/// <summary>
/// No-op <see cref="IAetherSecurityAudit"/> — returns empty findings for all audits.
/// </summary>
public sealed class NullAetherSecurityAudit : IAetherSecurityAudit
{
    /// <summary>The singleton instance.</summary>
    public static readonly NullAetherSecurityAudit Instance = new();

    private NullAetherSecurityAudit() { }
}
