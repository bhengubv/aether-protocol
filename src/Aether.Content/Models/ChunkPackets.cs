// SPDX-License-Identifier: MIT

namespace Aether.Content.Models;

/// <summary>
/// Cross-language stable wire payload for <see cref="Aether.Protocol.PacketType.ChunkRequest"/>.
/// JSON-encoded with snake_case names. Receiver replies with a matching
/// <see cref="ChunkDataPayload"/> per requested chunk.
/// </summary>
public sealed class ChunkRequestPayload
{
    /// <summary>Root hash identifying the content.</summary>
    public string RootHash { get; set; } = string.Empty;

    /// <summary>Specific chunk indices the requester wants. Empty means "all chunks".</summary>
    public IReadOnlyList<int> ChunkIndices { get; set; } = Array.Empty<int>();
}

/// <summary>
/// Cross-language stable wire payload for <see cref="Aether.Protocol.PacketType.ChunkData"/>.
/// One chunk per packet — keeps responses simple and friendly to BLE MTU limits.
/// </summary>
public sealed class ChunkDataPayload
{
    /// <summary>Root hash this chunk belongs to.</summary>
    public string RootHash { get; set; } = string.Empty;

    /// <summary>Index of this chunk within the content.</summary>
    public int ChunkIndex { get; set; }

    /// <summary>Raw chunk bytes (the receiver verifies via <see cref="ContentDescriptor.VerifyChunk"/>).</summary>
    public byte[] Data { get; set; } = [];
}

/// <summary>
/// Cross-language stable wire payload for <see cref="Aether.Protocol.PacketType.TorrentMetadata"/>.
/// Carries a serialized <see cref="ContentDescriptor"/> so a peer can advertise content
/// before any chunk transfer begins.
/// </summary>
public sealed class TorrentMetadataPayload
{
    /// <summary>The content descriptor being advertised.</summary>
    public ContentDescriptor Descriptor { get; set; } = new();

    /// <summary>UHID(s) currently known to hold this content. Receivers can pull from any of them.</summary>
    public IReadOnlyList<string> SeederUhids { get; set; } = Array.Empty<string>();
}
