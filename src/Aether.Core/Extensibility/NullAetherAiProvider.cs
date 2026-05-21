// SPDX-License-Identifier: MIT

using Aether.Protocol;

namespace Aether.Extensibility;

/// <summary>
/// Default (no-op) <see cref="IAetherAiProvider"/> — registered automatically when no
/// CircleAI SDK is present. Acts as the canonical reference implementation of the
/// null-provider contract described on <see cref="IAetherAiProvider"/>.
///
/// <para>
/// <see cref="IsAvailable"/> is always <c>false</c>. All three methods return the
/// same neutral values as the interface's default implementations, but are re-declared
/// here explicitly so the no-op behaviour is immediately visible in source and cannot
/// be accidentally shadowed by a subclass.
/// </para>
///
/// <para>
/// Thread-safe and allocation-free on the hot path — all returns are
/// pre-allocated singletons or <see cref="Task.FromResult{TResult}"/> wrappers.
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
