// SPDX-License-Identifier: MIT

using Aether.Protocol;

namespace Aether.Extensibility;

// ── Supporting types (BhenguAI health monitoring) ─────────────────────────────

/// <summary>
/// Aggregate health of the mesh as assessed by BhenguAI / CircleAI.
/// Returned by <see cref="IAetherAiProvider.GetNetworkHealthAsync"/>.
///
/// <para>
/// <see cref="OverallScore"/> ranges from 0.0 (severely degraded) to 1.0 (fully healthy).
/// The AI computes this from node trust scores, route stability, and observed
/// congestion — callers may use it as a single-number signal for dashboards or
/// adaptive behaviour (e.g. fall back to DTN when score drops below 0.4).
/// </para>
/// </summary>
/// <param name="OverallScore">Composite health score: 0.0–1.0.</param>
/// <param name="TrustedNodeCount">Number of peers with trust score ≥ 0.7.</param>
/// <param name="SuspiciousNodeCount">Number of peers with trust score &lt; 0.3 or under AI monitoring.</param>
/// <param name="Summary">Human-readable summary sentence from the AI.</param>
/// <param name="GeneratedAt">UTC time the AI produced this report.</param>
public sealed record AiNetworkHealthReport(
    double         OverallScore,
    int            TrustedNodeCount,
    int            SuspiciousNodeCount,
    string         Summary,
    DateTimeOffset GeneratedAt)
{
    /// <summary><c>true</c> when <see cref="OverallScore"/> is within the valid 0–1 range.</summary>
    public bool IsValid => OverallScore is >= 0.0 and <= 1.0;

    /// <summary>A neutral placeholder returned when AI is unavailable.</summary>
    public static readonly AiNetworkHealthReport Unavailable =
        new(1.0, 0, 0, "AI health monitoring unavailable.", DateTimeOffset.UtcNow);
}

// ── Supporting types ──────────────────────────────────────────────────────────

/// <summary>
/// AI confidence levels for threat assessment.
/// </summary>
public enum AiThreatLevel : byte
{
    /// <summary>Packet is clean — forward normally.</summary>
    None   = 0,

    /// <summary>Anomalous but non-critical — log and forward.</summary>
    Low    = 1,

    /// <summary>Suspicious — suppress forwarding and log the packet.</summary>
    Medium = 2,

    /// <summary>Confirmed threat — drop the packet and emit a diagnostic event.</summary>
    High   = 3,
}

/// <summary>
/// An AI-suggested hop path to a destination.
/// </summary>
/// <param name="Path">
/// Ordered list of UHID hops (destination-exclusive; the final hop is the destination).
/// </param>
/// <param name="Confidence">AI confidence in this path, ranging from 0.0 to 1.0.</param>
public sealed record AiRouteSuggestion(
    IReadOnlyList<string> Path,
    double Confidence);

