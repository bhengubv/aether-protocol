// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Text.Json;
using Aether.Constants;
using Aether.Content.Models;
using Aether.Extensibility;
using Aether.Protocol;
using Aether.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aether.Content;

/// <summary>
/// Default content distribution service. Announces and serves content advertised
/// by <see cref="ContentDescriptor"/>, requests chunks from peers, verifies each
/// arrival, and emits a single <see cref="ContentComplete"/> event when the local
/// store has every chunk for a content.
/// </summary>
public sealed class ContentService : IContentService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMeshSender _sender;
    private readonly IRoutingService _routing;
    private readonly IContentStore _store;
    private readonly IAetherIncentiveProvider _incentives;
    private readonly ILogger<ContentService> _logger;

    // ── Chunk Shuffle state ──────────────────────────────────────────────────
    /// <summary>Active shuffle sessions keyed by root hash.</summary>
    private readonly ConcurrentDictionary<string, ChunkShuffleSession> _shuffleSessions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Monotonically-increasing generation counter for bitmap broadcasts.
    /// Incremented on each call to <see cref="BroadcastBitmapAsync"/>.
    /// Stored as <c>int</c> for <see cref="Interlocked"/> compatibility;
    /// cast to <c>uint</c> on the wire.
    /// </summary>
    private int _bitmapGeneration;

    public event EventHandler<ContentDescriptor>? ContentAnnounced;
    public event EventHandler<ChunkArrivedEventArgs>? ChunkReceived;
    public event EventHandler<ContentDescriptor>? ContentComplete;

    public ContentService(
        IMeshSender sender,
        IRoutingService routing,
        IContentStore? store = null,
        IAetherIncentiveProvider? incentives = null,
        ILogger<ContentService>? logger = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        _store = store ?? new InMemoryContentStore();
        _incentives = incentives ?? new DefaultIncentiveProvider();
        _logger = logger ?? NullLogger<ContentService>.Instance;
    }

    public async Task<ContentDescriptor> PublishAsync(string name, byte[] data, string contentType = "application/octet-stream", int chunkSizeBytes = 0, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var descriptor = ContentDescriptor.FromBytes(name, data, contentType, chunkSizeBytes);

        await _store.SaveDescriptorAsync(descriptor, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < descriptor.ChunkCount; i++)
        {
            var start = (long)i * descriptor.ChunkSizeBytes;
            var len = (int)Math.Min(descriptor.ChunkSizeBytes, data.Length - start);
            var chunk = new byte[len];
            Buffer.BlockCopy(data, (int)start, chunk, 0, len);
            await _store.SaveChunkAsync(descriptor.RootHash, i, chunk, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Published content {Name} ({Bytes} bytes, {Chunks} chunks) root={Root}",
            name, descriptor.TotalBytes, descriptor.ChunkCount, descriptor.RootHash);
        return descriptor;
    }

    public async Task AnnounceAsync(ContentDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var payload = new TorrentMetadataPayload
        {
            Descriptor = descriptor,
            SeederUhids = new[] { _sender.LocalUhid },
        };
        var packet = new MeshPacket
        {
            Type = PacketType.TorrentMetadata,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = string.Empty,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 0,
            Payload = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
        };
        await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
    }

    public async Task BroadcastBitmapAsync(string rootHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootHash);

        var descriptor = await _store.GetDescriptorAsync(rootHash, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            _logger.LogDebug("BroadcastBitmapAsync: no descriptor for root={Root} — skipped", rootHash);
            return;
        }

        var have  = await _store.ListChunksAsync(rootHash, cancellationToken).ConfigureAwait(false);
        var flags = new bool[descriptor.ChunkCount];
        foreach (var i in have)
            if ((uint)i < (uint)flags.Length)
                flags[i] = true;

        var generation = unchecked((uint)Interlocked.Increment(ref _bitmapGeneration));

        // Keep the session's have-set in sync so it can build accurate payloads later.
        if (_shuffleSessions.TryGetValue(rootHash, out var existingSession))
        {
            // Session was already created — we don't rebuild it; its internal state
            // already tracks received chunks.  The bitmap we broadcast is built
            // directly from the store (the authoritative source).
            _ = existingSession; // silence unused-variable warning
        }

        var bitmapPayload = new ChunkBitmapPayload
        {
            RootHash   = rootHash,
            ChunkCount = descriptor.ChunkCount,
            HaveBitset = ChunkBitmapPayload.Encode(flags),
            Generation = generation,
        };

        var packet = new MeshPacket
        {
            Type             = PacketType.ChunkBitmap,
            SourceUhid       = _sender.LocalUhid,
            DestinationUhid  = string.Empty,
            Ttl              = ProtocolConstants.DefaultTtl,
            Priority         = 0,
            Payload          = JsonSerializer.SerializeToUtf8Bytes(bitmapPayload, JsonOptions),
        };

        await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("BroadcastBitmap root={Root} have={Have}/{Total} gen={Gen}",
            rootHash, have.Count, descriptor.ChunkCount, generation);
    }

    public async Task RequestChunksAsync(string rootHash, IReadOnlyList<int> chunkIndices, string? peerUhid = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootHash);
        ArgumentNullException.ThrowIfNull(chunkIndices);

        var payload = JsonSerializer.SerializeToUtf8Bytes(new ChunkRequestPayload
        {
            RootHash = rootHash,
            ChunkIndices = chunkIndices,
        }, JsonOptions);

        var packet = new MeshPacket
        {
            Type = PacketType.ChunkRequest,
            SourceUhid = _sender.LocalUhid,
            DestinationUhid = peerUhid ?? string.Empty,
            Ttl = ProtocolConstants.DefaultTtl,
            Priority = 0,
            Payload = payload,
        };

        if (string.IsNullOrEmpty(peerUhid))
        {
            await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var route = await _routing.FindRouteAsync(peerUhid, cancellationToken).ConfigureAwait(false);
            if (route is not null)
                await _sender.SendAsync(packet, route.NextHopUhid, cancellationToken).ConfigureAwait(false);
            else
                await _sender.BroadcastAsync(packet, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task HandleAsync(MeshPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        switch (packet.Type)
        {
            case PacketType.TorrentMetadata:
                await HandleAnnouncementAsync(packet, cancellationToken).ConfigureAwait(false);
                break;
            case PacketType.ChunkRequest:
                await HandleChunkRequestAsync(packet, cancellationToken).ConfigureAwait(false);
                break;
            case PacketType.ChunkData:
                await HandleChunkDataAsync(packet, cancellationToken).ConfigureAwait(false);
                break;
            case PacketType.ChunkBitmap:
                await HandleChunkBitmapAsync(packet, cancellationToken).ConfigureAwait(false);
                break;
            default:
                _logger.LogDebug("ContentService.HandleAsync ignoring non-content packet type {Type}", packet.Type);
                break;
        }
    }

    public async Task<byte[]?> AssembleAsync(string rootHash, CancellationToken cancellationToken = default)
    {
        var descriptor = await _store.GetDescriptorAsync(rootHash, cancellationToken).ConfigureAwait(false);
        if (descriptor is null) return null;

        var chunks = await _store.ListChunksAsync(rootHash, cancellationToken).ConfigureAwait(false);
        if (chunks.Count != descriptor.ChunkCount) return null;

        var assembled = new byte[descriptor.TotalBytes];
        var offset = 0L;
        for (var i = 0; i < descriptor.ChunkCount; i++)
        {
            var bytes = await _store.GetChunkAsync(rootHash, i, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return null;
            Buffer.BlockCopy(bytes, 0, assembled, (int)offset, bytes.Length);
            offset += bytes.Length;
        }
        return assembled;
    }

    private async Task HandleAnnouncementAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        TorrentMetadataPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<TorrentMetadataPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Content: failed to deserialize announcement from packet {Id}", packet.Id);
            return;
        }
        if (body?.Descriptor is null) return;
        if (!body.Descriptor.VerifySelf())
        {
            _logger.LogWarning("Content: announcement {Root} failed self-verification — dropped", body.Descriptor.RootHash);
            return;
        }

        var existing = await _store.GetDescriptorAsync(body.Descriptor.RootHash, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await _store.SaveDescriptorAsync(body.Descriptor, cancellationToken).ConfigureAwait(false);
            ContentAnnounced?.Invoke(this, body.Descriptor);
            _logger.LogDebug("Content announcement received root={Root} chunks={Count}",
                body.Descriptor.RootHash, body.Descriptor.ChunkCount);
        }
    }

    private async Task HandleChunkRequestAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        ChunkRequestPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<ChunkRequestPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Content: failed to deserialize chunk request from packet {Id}", packet.Id);
            return;
        }
        if (body is null || string.IsNullOrEmpty(body.RootHash)) return;

        var descriptor = await _store.GetDescriptorAsync(body.RootHash, cancellationToken).ConfigureAwait(false);
        if (descriptor is null) return;

        var indices = body.ChunkIndices.Count == 0
            ? Enumerable.Range(0, descriptor.ChunkCount).ToArray()
            : body.ChunkIndices.ToArray();

        foreach (var index in indices)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (index < 0 || index >= descriptor.ChunkCount) continue;

            var bytes = await _store.GetChunkAsync(body.RootHash, index, cancellationToken).ConfigureAwait(false);
            if (bytes is null) continue;

            var dataPayload = JsonSerializer.SerializeToUtf8Bytes(new ChunkDataPayload
            {
                RootHash = body.RootHash,
                ChunkIndex = index,
                Data = bytes,
            }, JsonOptions);

            var responsePacket = new MeshPacket
            {
                Type = PacketType.ChunkData,
                SourceUhid = _sender.LocalUhid,
                DestinationUhid = packet.SourceUhid,
                Ttl = ProtocolConstants.DefaultTtl,
                Priority = 0,
                Payload = dataPayload,
            };

            var route = await _routing.FindRouteAsync(packet.SourceUhid, cancellationToken).ConfigureAwait(false);
            if (route is not null)
                await _sender.SendAsync(responsePacket, route.NextHopUhid, cancellationToken).ConfigureAwait(false);
            else
                await _sender.BroadcastAsync(responsePacket, cancellationToken).ConfigureAwait(false);

            await _incentives.RecordRelayAsync(_sender.LocalUhid, responsePacket, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleChunkDataAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        ChunkDataPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<ChunkDataPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Content: failed to deserialize chunk data from packet {Id}", packet.Id);
            return;
        }
        if (body is null || string.IsNullOrEmpty(body.RootHash) || body.Data is null) return;

        var descriptor = await _store.GetDescriptorAsync(body.RootHash, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            _logger.LogDebug("Content: chunk data {Index} arrived for unknown root {Root} — discarded",
                body.ChunkIndex, body.RootHash);
            return;
        }

        var verified = descriptor.VerifyChunk(body.ChunkIndex, body.Data);
        if (!verified)
        {
            _logger.LogWarning("Content: chunk {Index} for root {Root} failed hash check from {Source} — dropped",
                body.ChunkIndex, body.RootHash, packet.SourceUhid);
            ChunkReceived?.Invoke(this, new ChunkArrivedEventArgs
            {
                RootHash = body.RootHash,
                ChunkIndex = body.ChunkIndex,
                Verified = false,
                ContentComplete = false,
            });
            return;
        }

        await _store.SaveChunkAsync(body.RootHash, body.ChunkIndex, body.Data, cancellationToken).ConfigureAwait(false);

        var have = await _store.ListChunksAsync(body.RootHash, cancellationToken).ConfigureAwait(false);
        var complete = have.Count == descriptor.ChunkCount;
        ChunkReceived?.Invoke(this, new ChunkArrivedEventArgs
        {
            RootHash = body.RootHash,
            ChunkIndex = body.ChunkIndex,
            Verified = true,
            ContentComplete = complete,
        });
        if (complete)
        {
            ContentComplete?.Invoke(this, descriptor);
            _logger.LogInformation("Content {Root} fully assembled ({Chunks} chunks)", body.RootHash, descriptor.ChunkCount);
        }

        // ── Chunk Shuffle: coalesced bitmap re-advertisement ──────────────────
        if (_shuffleSessions.TryGetValue(body.RootHash, out var session))
        {
            var shouldBroadcast = session.OnChunkReceived(body.ChunkIndex);
            if (shouldBroadcast && !complete)
            {
                // Re-broadcast availability bitmap so other peers can pull from us.
                // We await but swallow exceptions — a failed bitmap broadcast is
                // cosmetic; the content transfer itself is unaffected.
                try
                {
                    await BroadcastBitmapAsync(body.RootHash, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "BroadcastBitmap after chunk {Index} failed (non-fatal)", body.ChunkIndex);
                }
            }
        }
    }

    /// <summary>
    /// Handle an inbound <see cref="PacketType.ChunkBitmap"/> from a peer.
    ///
    /// <para>
    /// Creates a <see cref="ChunkShuffleSession"/> for the root hash on first
    /// contact (if we know the descriptor), then feeds the peer's bitmap to the
    /// session.  Any chunk assignments returned are executed immediately via
    /// <see cref="RequestChunksAsync"/>.
    /// </para>
    /// </summary>
    private async Task HandleChunkBitmapAsync(MeshPacket packet, CancellationToken cancellationToken)
    {
        ChunkBitmapPayload? body;
        try
        {
            body = JsonSerializer.Deserialize<ChunkBitmapPayload>(packet.Payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Content: failed to deserialise ChunkBitmap from packet {Id}", packet.Id);
            return;
        }

        if (body is null || string.IsNullOrEmpty(body.RootHash) || body.HaveBitset is null)
            return;

        // Only engage if we know this content.
        var descriptor = await _store.GetDescriptorAsync(body.RootHash, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            _logger.LogDebug("ChunkBitmap: no descriptor for root={Root} from {Src} — ignored",
                body.RootHash, packet.SourceUhid);
            return;
        }

        // Get-or-create a shuffle session for this content.
        if (!_shuffleSessions.TryGetValue(body.RootHash, out var session))
        {
            var localHave = await _store.ListChunksAsync(body.RootHash, cancellationToken).ConfigureAwait(false);
            var created = new ChunkShuffleSession(body.RootHash, descriptor.ChunkCount, localHave);
            // GetOrAdd wins the race safely; the loser's object is discarded.
            session = _shuffleSessions.GetOrAdd(body.RootHash, created);
        }

        if (session.IsComplete)
        {
            _logger.LogDebug("ChunkBitmap: root={Root} already complete — no requests issued", body.RootHash);
            return;
        }

        // Use the smaller of the declared ChunkCount and our known ChunkCount to
        // avoid decoding a rogue (oversized) bitset.
        var safeChunkCount = Math.Min(body.ChunkCount, descriptor.ChunkCount);
        var peerHas = ChunkBitmapPayload.Decode(body.HaveBitset, safeChunkCount);

        var assignments = session.OnPeerBitmap(packet.SourceUhid, peerHas, body.Generation);

        foreach (var (peerUhid, indices) in assignments)
        {
            if (indices.Length == 0) continue;
            _logger.LogDebug("ChunkShuffle: requesting {Count} chunk(s) from {Peer} for root={Root}",
                indices.Length, peerUhid, body.RootHash);
            await RequestChunksAsync(body.RootHash, indices, peerUhid, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class DefaultIncentiveProvider : IAetherIncentiveProvider
    {
    }
}
