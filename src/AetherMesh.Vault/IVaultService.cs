// SPDX-License-Identifier: MIT
using AetherMesh.Vault.Models;

namespace AetherMesh.Vault;

/// <summary>
/// Erasure-coded encrypted distributed backup (aether-vault Phase-2 extension).
///
/// A file is split into K+M shards; any K shards reconstruct it. The
/// in-memory implementation uses simple byte partitioning (no real
/// Reed-Solomon) for testing; production implementations use libfec/RS.
///
/// NodeCapability: <c>aethermesh.vault/v1</c> (<see cref="AetherMesh.Models.NodeCapabilities.Vault"/>).
/// PacketType: <c>VaultShardRequest (42)</c>.
/// </summary>
public interface IVaultService
{
    /// <summary>
    /// Shard and store a stream. Returns the manifest the owner must keep.
    /// </summary>
    Task<VaultManifest> StoreAsync(Stream file, string label, CancellationToken ct = default);

    /// <summary>
    /// Recover a file from its shards. Throws <see cref="InvalidOperationException"/>
    /// if fewer than <see cref="VaultManifest.K"/> shards are available.
    /// </summary>
    Task<Stream> RecoverAsync(VaultManifest manifest, CancellationToken ct = default);

    /// <summary>
    /// Check how many shards are locally available and whether the file is recoverable.
    /// </summary>
    Task<VaultHealth> CheckHealthAsync(VaultManifest manifest, CancellationToken ct = default);

    /// <summary>
    /// Re-replicate shards until <paramref name="targetRedundancy"/> copies exist on
    /// distinct peers. No-op in the in-memory implementation.
    /// </summary>
    Task ReplicateAsync(VaultManifest manifest, int targetRedundancy = 14, CancellationToken ct = default);

    /// <summary>Fired when a mesh peer requests a shard that this node holds.</summary>
    event EventHandler<VaultShardRequest> ShardRequested;
}
