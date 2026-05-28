// SPDX-License-Identifier: MIT

namespace Aether.Extensibility;

// ─────────────────────────────────────────────────────────────────────────────
//  Context memory for the AI layer (mempalace / CircleAI.Memory integration)
//
//  The AI provider (CircleAI / BhenguAI) needs persistent context across sessions
//  to improve its recommendations over time: route history, trust trajectories,
//  observed threat patterns. This interface provides the minimal protocol-layer
//  contract. Full episodic and semantic memory lives in CircleAI.Memory
//  (IEpisodicMemoryStore, IGoalStore) — this is the lightweight bridge.
//
//  mempalace (github.com/bhengubv/mempalace) is the Python reference implementation
//  of the memory patterns. CircleAI.Memory is the C# port. This interface
//  abstracts both.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A single stored memory entry capturing a mesh observation at a point in time.
/// </summary>
/// <param name="Id">Unique identifier for this entry (used to prevent duplicate storage).</param>
/// <param name="NodeId">UHID of the peer this memory entry is about, or <c>null</c> for mesh-wide entries.</param>
/// <param name="Category">Logical grouping: <c>"route"</c>, <c>"trust"</c>, <c>"threat"</c>, <c>"health"</c>, etc.</param>
/// <param name="Content">The observation encoded as a plain-text or JSON summary.</param>
/// <param name="Importance">Relative importance from 0.0 (trivial) to 1.0 (critical). Used for eviction ordering.</param>
/// <param name="RecordedAt">UTC timestamp of the observation.</param>
public sealed record AetherMemoryEntry(
    string         Id,
    string?        NodeId,
    string         Category,
    string         Content,
    double         Importance,
    DateTimeOffset RecordedAt);

/// <summary>
/// Lightweight episodic memory store for the Aether AI layer. Enables
/// <see cref="IAetherAiProvider"/> implementations to persist mesh observations
/// across process restarts and build better long-term recommendations.
///
/// <para>
/// This interface is purposely minimal — it is a <em>protocol layer</em> contract.
/// Full-featured episodic, semantic, and goal memory lives in CircleAI.Memory
/// (<c>IEpisodicMemoryStore</c>, <c>IGoalStore</c>, <c>IPersonaStore</c>) and in
/// the mempalace Python reference implementation.
/// </para>
///
/// <para>
/// Register an implementation via DI. When no implementation is registered,
/// <see cref="NullAetherContextMemory"/> is used and no context persists between
/// sessions — the AI provider starts fresh each time.
/// </para>
/// </summary>
public interface IAetherContextMemory
{
    /// <summary>
    /// Stores an observation. Implementations should deduplicate by
    /// <see cref="AetherMemoryEntry.Id"/> and update existing entries if the same
    /// observation arrives again (e.g. a trust score update for the same node).
    /// </summary>
    Task StoreAsync(AetherMemoryEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves recent entries for a specific node, ordered by
    /// <see cref="AetherMemoryEntry.RecordedAt"/> descending (newest first).
    /// Returns an empty list when the node is unknown.
    /// </summary>
    /// <param name="nodeId">UHID of the peer to query.</param>
    /// <param name="limit">Maximum number of entries to return. Defaults to 20.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<AetherMemoryEntry>> RecallAsync(
        string            nodeId,
        int               limit              = 20,
        CancellationToken cancellationToken  = default);

    /// <summary>
    /// Retrieves recent entries across all nodes for a given category (e.g. <c>"threat"</c>),
    /// ordered by <see cref="AetherMemoryEntry.Importance"/> descending, then by
    /// <see cref="AetherMemoryEntry.RecordedAt"/> descending.
    /// </summary>
    /// <param name="category">Category filter (e.g. <c>"route"</c>, <c>"trust"</c>, <c>"threat"</c>).</param>
    /// <param name="limit">Maximum number of entries to return. Defaults to 50.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<AetherMemoryEntry>> RecallByCategoryAsync(
        string            category,
        int               limit              = 50,
        CancellationToken cancellationToken  = default);

    /// <summary>
    /// Removes entries older than <paramref name="cutoff"/> with importance below
    /// <paramref name="importanceThreshold"/>. Called periodically to bound storage
    /// growth on memory-constrained mesh nodes.
    /// </summary>
    Task PruneAsync(
        DateTimeOffset    cutoff,
        double            importanceThreshold = 0.3,
        CancellationToken cancellationToken   = default);
}

/// <summary>
/// No-op <see cref="IAetherContextMemory"/> — used when no memory backend is
/// registered. All stores are silently discarded; recalls return empty lists.
/// </summary>
public sealed class NullAetherContextMemory : IAetherContextMemory
{
    /// <summary>The singleton instance.</summary>
    public static readonly NullAetherContextMemory Instance = new();

    private NullAetherContextMemory() { }

    /// <inheritdoc/>
    public Task StoreAsync(AetherMemoryEntry entry, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<IReadOnlyList<AetherMemoryEntry>> RecallAsync(
        string nodeId, int limit = 20, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AetherMemoryEntry>>([]);

    /// <inheritdoc/>
    public Task<IReadOnlyList<AetherMemoryEntry>> RecallByCategoryAsync(
        string category, int limit = 50, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AetherMemoryEntry>>([]);

    /// <inheritdoc/>
    public Task PruneAsync(
        DateTimeOffset cutoff, double importanceThreshold = 0.3, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
