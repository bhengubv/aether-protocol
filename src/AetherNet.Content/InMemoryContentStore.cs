// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Content.Models;

namespace AetherNet.Content;

/// <summary>
/// Process-local content store. Suitable for tests and demos.
/// </summary>
public sealed class InMemoryContentStore : IContentStore
{
    private readonly ConcurrentDictionary<string, ContentDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte[]>> _chunks = new(StringComparer.OrdinalIgnoreCase);

    public Task SaveDescriptorAsync(ContentDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrEmpty(descriptor.RootHash);
        _descriptors[descriptor.RootHash] = descriptor;
        _chunks.GetOrAdd(descriptor.RootHash, _ => new ConcurrentDictionary<int, byte[]>());
        return Task.CompletedTask;
    }

    public Task<ContentDescriptor?> GetDescriptorAsync(string rootHash, CancellationToken cancellationToken = default)
    {
        _descriptors.TryGetValue(rootHash, out var descriptor);
        return Task.FromResult(descriptor);
    }

    public Task SaveChunkAsync(string rootHash, int chunkIndex, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootHash);
        ArgumentNullException.ThrowIfNull(bytes);
        var dict = _chunks.GetOrAdd(rootHash, _ => new ConcurrentDictionary<int, byte[]>());
        dict[chunkIndex] = bytes;
        return Task.CompletedTask;
    }

    public Task<byte[]?> GetChunkAsync(string rootHash, int chunkIndex, CancellationToken cancellationToken = default)
    {
        if (_chunks.TryGetValue(rootHash, out var dict) && dict.TryGetValue(chunkIndex, out var bytes))
            return Task.FromResult<byte[]?>(bytes);
        return Task.FromResult<byte[]?>(null);
    }

    public Task<IReadOnlyList<int>> ListChunksAsync(string rootHash, CancellationToken cancellationToken = default)
    {
        if (_chunks.TryGetValue(rootHash, out var dict))
        {
            IReadOnlyList<int> indices = dict.Keys.OrderBy(i => i).ToArray();
            return Task.FromResult(indices);
        }
        return Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
    }

    public Task<IReadOnlyList<ContentDescriptor>> ListDescriptorsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ContentDescriptor> all = _descriptors.Values.ToArray();
        return Task.FromResult(all);
    }
}
