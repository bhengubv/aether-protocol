// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Content;
using AetherNet.Content.Models;
using AetherNet.Core.Tests.Fakes;
using AetherNet.Protocol;
using AetherNet.Routing;
using Xunit;

namespace AetherNet.Core.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Part 1 — ChunkBitmapPayload encoding / decoding
// ─────────────────────────────────────────────────────────────────────────────

public class ChunkBitmapPayloadTests
{
    // ── Encode ────────────────────────────────────────────────────────────────

    [Fact]
    public void Encode_SparseSet_CorrectByte()
    {
        // Chunks 0, 2, 5 of 8 total.
        // Bit 0 → 1, bit 2 → 4, bit 5 → 32  →  1 + 4 + 32 = 37 = 0x25
        var flags = new bool[8];
        flags[0] = flags[2] = flags[5] = true;

        var bitset = ChunkBitmapPayload.Encode(flags);

        Assert.Single(bitset);
        Assert.Equal(0x25, bitset[0]);
    }

    [Fact]
    public void Encode_AllTrue_16Chunks_TwoFullBytes()
    {
        var flags = Enumerable.Repeat(true, 16).ToArray();
        var bitset = ChunkBitmapPayload.Encode(flags);
        Assert.Equal(2, bitset.Length);
        Assert.Equal(0xFF, bitset[0]);
        Assert.Equal(0xFF, bitset[1]);
    }

    [Fact]
    public void Encode_EmptyFlags_ReturnsEmptyArray()
    {
        var bitset = ChunkBitmapPayload.Encode([]);
        Assert.Empty(bitset);
    }

    [Fact]
    public void Encode_NineChunks_TwoBytes_TrailingBitZero()
    {
        // 9 chunks → 2 bytes; chunk 8 is bit 0 of byte 1
        var flags = new bool[9];
        flags[8] = true;         // only last chunk
        var bitset = ChunkBitmapPayload.Encode(flags);
        Assert.Equal(2, bitset.Length);
        Assert.Equal(0x00, bitset[0]);
        Assert.Equal(0x01, bitset[1]);
    }

