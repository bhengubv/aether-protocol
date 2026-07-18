// SPDX-License-Identifier: MIT

namespace AetherNet.Content.Download;

/// <summary>
/// A source the <see cref="SegmentedContentDownloader"/> can pull individual chunks from. The mesh
/// implementation (<see cref="MeshChunkSource"/>) issues a chunk request and awaits the reply;
/// callers can supply any implementation (e.g. an HTTP range fetcher or a test double).
/// </summary>
public interface IChunkSource
{
    /// <summary>
    /// Fetch one chunk's bytes. Throw <see cref="ChunkSourceException"/> to signal failure —
    /// transient failures are retried with backoff, permanent ones fail the whole download fast.
    /// </summary>
    Task<byte[]> FetchChunkAsync(string rootHash, int chunkIndex, string? preferredPeer, CancellationToken cancellationToken);
}

/// <summary>A chunk fetch failed. <see cref="Permanent"/> distinguishes fail-fast from retry-with-backoff.</summary>
public sealed class ChunkSourceException : Exception
{
    /// <summary>True = do not retry (fail the download); false = transient, retry with backoff.</summary>
    public bool Permanent { get; }

    public ChunkSourceException(string message, bool permanent = false, Exception? innerException = null)
        : base(message, innerException) => Permanent = permanent;
}
