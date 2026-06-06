// SPDX-License-Identifier: MIT

using AetherMesh.Content.Models;

namespace AetherMesh.Content;

/// <summary>
/// Persistence boundary for content descriptors and chunks. Hosts that want
/// content to survive a restart supply a real implementation; the default
/// <see cref="InMemoryContentStore"/> is volatile and process-local.
///
/// Stores are keyed by descriptor <see cref="ContentDescriptor.RootHash"/>:
/// content is content-addressed, so the root hash identifies it uniquely.
/// </summary>
public interface IContentStore
{
    /// <summary>Save (or replace) a descriptor under its root hash.</summary>
    Task SaveDescriptorAsync(ContentDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>Returns the descriptor for the given root hash, or null if absent.</summary>
    Task<ContentDescriptor?> GetDescriptorAsync(string rootHash, CancellationToken cancellationToken = default);

    /// <summary>Persist a verified chunk under <c>(rootHash, chunkIndex)</c>.</summary>
    Task SaveChunkAsync(string rootHash, int chunkIndex, byte[] bytes, CancellationToken cancellationToken = default);

    /// <summary>Returns the bytes of a stored chunk, or null if absent.</summary>
    Task<byte[]?> GetChunkAsync(string rootHash, int chunkIndex, CancellationToken cancellationToken = default);

    /// <summary>Returns the indices of every chunk currently stored for <paramref name="rootHash"/>.</summary>
    Task<IReadOnlyList<int>> ListChunksAsync(string rootHash, CancellationToken cancellationToken = default);

    /// <summary>Enumerate every descriptor in the store.</summary>
    Task<IReadOnlyList<ContentDescriptor>> ListDescriptorsAsync(CancellationToken cancellationToken = default);
}
