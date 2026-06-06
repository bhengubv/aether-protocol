// SPDX-License-Identifier: MIT

using AetherMesh.Content.Models;
using AetherMesh.Protocol;

namespace AetherMesh.Content;

/// <summary>
/// Chunked content distribution. Senders publish a <see cref="ContentDescriptor"/>
/// (which advertises the available chunks) and serve <see cref="PacketType.ChunkRequest"/>
/// packets on demand. Receivers issue chunk requests, verify each
/// <see cref="PacketType.ChunkData"/> response against the descriptor's chunk hash,
/// and assemble the final byte stream.
/// </summary>
public interface IContentService
{
    /// <summary>Raised when a peer announces content we did not previously know about.</summary>
    event EventHandler<ContentDescriptor>? ContentAnnounced;

    /// <summary>Raised when a chunk we requested arrives and verifies.</summary>
    event EventHandler<ChunkArrivedEventArgs>? ChunkReceived;

    /// <summary>Raised when every chunk for a descriptor is present locally and verifies.</summary>
    event EventHandler<ContentDescriptor>? ContentComplete;

    /// <summary>
    /// Publish content from a byte buffer. Computes the descriptor, persists every
    /// chunk locally, and returns the descriptor so the caller can hand it to
    /// peers (e.g. via <see cref="AnnounceAsync"/>).
    /// </summary>
    Task<ContentDescriptor> PublishAsync(string name, byte[] data, string contentType = "application/octet-stream", int chunkSizeBytes = 0, CancellationToken cancellationToken = default);

    /// <summary>Broadcast a <see cref="PacketType.TorrentMetadata"/> packet announcing a descriptor.</summary>
    Task AnnounceAsync(ContentDescriptor descriptor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcast a <see cref="PacketType.ChunkBitmap"/> packet advertising which chunks
    /// of <paramref name="rootHash"/> this node currently holds.
    ///
    /// <para>
    /// Call this after <see cref="AnnounceAsync"/> (as a seeder) or after receiving
    /// <see cref="ContentAnnounced"/> (as a leecher) to opt into the Chunk Shuffle
    /// protocol. Peers that receive the bitmap will automatically issue targeted
    /// <see cref="PacketType.ChunkRequest"/> packets for chunks they lack.
    /// </para>
    ///
    /// <para>
    /// <see cref="ContentService"/> also calls this automatically after each coalescing
    /// batch of chunks received, so callers only need to trigger the initial broadcast.
    /// </para>
    /// </summary>
    Task BroadcastBitmapAsync(string rootHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Request specific chunks of a content from a peer (or any peer, if <paramref name="peerUhid"/> is null —
    /// the request is broadcast). The receiver verifies each arriving chunk and stores it locally; the
    /// <see cref="ChunkReceived"/> and <see cref="ContentComplete"/> events fire as appropriate.
    /// </summary>
    Task RequestChunksAsync(string rootHash, IReadOnlyList<int> chunkIndices, string? peerUhid = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pump an inbound content packet into the service.
    /// Handled types: <see cref="PacketType.TorrentMetadata"/>,
    /// <see cref="PacketType.ChunkRequest"/>, <see cref="PacketType.ChunkData"/>,
    /// <see cref="PacketType.ChunkBitmap"/>.
    /// </summary>
    Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default);

    /// <summary>Reassemble content from the local store. Returns null if any chunk is missing.</summary>
    Task<byte[]?> AssembleAsync(string rootHash, CancellationToken cancellationToken = default);
}

/// <summary>Event payload for <see cref="IContentService.ChunkReceived"/>.</summary>
public sealed class ChunkArrivedEventArgs : EventArgs
{
    public string RootHash { get; init; } = string.Empty;
    public int ChunkIndex { get; init; }
    public bool Verified { get; init; }
    public bool ContentComplete { get; init; }
}
