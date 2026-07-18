// SPDX-License-Identifier: MIT

using AetherNet.BitTorrent.Metainfo;
using AetherNet.Content;
using AetherNet.Content.Models;

namespace AetherNet.BitTorrent.Gateway;

/// <summary>
/// Bridges BitTorrent content into (and out of) the AetherNet mesh.
///
/// <para><b>Forward (swarm → mesh):</b> a completed BitTorrent download — verified against its
/// SHA-1 pieces by the peer-wire layer — is translated into AetherNet content: split into
/// SHA-256-addressed chunks (<see cref="ContentDescriptor"/>) and written to the content store, so
/// offline peers can pull and reassemble it over AetherNet's existing chunk protocol. The two
/// systems don't share hashes (BitTorrent v1 = SHA-1 pieces; AetherNet = SHA-256 chunks), so this
/// is a genuine re-hash / re-chunk over the same underlying bytes — never a relabel.</para>
///
/// <para><b>Reverse (mesh → swarm):</b> mesh content is reassembled from its chunks and packaged as
/// a real <c>.torrent</c> (SHA-1 pieces) so a gateway node can seed it into a BitTorrent swarm.</para>
///
/// <para>Feature-flagged off by default at the host (<c>AETHERNET_TORRENT_INGEST</c>); this type is
/// the mechanism the host drives once ingest is enabled.</para>
/// </summary>
public static class TorrentMeshGateway
{
    /// <summary>
    /// Forward bridge against the content store directly: translate a completed torrent's bytes into
    /// AetherNet chunks and persist the descriptor + every chunk under its root hash. Returns the
    /// descriptor for the host to announce (e.g. via <see cref="IContentService.AnnounceAsync"/>).
    /// </summary>
    public static async Task<ContentDescriptor> IngestAsync(
        IContentStore store,
        string name,
        byte[] torrentBytes,
        string contentType = "application/octet-stream",
        int chunkSizeBytes = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(torrentBytes);

        var descriptor = ContentDescriptor.FromBytes(name, torrentBytes, contentType, chunkSizeBytes);
        await store.SaveDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false);

        int size = descriptor.ChunkSizeBytes;
        for (int i = 0; i < descriptor.ChunkCount; i++)
        {
            long start = (long)i * size;
            int len = (int)Math.Min(size, torrentBytes.Length - start);
            var chunk = new byte[len];
            Array.Copy(torrentBytes, start, chunk, 0, len);
            await store.SaveChunkAsync(descriptor.RootHash, i, chunk, cancellationToken).ConfigureAwait(false);
        }
        return descriptor;
    }

    /// <summary>
    /// Forward bridge over the live mesh: publish the torrent's bytes through
    /// <see cref="IContentService"/> (which chunks + stores them) and announce the descriptor to
    /// peers so offline nodes can pull it.
    /// </summary>
    public static async Task<ContentDescriptor> IngestAsync(
        IContentService content,
        string name,
        byte[] torrentBytes,
        string contentType = "application/octet-stream",
        int chunkSizeBytes = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(torrentBytes);

        var descriptor = await content.PublishAsync(name, torrentBytes, contentType, chunkSizeBytes, cancellationToken).ConfigureAwait(false);
        await content.AnnounceAsync(descriptor, cancellationToken).ConfigureAwait(false);
        return descriptor;
    }

    /// <summary>
    /// Reassemble the original bytes from the mesh chunks of <paramref name="rootHash"/>, verifying
    /// each chunk against the descriptor. Returns null if the descriptor or any chunk is missing or
    /// fails verification. The receiving/offline node's step before local use or re-seeding.
    /// </summary>
    public static async Task<byte[]?> AssembleFromStoreAsync(
        IContentStore store,
        string rootHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var descriptor = await store.GetDescriptorAsync(rootHash, cancellationToken).ConfigureAwait(false);
        if (descriptor is null) return null;

        var output = new byte[descriptor.TotalBytes];
        long offset = 0;
        for (int i = 0; i < descriptor.ChunkCount; i++)
        {
            var chunk = await store.GetChunkAsync(rootHash, i, cancellationToken).ConfigureAwait(false);
            if (chunk is null || !descriptor.VerifyChunk(i, chunk)) return null;
            Array.Copy(chunk, 0, output, offset, chunk.Length);
            offset += chunk.Length;
        }
        return offset == descriptor.TotalBytes ? output : null;
    }

    /// <summary>
    /// Reverse bridge: package mesh content bytes as a real single-file <c>.torrent</c> (SHA-1
    /// pieces) so a gateway node can seed it into a BitTorrent swarm.
    /// </summary>
    public static byte[] ExportAsTorrent(string name, ReadOnlySpan<byte> content, int pieceLength = 262144, string? announce = null)
        => TorrentBuilder.CreateSingleFile(name, content, pieceLength, announce);

    /// <summary>
    /// The content-identity bridge: map a parsed BitTorrent metainfo (SHA-1 identity) to the
    /// AetherNet mesh descriptor (SHA-256 identity) over the SAME underlying file bytes, asserting
    /// the byte lengths agree. Proves both content identities coexist over one file.
    /// </summary>
    public static ContentDescriptor MapToMeshDescriptor(TorrentMetainfo torrent, byte[] assembledBytes, int chunkSizeBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(torrent);
        ArgumentNullException.ThrowIfNull(assembledBytes);
        if (assembledBytes.LongLength != torrent.TotalLength)
            throw new ArgumentException($"assembled byte length {assembledBytes.LongLength} != torrent total length {torrent.TotalLength}");
        return ContentDescriptor.FromBytes(torrent.Name, assembledBytes, "application/octet-stream", chunkSizeBytes);
    }
}