    // ── Decode ────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_RoundTrips_SparseSet()
    {
        var original = new bool[8];
        original[0] = original[2] = original[5] = true;

        var bitset  = ChunkBitmapPayload.Encode(original);
        var decoded = ChunkBitmapPayload.Decode(bitset, 8);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public void Decode_MoreChunksThanBits_TrailingAreFalse()
    {
        // 1 byte encodes 8 bits, but we claim 16 chunks — trailing 8 must be false
        var bitset  = new byte[] { 0xFF };
        var decoded = ChunkBitmapPayload.Decode(bitset, 16);

        Assert.Equal(16, decoded.Length);
        Assert.All(decoded[0..8], b => Assert.True(b));
        Assert.All(decoded[8..16], b => Assert.False(b));
    }

    [Fact]
    public void Decode_ZeroChunkCount_ReturnsEmpty()
    {
        var decoded = ChunkBitmapPayload.Decode(new byte[] { 0xFF }, 0);
        Assert.Empty(decoded);
    }

    // ── HasAll ────────────────────────────────────────────────────────────────

    [Fact]
    public void HasAll_AllBitsSet_ReturnsTrue()
    {
        var payload = new ChunkBitmapPayload
        {
            ChunkCount = 4,
            HaveBitset = new byte[] { 0x0F },  // bits 0-3 set
        };
        Assert.True(payload.HasAll());
    }

    [Fact]
    public void HasAll_SomeBitsClear_ReturnsFalse()
    {
        var payload = new ChunkBitmapPayload
        {
            ChunkCount = 4,
            HaveBitset = new byte[] { 0x07 },  // only bits 0-2
        };
        Assert.False(payload.HasAll());
    }

    [Fact]
    public void HasAll_ZeroChunks_ReturnsTrue()
    {
        var payload = new ChunkBitmapPayload { ChunkCount = 0, HaveBitset = [] };
        Assert.True(payload.HasAll());
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Part 2 — ChunkShuffleSession coordinator
// ─────────────────────────────────────────────────────────────────────────────

public class ChunkShuffleSessionTests
{
    private static bool[] AllTrue(int n)  => Enumerable.Repeat(true,  n).ToArray();
    private static bool[] AllFalse(int n) => Enumerable.Repeat(false, n).ToArray();

    // ── Basic assignment ──────────────────────────────────────────────────────

    [Fact]
    public void OnPeerBitmap_FullPeer_AssignsCappedAtMaxConcurrent()
    {
        var session = new ChunkShuffleSession("root", 20);
        var assignments = session.OnPeerBitmap("peer-a", AllTrue(20), generation: 1);

        var total = assignments.SelectMany(a => a.ChunkIndices).ToArray();
        Assert.Equal(ProtocolConstants.MaxConcurrentChunkTransfers, total.Length);
    }

    [Fact]
    public void OnPeerBitmap_TwoPeers_AssignsNonOverlappingChunks()
    {
        // 20 chunks, two full peers, each can get up to MaxConcurrent=4 → 8 unique
        var session = new ChunkShuffleSession("root", 20);

        var aAssign = session.OnPeerBitmap("peer-a", AllTrue(20), 1);
        var bAssign = session.OnPeerBitmap("peer-b", AllTrue(20), 1);

        var all = aAssign.Concat(bAssign)
                         .SelectMany(a => a.ChunkIndices)
                         .ToArray();

        // No chunk appears more than once across all peer assignments
        Assert.Equal(all.Distinct().Count(), all.Length);
        // Both peers got their MaxConcurrent slots
        Assert.Equal(ProtocolConstants.MaxConcurrentChunkTransfers,
                     aAssign.Sum(a => a.ChunkIndices.Length));
        Assert.Equal(ProtocolConstants.MaxConcurrentChunkTransfers,
                     bAssign.Sum(a => a.ChunkIndices.Length));
    }

    [Fact]
    public void OnPeerBitmap_LocalHaveExcluded_NeverRequested()
    {
        // We already have chunks 0-3; peer has all 8 → we should only request 4-7
        var have    = new[] { 0, 1, 2, 3 };
        var session = new ChunkShuffleSession("root", 8, localHave: have);
        var peerHas = AllTrue(8);

        var assignments = session.OnPeerBitmap("peer-a", peerHas, 1);
        var requested   = assignments.SelectMany(a => a.ChunkIndices).ToArray();

        Assert.All(requested, idx => Assert.DoesNotContain(idx, have));
        Assert.All(requested, idx => Assert.InRange(idx, 4, 7));
    }

    [Fact]
    public void OnPeerBitmap_PeerMissingChunk_NotRequested()
    {
        // Peer only has chunks 0 and 1 out of 10 → only those two are candidates
        var peerHas = new bool[10];
        peerHas[0] = peerHas[1] = true;

        var session     = new ChunkShuffleSession("root", 10);
        var assignments = session.OnPeerBitmap("peer-a", peerHas, 1);
        var requested   = assignments.SelectMany(a => a.ChunkIndices).OrderBy(i => i).ToArray();

        Assert.Equal(new[] { 0, 1 }, requested);
    }

    [Fact]
    public void OnPeerBitmap_NoPeerHasNeededChunk_ReturnsEmpty()
    {
        var session = new ChunkShuffleSession("root", 4);
        // Peer has nothing
        var assignments = session.OnPeerBitmap("peer-a", AllFalse(4), 1);
        Assert.Empty(assignments);
    }

    [Fact]
    public void OnPeerBitmap_SessionAlreadyComplete_ReturnsEmpty()
    {
        var session = new ChunkShuffleSession("root", 4, localHave: new[] { 0, 1, 2, 3 });
        Assert.True(session.IsComplete);

        var assignments = session.OnPeerBitmap("peer-a", AllTrue(4), 1);
        Assert.Empty(assignments);
    }

    // ── Generation guard ──────────────────────────────────────────────────────

    [Fact]
    public void OnPeerBitmap_StaleGeneration_Discarded()
    {
        var session = new ChunkShuffleSession("root", 8);
        var allTrue = AllTrue(8);

        // First call with gen=5 takes all 4 slots
        session.OnPeerBitmap("peer-a", allTrue, generation: 5);

        // Second call with gen=3 (older) must be discarded entirely
        // → ComputeAssignments won't fire, so no new state change
        var stale = session.OnPeerBitmap("peer-a", allTrue, generation: 3);
        Assert.Empty(stale);
    }

    [Fact]
    public void OnPeerBitmap_FreshGeneration_Replaces()
    {
        // Deterministic scenario:
        //   gen=1: peer-a has only chunks [0,1] → exactly those 2 go in-flight (< cap of 4)
        //   Receive both → in-flight drains to zero (guaranteed, we know which indices were assigned)
        //   gen=2: peer-a now has all 8 → should assign 4 from [2..7] (6 candidates, cap=4)
        var session = new ChunkShuffleSession("root", 8);

        var hasOnlyTwo = new bool[8];
        hasOnlyTwo[0] = true;
        hasOnlyTwo[1] = true;

        var gen1 = session.OnPeerBitmap("peer-a", hasOnlyTwo, generation: 1);
        var assigned1 = gen1.SelectMany(a => a.ChunkIndices).ToArray();
        // Both candidates must be assigned (only 2 available, cap=4)
        Assert.Equal(2, assigned1.Length);

        // Drain in-flight completely — we know exactly which indices were assigned
        foreach (var idx in assigned1)
            session.OnChunkReceived(idx);

        // gen=2 with full bitmap: 6 candidates [2..7], cap=4 → assigns exactly 4
        var gen2 = session.OnPeerBitmap("peer-a", AllTrue(8), generation: 2);
        var requested = gen2.SelectMany(a => a.ChunkIndices).ToArray();

        Assert.Equal(ProtocolConstants.MaxConcurrentChunkTransfers, requested.Length);
        Assert.All(requested, idx => Assert.InRange(idx, 2, 7));
    }

    // ── Chunk arrival + coalescing ────────────────────────────────────────────

    [Fact]
    public void OnChunkReceived_BatchSizeReached_ReturnsTrueOnExactBatch()
    {
        var session = new ChunkShuffleSession("root", 100);

        // Feed (BatchSize - 1) chunks — should not trigger broadcast
        for (var i = 0; i < ProtocolConstants.ChunkBitmapBroadcastBatchSize - 1; i++)
            Assert.False(session.OnChunkReceived(i));

        // The exact batch-size chunk triggers it
        Assert.True(session.OnChunkReceived(ProtocolConstants.ChunkBitmapBroadcastBatchSize - 1));
    }

    [Fact]
    public void OnChunkReceived_AfterBroadcastReset_CountsRestart()
    {
        var session = new ChunkShuffleSession("root", 100);

        // First batch
        for (var i = 0; i < ProtocolConstants.ChunkBitmapBroadcastBatchSize - 1; i++)
            session.OnChunkReceived(i);
        Assert.True(session.OnChunkReceived(ProtocolConstants.ChunkBitmapBroadcastBatchSize - 1));

        // Second batch starts fresh
        for (var i = ProtocolConstants.ChunkBitmapBroadcastBatchSize;
                 i < ProtocolConstants.ChunkBitmapBroadcastBatchSize * 2 - 1; i++)
            Assert.False(session.OnChunkReceived(i));
    }

    [Fact]
    public void OnChunkReceived_CoalesceTimeoutElapsed_ReturnsTrueEarly()
    {
        var fakeTimeMs = 0L;
        var session    = new ChunkShuffleSession("root", 100, getTimestampMs: () => fakeTimeMs);

        // Advance past the coalesce interval before any chunk arrives
        fakeTimeMs = ProtocolConstants.ChunkBitmapBroadcastCoalesceMs + 1;

        // First chunk triggers broadcast because timeout already elapsed
        Assert.True(session.OnChunkReceived(0));
    }

    [Fact]
    public void OnChunkReceived_UnderCoalesceTimeout_ReturnsFalse()
    {
        var fakeTimeMs = 0L;
        var session    = new ChunkShuffleSession("root", 100, getTimestampMs: () => fakeTimeMs);

        // Only 50ms elapsed (well below 500ms coalesce threshold)
        fakeTimeMs = 50;
        Assert.False(session.OnChunkReceived(0));
    }

    [Fact]
    public void OnChunkReceived_AllChunks_SetsIsComplete()
    {
        var n       = 5;
        var session = new ChunkShuffleSession("root", n);

        for (var i = 0; i < n - 1; i++)
        {
            session.OnChunkReceived(i);
            Assert.False(session.IsComplete);
        }
        session.OnChunkReceived(n - 1);
        Assert.True(session.IsComplete);
    }

    // ── Peer drop + reassignment ──────────────────────────────────────────────

    [Fact]
    public void OnPeerDropped_ReleasesInFlightAndReassignsToRemainingPeer()
    {
        // 4-chunk content, peer A fills all 4 slots, peer B is idle.
        var session = new ChunkShuffleSession("root", 4);
        var allTrue = AllTrue(4);

        session.OnPeerBitmap("peer-a", allTrue, 1);  // A gets [0,1,2,3] (all slots full)
        var bFirst = session.OnPeerBitmap("peer-b", allTrue, 1);
        // Nothing left for B because A has all 4 in-flight
        Assert.Empty(bFirst);

        // Drop A → 4 freed. B now has 4 open slots and all 4 chunks available.
        var reassign = session.OnPeerDropped("peer-a");
        var bNew     = reassign.Where(r => r.PeerUhid == "peer-b")
                               .SelectMany(r => r.ChunkIndices)
                               .OrderBy(i => i)
                               .ToArray();
        Assert.Equal(new[] { 0, 1, 2, 3 }, bNew);
    }

    [Fact]
    public void OnPeerDropped_UnknownPeer_ReturnsEmpty()
    {
        var session = new ChunkShuffleSession("root", 8);
        // No peers registered at all
        var result = session.OnPeerDropped("ghost");
        Assert.Empty(result);
    }

    // ── BuildBitmapPayload ────────────────────────────────────────────────────

    [Fact]
    public void BuildBitmapPayload_ReflectsLocalHaveAtCreation()
    {
        var have    = new[] { 0, 2, 5 };
        var session = new ChunkShuffleSession("root-abc", 8, localHave: have);

        var payload = session.BuildBitmapPayload(generation: 42);

        Assert.Equal("root-abc", payload.RootHash);
        Assert.Equal(8, payload.ChunkCount);
        Assert.Equal(42u, payload.Generation);

        var decoded = ChunkBitmapPayload.Decode(payload.HaveBitset, 8);
        Assert.True(decoded[0]);
        Assert.False(decoded[1]);
        Assert.True(decoded[2]);
        Assert.False(decoded[3]);
        Assert.False(decoded[4]);
        Assert.True(decoded[5]);
        Assert.False(decoded[6]);
        Assert.False(decoded[7]);
    }

    [Fact]
    public void BuildBitmapPayload_AfterOnChunkReceived_IncludesNewChunks()
    {
        var session = new ChunkShuffleSession("root", 4);

        session.OnChunkReceived(1);
        session.OnChunkReceived(3);

        var payload = session.BuildBitmapPayload(1);
        var decoded = ChunkBitmapPayload.Decode(payload.HaveBitset, 4);

        Assert.False(decoded[0]);
        Assert.True(decoded[1]);
        Assert.False(decoded[2]);
        Assert.True(decoded[3]);
    }

    [Fact]
    public void LocalHaveCount_UpdatesOnChunkReceived()
    {
        var session = new ChunkShuffleSession("root", 10);
        Assert.Equal(0, session.LocalHaveCount);

        session.OnChunkReceived(3);
        session.OnChunkReceived(7);
        Assert.Equal(2, session.LocalHaveCount);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Part 3 — ContentService integration with ChunkBitmap packets
// ─────────────────────────────────────────────────────────────────────────────

public class ContentServiceChunkBitmapTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private const string LocalUhid    = "local-node";
    private const string PublisherUhid = "publisher-node";

    private static (ContentService svc, FakeMeshSender sender, InMemoryContentStore store) NewSvc(string uhid = LocalUhid)
    {
        var sender = new FakeMeshSender(uhid);
        var store  = new InMemoryContentStore();
        var svc    = new ContentService(sender, new RoutingService(sender), store);
        return (svc, sender, store);
    }

    // ── BroadcastBitmapAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task BroadcastBitmapAsync_UnknownRoot_DoesNotBroadcast()
    {
        var (svc, sender, _) = NewSvc();
        await svc.BroadcastBitmapAsync("unknown-root");
        Assert.Empty(sender.Broadcasts);
    }

    [Fact]
    public async Task BroadcastBitmapAsync_AfterPublish_EmitsFullBitmap()
    {
        var (svc, sender, _) = NewSvc();
        var data       = new byte[6];
        var descriptor = await svc.PublishAsync("x.bin", data, chunkSizeBytes: 3);
        sender.Clear();

        await svc.BroadcastBitmapAsync(descriptor.RootHash);

        var bitmapBroadcasts = sender.Broadcasts.Where(p => p.Type == PacketType.ChunkBitmap).ToArray();
        Assert.Single(bitmapBroadcasts);

        var payload = JsonSerializer.Deserialize<ChunkBitmapPayload>(
                          bitmapBroadcasts[0].Payload, Json)!;
        Assert.Equal(descriptor.RootHash, payload.RootHash);
        Assert.Equal(descriptor.ChunkCount, payload.ChunkCount);
        Assert.True(payload.HasAll());            // publisher has everything
        Assert.Equal(1u, payload.Generation);     // first broadcast
    }

    [Fact]
    public async Task BroadcastBitmapAsync_CalledTwice_GenerationIncrements()
    {
        var (svc, sender, _) = NewSvc();
        var descriptor = await svc.PublishAsync("y.bin", new byte[4], chunkSizeBytes: 2);
        sender.Clear();

        await svc.BroadcastBitmapAsync(descriptor.RootHash);
        await svc.BroadcastBitmapAsync(descriptor.RootHash);

        // ConcurrentBag has LIFO ordering — sort by generation for deterministic assertions.
        var payloads = sender.Broadcasts
            .Where(p => p.Type == PacketType.ChunkBitmap)
            .Select(p => JsonSerializer.Deserialize<ChunkBitmapPayload>(p.Payload, Json)!)
            .OrderBy(p => p.Generation)
            .ToArray();

        Assert.Equal(2, payloads.Length);
        Assert.Equal(1u, payloads[0].Generation);
        Assert.Equal(2u, payloads[1].Generation);
    }

    // ── HandleAsync — ChunkBitmap ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ChunkBitmap_UnknownRoot_IsIgnored()
    {
        var (svc, sender, _) = NewSvc();

        var bitmapPayload = new ChunkBitmapPayload
        {
            RootHash   = new string('0', 64),
            ChunkCount = 4,
            HaveBitset = ChunkBitmapPayload.Encode(Enumerable.Repeat(true, 4).ToArray()),
            Generation = 1,
        };

        await svc.HandleAsync(new MeshPacket
        {
            Type       = PacketType.ChunkBitmap,
            SourceUhid = PublisherUhid,
            Payload    = JsonSerializer.SerializeToUtf8Bytes(bitmapPayload, Json),
        });

        // No chunk requests should have been issued
        Assert.DoesNotContain(sender.Broadcasts, p => p.Type == PacketType.ChunkRequest);
        Assert.DoesNotContain(sender.Unicasts,   u => u.Packet.Type == PacketType.ChunkRequest);
    }

    [Fact]
    public async Task HandleAsync_ChunkBitmap_WithFullPeerBitmap_IssuersChunkRequests()
    {
        // Local node knows the descriptor but has no chunks.
        // Publisher sends a full bitmap → local should request up to MaxConcurrent chunks.
        var (svc, sender, store) = NewSvc();

        // Create descriptor on a separate publisher and save locally (as if we got it via TorrentMetadata)
        var pubSender = new FakeMeshSender(PublisherUhid);
        var pubSvc    = new ContentService(pubSender, new RoutingService(pubSender));
        var data      = Enumerable.Range(0, 30).Select(i => (byte)i).ToArray();
        var descriptor = await pubSvc.PublishAsync("blob.bin", data, chunkSizeBytes: 3);  // 10 chunks

        await store.SaveDescriptorAsync(descriptor);  // receiver knows the descriptor
        sender.Clear();

        // Publisher sends its full bitmap
        var peerFlags = Enumerable.Repeat(true, 10).ToArray();
        var bitmapPayload = new ChunkBitmapPayload
        {
            RootHash   = descriptor.RootHash,
            ChunkCount = 10,
            HaveBitset = ChunkBitmapPayload.Encode(peerFlags),
            Generation = 1,
        };

        await svc.HandleAsync(new MeshPacket
        {
            Type       = PacketType.ChunkBitmap,
            SourceUhid = PublisherUhid,
            Payload    = JsonSerializer.SerializeToUtf8Bytes(bitmapPayload, Json),
        });

        var requests = sender.Broadcasts
            .Where(p => p.Type == PacketType.ChunkRequest)
            .Select(p => JsonSerializer.Deserialize<ChunkRequestPayload>(p.Payload, Json)!)
            .ToArray();

        // Should have issued exactly MaxConcurrentChunkTransfers chunk requests
        var allRequested = requests.SelectMany(r => r.ChunkIndices).ToArray();
        Assert.Equal(ProtocolConstants.MaxConcurrentChunkTransfers, allRequested.Length);
        Assert.Equal(descriptor.RootHash, requests[0].RootHash);
    }

    [Fact]
    public async Task HandleAsync_ChunkBitmap_StaleThenFresh_OnlyFreshIssuersRequests()
    {
        var (svc, sender, store) = NewSvc();
        var pubSender  = new FakeMeshSender(PublisherUhid);
        var pubSvc     = new ContentService(pubSender, new RoutingService(pubSender));
        var descriptor = await pubSvc.PublishAsync("c.bin",
                             Enumerable.Range(0, 20).Select(b => (byte)b).ToArray(),
                             chunkSizeBytes: 4); // 5 chunks

        await store.SaveDescriptorAsync(descriptor);
        sender.Clear();

        var fullBitset = ChunkBitmapPayload.Encode(Enumerable.Repeat(true, 5).ToArray());

        // Fresh packet gen=10
        await svc.HandleAsync(new MeshPacket
        {
            Type       = PacketType.ChunkBitmap,
            SourceUhid = PublisherUhid,
            Payload    = JsonSerializer.SerializeToUtf8Bytes(
                             new ChunkBitmapPayload { RootHash = descriptor.RootHash,
                                                      ChunkCount = 5, HaveBitset = fullBitset,
                                                      Generation = 10 }, Json),
        });

        var afterFresh = sender.Broadcasts.Count(p => p.Type == PacketType.ChunkRequest);
        sender.Clear();

        // Stale packet gen=7 — must be discarded, no new requests
        await svc.HandleAsync(new MeshPacket
        {
            Type       = PacketType.ChunkBitmap,
            SourceUhid = PublisherUhid,
            Payload    = JsonSerializer.SerializeToUtf8Bytes(
                             new ChunkBitmapPayload { RootHash = descriptor.RootHash,
                                                      ChunkCount = 5, HaveBitset = fullBitset,
                                                      Generation = 7 }, Json),
        });

        var afterStale = sender.Broadcasts.Count(p => p.Type == PacketType.ChunkRequest);
        Assert.True(afterFresh > 0,   "fresh bitmap should have triggered requests");
        Assert.Equal(0, afterStale);  // stale bitmap must not trigger new requests
    }

    [Fact]
    public async Task HandleAsync_ChunkBitmap_MalformedPayload_IsIgnoredSilently()
    {
        var (svc, sender, _) = NewSvc();

        await svc.HandleAsync(new MeshPacket
        {
            Type       = PacketType.ChunkBitmap,
            SourceUhid = PublisherUhid,
            Payload    = new byte[] { 0xFF, 0x00, 0x13 }, // not valid JSON
        });

        Assert.DoesNotContain(sender.Broadcasts, p => p.Type == PacketType.ChunkRequest);
    }

    // ── ChunkData → coalesced bitmap re-advertisement ─────────────────────────

    [Fact]
    public async Task HandleAsync_ChunkData_WithSession_TriggersBitmapAfterBatch()
    {
        // Set up: local node has descriptor only (no chunks). Session is pre-created
        // by feeding a ChunkBitmap from a peer first, which also issues requests.
        // Then simulate receiving the chunks: after BatchSize chunks, bitmap should be broadcast.
        var (svc, sender, store) = NewSvc();
        var pubSender  = new FakeMeshSender(PublisherUhid);
        var pubSvc     = new ContentService(pubSender, new RoutingService(pubSender));
        var data       = Enumerable.Range(0, ProtocolConstants.ChunkBitmapBroadcastBatchSize * 3)
                                   .Select(i => (byte)i)
                                   .ToArray();
        var descriptor = await pubSvc.PublishAsync("big.bin", data, chunkSizeBytes: 1);
        await store.SaveDescriptorAsync(descriptor);

        // Activate shuffle session via a peer bitmap
        var allTrue = ChunkBitmapPayload.Encode(Enumerable.Repeat(true, descriptor.ChunkCount).ToArray());
        await svc.HandleAsync(new MeshPacket
        {
            Type       = PacketType.ChunkBitmap,
            SourceUhid = PublisherUhid,
            Payload    = JsonSerializer.SerializeToUtf8Bytes(
                             new ChunkBitmapPayload
                             {
                                 RootHash = descriptor.RootHash, ChunkCount = descriptor.ChunkCount,
                                 HaveBitset = allTrue, Generation = 1
                             }, Json),
        });
        sender.Clear();

        // Feed exactly BatchSize verified chunk-data packets from the publisher
        for (var i = 0; i < ProtocolConstants.ChunkBitmapBroadcastBatchSize; i++)
        {
            var chunkBytes = new byte[] { data[i] };
            var chunkDataPayload = new ChunkDataPayload
            {
                RootHash   = descriptor.RootHash,
                ChunkIndex = i,
                Data       = chunkBytes,
            };
            await svc.HandleAsync(new MeshPacket
            {
                Type       = PacketType.ChunkData,
                SourceUhid = PublisherUhid,
                Payload    = JsonSerializer.SerializeToUtf8Bytes(chunkDataPayload, Json),
            });
        }

        // After the batch, at least one ChunkBitmap broadcast should have been sent
        var bitmapsSent = sender.Broadcasts.Where(p => p.Type == PacketType.ChunkBitmap).ToArray();
        Assert.NotEmpty(bitmapsSent);

        // The emitted bitmap should reflect the batch of received chunks
        var lastBitmap = JsonSerializer.Deserialize<ChunkBitmapPayload>(
                             bitmapsSent.Last().Payload, Json)!;
        Assert.Equal(descriptor.RootHash, lastBitmap.RootHash);
        var decoded = ChunkBitmapPayload.Decode(lastBitmap.HaveBitset, descriptor.ChunkCount);
        for (var i = 0; i < ProtocolConstants.ChunkBitmapBroadcastBatchSize; i++)
            Assert.True(decoded[i], $"chunk {i} should be flagged as received in the bitmap");
    }
}