/// <summary>
/// Extension point for AI-enhanced mesh operations (CircleAI).
///
/// Three integration points:
/// <list type="number">
///   <item>
///     <description>
///       <see cref="SuggestRoutesAsync"/> — predictive pre-routing hints produced
///       before AODV floods the mesh; an empty list means standard AODV proceeds.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="GetTransportBiasesAsync"/> — per-transport score multipliers applied
///       on top of Kalman ranking; 1.0 is neutral.
///     </description>
///   </item>
///   <item>
///     <description>
///       <see cref="AssessThreatAsync"/> — per-packet anomaly detection that forms the
///       AI security layer; returns <see cref="AiThreatLevel.None"/> when clean.
///     </description>
///   </item>
/// </list>
///
/// <para>
/// The AI is a "magic potion": it enhances but never blocks. Aether must operate
/// correctly when <see cref="IsAvailable"/> is <c>false</c>.
/// </para>
///
/// <para><b>Null-provider contract (important for implementors and callers):</b></para>
/// <para>
/// Every method on this interface ships with a default implementation that is safe
/// to call unconditionally — it returns the neutral value (empty list, empty
/// dictionary, <see cref="AiThreatLevel.None"/>) without throwing. The
/// <see cref="IsAvailable"/> check is therefore a <em>performance optimisation</em>,
/// not a correctness requirement: callers that skip the check in non-hot paths
/// are still correct, merely slightly less efficient. Implementations that set
/// <see cref="IsAvailable"/> to <c>true</c> are responsible for honouring this
/// same contract — methods must never throw when the provider is initialised but
/// temporarily degraded. <see cref="NullAetherAiProvider"/> is the registered
/// default and serves as the canonical reference implementation of this contract.
/// </para>
/// </summary>
public interface IAetherAiProvider
{
    /// <summary>
    /// Whether this AI provider is loaded and fully operational on this device.
    /// <c>false</c> means no AI intelligence is available (e.g. CircleAI SDK not
    /// installed, not licenced, or still initialising). When <c>false</c>, callers
    /// <em>may</em> skip the other methods as an optimisation; all methods still
    /// return safe neutral values via their default implementations.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Returns AI-predicted route candidates for a destination, optionally
    /// short-circuiting AODV discovery. An empty list means standard AODV proceeds.
    /// </summary>
    /// <param name="destinationUhid">UHID of the intended destination node.</param>
    /// <param name="payloadBytes">Size of the payload in bytes; used for path-capacity estimation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A (possibly empty) read-only list of <see cref="AiRouteSuggestion"/> values, each
    /// containing an ordered hop path and a confidence score. Callers may sort by
    /// <see cref="AiRouteSuggestion.Confidence"/> descending to pick the best candidate.
    /// </returns>
    Task<IReadOnlyList<AiRouteSuggestion>> SuggestRoutesAsync(
        string destinationUhid,
        int payloadBytes,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AiRouteSuggestion>>(Array.Empty<AiRouteSuggestion>());

    /// <summary>
    /// Returns per-transport score multipliers (transport name → multiplier).
    /// <list type="bullet">
    ///   <item><description>1.0 = neutral (Kalman score unchanged).</description></item>
    ///   <item><description>&gt;1.0 = AI-preferred transport; score amplified.</description></item>
    ///   <item><description>&lt;1.0 = AI-discouraged transport; score reduced.</description></item>
    ///   <item><description>0.0 = effectively suppress (score zeroed).</description></item>
    /// </list>
    /// An empty dictionary means Kalman scores are used unmodified.
    /// </summary>
    /// <param name="payloadBytes">Size of the payload in bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A (possibly empty) read-only dictionary mapping transport names to multipliers.
    /// </returns>
    Task<IReadOnlyDictionary<string, double>> GetTransportBiasesAsync(
        int payloadBytes,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, double>>(
               new Dictionary<string, double>());

    /// <summary>
    /// Assesses whether an incoming packet represents a threat.
    /// Returns <see cref="AiThreatLevel.None"/> when AI is unavailable or the packet is clean.
    /// </summary>
    /// <remarks>
    /// Callers should suppress forwarding for <see cref="AiThreatLevel.Medium"/> or higher.
    /// A convenient guard is: <c>if (level &gt;= AiThreatLevel.Medium) suppress;</c>
    /// </remarks>
    /// <param name="packet">The incoming <see cref="MeshPacket"/> to assess.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The assessed <see cref="AiThreatLevel"/> for the packet.
    /// </returns>
    Task<AiThreatLevel> AssessThreatAsync(
        MeshPacket packet,
        CancellationToken cancellationToken = default)
        => Task.FromResult(AiThreatLevel.None);

    /// <summary>
    /// Returns an aggregate health report for the current mesh state, as assessed
    /// by BhenguAI / CircleAI from accumulated telemetry.
    ///
    /// <para>
    /// The report combines node trust scores, route stability, congestion, and
    /// any active security posture into a single <see cref="AiNetworkHealthReport.OverallScore"/>
    /// (0.0 = severely degraded, 1.0 = fully healthy). It is intended for dashboards,
    /// adaptive behaviour (e.g. switch to DTN when score is low), and incident response.
    /// </para>
    ///
    /// <para>
    /// Returns <see cref="AiNetworkHealthReport.Unavailable"/> when AI is not active —
    /// callers need not check <see cref="IsAvailable"/> first.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<AiNetworkHealthReport> GetNetworkHealthAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(AiNetworkHealthReport.Unavailable);
}
