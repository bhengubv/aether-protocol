// SPDX-License-Identifier: MIT

using Aether.Protocol;

namespace Aether.Extensibility;

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
/// Default no-op implementations make every method safe to call when no AI
/// is installed. <c>IsAvailable</c> is the gate: callers must check it before
/// trusting any returned data.
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
/// correctly when <see cref="IsAvailable"/> is <c>false</c>, and every method
/// provides a safe no-op default so callers do not need null-guards on the provider.
/// </para>
/// </summary>
public interface IAetherAiProvider
{
    /// <summary>
    /// Whether this AI provider is loaded and operational on this device.
    /// When <c>false</c>, callers should skip all other methods.
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
}
