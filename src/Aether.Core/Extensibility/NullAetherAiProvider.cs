// SPDX-License-Identifier: MIT

using Aether.Protocol;

namespace Aether.Extensibility;

/// <summary>
/// No-op <see cref="IAetherAiProvider"/> — registered by default when no AI SDK is present.
///
/// <para>
/// All methods return safe defaults. <see cref="IsAvailable"/> is always <c>false</c>,
/// signalling to callers that no AI intelligence is available on this node.
/// </para>
/// <para>
/// This class implements every method explicitly so that the no-op behaviour is
/// transparent and cannot be accidentally overridden by a derived class.
/// </para>
/// </summary>
public sealed class NullAetherAiProvider : IAetherAiProvider
{
    /// <summary>
    /// Always <c>false</c>. Callers should skip all other AI methods when this is <c>false</c>.
    /// </summary>
    public bool IsAvailable => false;

    /// <summary>
    /// Returns an empty list. Standard AODV route discovery proceeds normally.
    /// </summary>
    public Task<IReadOnlyList<AiRouteSuggestion>> SuggestRoutesAsync(
        string destinationUhid,
        int payloadBytes,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AiRouteSuggestion>>(Array.Empty<AiRouteSuggestion>());

    /// <summary>
    /// Returns an empty dictionary. Kalman scores are used unmodified.
    /// </summary>
    public Task<IReadOnlyDictionary<string, double>> GetTransportBiasesAsync(
        int payloadBytes,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyDictionary<string, double>>(new Dictionary<string, double>());

    /// <summary>
    /// Returns <see cref="AiThreatLevel.None"/>. All packets are treated as clean.
    /// </summary>
    public Task<AiThreatLevel> AssessThreatAsync(
        MeshPacket packet,
        CancellationToken cancellationToken = default)
        => Task.FromResult(AiThreatLevel.None);
}
