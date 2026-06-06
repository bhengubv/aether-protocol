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

public class ContentServiceTests
{
    private const string Local = "local-uhid";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static (ContentService svc, FakeMeshSender sender, RoutingService routing, InMemoryContentStore store) NewService(
        string localUhid = Local)
    {
        var sender = new FakeMeshSender(localUhid);
        var routing = new RoutingService(sender);
        var store = new InMemoryContentStore();
        var svc = new ContentService(sender, routing, store);
        return (svc, sender, routing, store);
    }

    private static byte[] BuildPayload(int totalBytes)
    {
        var buf = new byte[totalBytes];
        for (var i = 0; i < buf.Length; i++) buf[i] = (byte)(i & 0xff);
        return buf;
    }

    // ─── PublishAsync ─────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_PersistsDescriptorAndAllChunks()
    {
        var (svc, _, _, store) = NewService();
        var data = BuildPayload(7); // 7 bytes, chunkSize=3 -> 3 chunks (3,3,1)

        var descriptor = await svc.PublishAsync("file.bin", data, "text/plain", chunkSizeBytes: 3);

        Assert.NotEmpty(descriptor.RootHash);
        Assert.Equal("file.bin", descriptor.Name);
        Assert.Equal("text/plain", descriptor.ContentType);
        Assert.Equal(7, descriptor.TotalBytes);
        Assert.Equal(3, descriptor.ChunkSizeBytes);
        Assert.Equal(3, descriptor.ChunkCount);
        Assert.True(descriptor.VerifySelf());

        var stored = await store.GetDescriptorAsync(descriptor.RootHash);
        Assert.NotNull(stored);

        var indices = await store.ListChunksAsync(descriptor.RootHash);
        Assert.Equal(new[] { 0, 1, 2 }, indices);

        var chunk2 = await store.GetChunkAsync(descriptor.RootHash, 2);
        Assert.NotNull(chunk2);
        Assert.Single(chunk2!); // last chunk is 1 byte (7 - 3 - 3)
    }

    [Fact]
    public async Task PublishAsync_NullData_Throws()
    {
        var (svc, _, _, _) = NewService();
        await Assert.ThrowsAsync<ArgumentNullException>(() => svc.PublishAsync("a", null!));
    }

    // ─── AssembleAsync ────────────────────────────────────────────

    [Fact]
    public async Task AssembleAsync_AfterPublish_ReturnsOriginalBytes()
    {
        var (svc, _, _, _) = NewService();
        var data = BuildPayload(20);
        var descriptor = await svc.PublishAsync("blob.bin", data, chunkSizeBytes: 6);

        var assembled = await svc.AssembleAsync(descriptor.RootHash);

        Assert.NotNull(assembled);
        Assert.Equal(data, assembled);
    }

    [Fact]
    public async Task AssembleAsync_UnknownRoot_ReturnsNull()
    {
        var (svc, _, _, _) = NewService();
        var assembled = await svc.AssembleAsync("0000000000000000000000000000000000000000000000000000000000000000");
        Assert.Null(assembled);
    }

    [Fact]
    public async Task AssembleAsync_MissingChunk_ReturnsNull()
    {
        var (svc, _, _, store) = NewService();
        var data = BuildPayload(10);
        var descriptor = await svc.PublishAsync("blob.bin", data, chunkSizeBytes: 3);

        // Confirm all chunks present, then drop one to simulate partial download.
        var fullIndices = await store.ListChunksAsync(descriptor.RootHash);
        Assert.Equal(descriptor.ChunkCount, fullIndices.Count);

        // Replace store so descriptor is present but chunk 1 is missing.
        var sparseStore = new InMemoryContentStore();
        await sparseStore.SaveDescriptorAsync(descriptor);
        for (var i = 0; i < descriptor.ChunkCount; i++)
        {
            if (i == 1) continue;
            var bytes = await store.GetChunkAsync(descriptor.RootHash, i);
            await sparseStore.SaveChunkAsync(descriptor.RootHash, i, bytes!);
        }

        var sender = new FakeMeshSender(Local);
        var sparseSvc = new ContentService(sender, new RoutingService(sender), sparseStore);

        var assembled = await sparseSvc.AssembleAsync(descriptor.RootHash);
        Assert.Null(assembled);
    }

    // ─── AnnounceAsync ────────────────────────────────────────────

    [Fact]
    public async Task AnnounceAsync_BroadcastsTorrentMetadataPacket()
    {
        var (svc, sender, _, _) = NewService();
        var descriptor = await svc.PublishAsync("note.txt", BuildPayload(4), chunkSizeBytes: 2);

        sender.Clear();
        await svc.AnnounceAsync(descriptor);

        Assert.Single(sender.Broadcasts);
        var pkt = sender.Broadcasts.Single();
        Assert.Equal(PacketType.TorrentMetadata, pkt.Type);
        Assert.Equal(Local, pkt.SourceUhid);

        var body = JsonSerializer.Deserialize<TorrentMetadataPayload>(pkt.Payload, JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(descriptor.RootHash, body!.Descriptor.RootHash);
        Assert.Contains(Local, body.SeederUhids);
    }

    // ─── RequestChunksAsync ───────────────────────────────────────

    [Fact]
    public async Task RequestChunksAsync_NullPeer_BroadcastsRequestWithIndices()
    {
        var (svc, sender, _, _) = NewService();

        await svc.RequestChunksAsync("root-x", new[] { 0, 2, 4 }, peerUhid: null);

        Assert.Single(sender.Broadcasts);
        var pkt = sender.Broadcasts.Single();
        Assert.Equal(PacketType.ChunkRequest, pkt.Type);
        Assert.Equal(Local, pkt.SourceUhid);

        var body = JsonSerializer.Deserialize<ChunkRequestPayload>(pkt.Payload, JsonOptions);
        Assert.NotNull(body);
        Assert.Equal("root-x", body!.RootHash);
        Assert.Equal(new[] { 0, 2, 4 }, body.ChunkIndices);
    }

    [Fact]
    public async Task RequestChunksAsync_EmptyRootHash_Throws()
    {
        var (svc, _, _, _) = NewService();
        await Assert.ThrowsAsync<ArgumentException>(() => svc.RequestChunksAsync("", new[] { 0 }));
    }

    // ─── HandleAsync — TorrentMetadata ───────────────────────────

    [Fact]
    public async Task HandleAsync_TorrentMetadata_NewContent_FiresAnnouncedAndPersists()
    {
        var (svc, _, _, store) = NewService();
        // Build a descriptor by publishing on a separate publisher node.
        var publisher = new ContentService(new FakeMeshSender("publisher"), new RoutingService(new FakeMeshSender("publisher")));
        var descriptor = await publisher.PublishAsync("hello.bin", BuildPayload(8), chunkSizeBytes: 4);

        var payload = JsonSerializer.SerializeToUtf8Bytes(new TorrentMetadataPayload
        {
            Descriptor = descriptor,
            SeederUhids = new[] { "publisher" },
        }, JsonOptions);

        var packet = new MeshPacket
        {
            Type = PacketType.TorrentMetadata,
            SourceUhid = "publisher",
            DestinationUhid = string.Empty,
            Ttl = ProtocolConstants.DefaultTtl,
            Payload = payload,
        };

        ContentDescriptor? announced = null;
        svc.ContentAnnounced += (_, d) => announced = d;

        await svc.HandleAsync(packet);

        Assert.NotNull(announced);
        Assert.Equal(descriptor.RootHash, announced!.RootHash);
        var stored = await store.GetDescriptorAsync(descriptor.RootHash);
        Assert.NotNull(stored);
    }

    [Fact]
    public async Task HandleAsync_NonContentPacketType_IsIgnored()
    {
        var (svc, _, _, store) = NewService();

        // RouteRequest is unrelated to content; HandleAsync should silently ignore.
        var packet = new MeshPacket
        {
            Type = PacketType.RouteRequest,
            SourceUhid = "someone",
            Payload = new byte[] { 1, 2, 3 },
        };

        await svc.HandleAsync(packet);

        Assert.Empty(await store.ListDescriptorsAsync());
    }

    // ─── HandleAsync — ChunkData ─────────────────────────────────

    [Fact]
    public async Task HandleAsync_ChunkData_CompletesContentAndFiresEvents()
    {
        // Receiver knows the descriptor (via prior announcement) but has no
        // chunks. We feed in two chunk-data packets and expect ContentComplete
        // to fire once the second arrives.
        var (svc, _, _, store) = NewService();

        var publisherSender = new FakeMeshSender("publisher");
        var publisher = new ContentService(publisherSender, new RoutingService(publisherSender));
        var data = BuildPayload(6);
        var descriptor = await publisher.PublishAsync("two.bin", data, chunkSizeBytes: 3);
        await store.SaveDescriptorAsync(descriptor); // receiver has manifest only

        var chunkArrivals = new List<ChunkArrivedEventArgs>();
        ContentDescriptor? completed = null;
        svc.ChunkReceived += (_, e) => chunkArrivals.Add(e);
        svc.ContentComplete += (_, d) => completed = d;

        for (var i = 0; i < descriptor.ChunkCount; i++)
        {
            var chunk = new byte[descriptor.ChunkSizeBytes];
            Buffer.BlockCopy(data, i * descriptor.ChunkSizeBytes, chunk, 0, descriptor.ChunkSizeBytes);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new ChunkDataPayload
            {
                RootHash = descriptor.RootHash,
                ChunkIndex = i,
                Data = chunk,
            }, JsonOptions);

            await svc.HandleAsync(new MeshPacket
            {
                Type = PacketType.ChunkData,
                SourceUhid = "publisher",
                DestinationUhid = Local,
                Payload = payload,
            });
        }

        Assert.Equal(2, chunkArrivals.Count);
        Assert.All(chunkArrivals, e => Assert.True(e.Verified));
        Assert.True(chunkArrivals[^1].ContentComplete);
        Assert.NotNull(completed);
        Assert.Equal(descriptor.RootHash, completed!.RootHash);

        var assembled = await svc.AssembleAsync(descriptor.RootHash);
        Assert.Equal(data, assembled);
    }

    [Fact]
    public async Task HandleAsync_ChunkData_FailsHashCheck_FiresUnverifiedAndDoesNotStore()
    {
        var (svc, _, _, store) = NewService();

        var publisherSender = new FakeMeshSender("publisher");
        var publisher = new ContentService(publisherSender, new RoutingService(publisherSender));
        var data = BuildPayload(3);
        var descriptor = await publisher.PublishAsync("one.bin", data, chunkSizeBytes: 3);
        await store.SaveDescriptorAsync(descriptor);

        ChunkArrivedEventArgs? received = null;
        svc.ChunkReceived += (_, e) => received = e;

        // Tamper: send wrong bytes for chunk 0.
        var tamperedPayload = JsonSerializer.SerializeToUtf8Bytes(new ChunkDataPayload
        {
            RootHash = descriptor.RootHash,
            ChunkIndex = 0,
            Data = new byte[] { 0xff, 0xff, 0xff },
        }, JsonOptions);

        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.ChunkData,
            SourceUhid = "publisher",
            Payload = tamperedPayload,
        });

        Assert.NotNull(received);
        Assert.False(received!.Verified);
        Assert.False(received.ContentComplete);
        Assert.Empty(await store.ListChunksAsync(descriptor.RootHash));
    }

    [Fact]
    public async Task HandleAsync_ChunkData_UnknownRoot_IsDiscarded()
    {
        var (svc, _, _, store) = NewService();

        var payload = JsonSerializer.SerializeToUtf8Bytes(new ChunkDataPayload
        {
            RootHash = "0000000000000000000000000000000000000000000000000000000000000000",
            ChunkIndex = 0,
            Data = new byte[] { 1, 2, 3 },
        }, JsonOptions);

        ChunkArrivedEventArgs? received = null;
        svc.ChunkReceived += (_, e) => received = e;

        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.ChunkData,
            SourceUhid = "stranger",
            Payload = payload,
        });

        Assert.Null(received);
        Assert.Empty(await store.ListDescriptorsAsync());
    }

    // ─── HandleAsync — ChunkRequest ──────────────────────────────

    [Fact]
    public async Task HandleAsync_ChunkRequest_KnownContent_RespondsOneChunkPerIndex()
    {
        // Local node has the content and should respond to a chunk request.
        var (svc, sender, _, _) = NewService();
        var descriptor = await svc.PublishAsync("served.bin", BuildPayload(9), chunkSizeBytes: 3);
        sender.Clear();

        var payload = JsonSerializer.SerializeToUtf8Bytes(new ChunkRequestPayload
        {
            RootHash = descriptor.RootHash,
            ChunkIndices = new[] { 0, 2 }, // skip the middle chunk
        }, JsonOptions);

        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.ChunkRequest,
            SourceUhid = "requester",
            DestinationUhid = Local,
            Payload = payload,
        });

        // No route to "requester" -> falls back to broadcast for each chunk.
        // (Routing may also broadcast RREQs while looking up; filter for ChunkData.)
        var chunkBroadcasts = sender.Broadcasts.Where(p => p.Type == PacketType.ChunkData).ToArray();
        Assert.Equal(2, chunkBroadcasts.Length);

        var indicesSent = chunkBroadcasts
            .Select(p => JsonSerializer.Deserialize<ChunkDataPayload>(p.Payload, JsonOptions)!.ChunkIndex)
            .OrderBy(i => i)
            .ToArray();
        Assert.Equal(new[] { 0, 2 }, indicesSent);
    }

    [Fact]
    public async Task HandleAsync_ChunkRequest_EmptyIndices_RespondsWithAllChunks()
    {
        var (svc, sender, _, _) = NewService();
        var descriptor = await svc.PublishAsync("all.bin", BuildPayload(6), chunkSizeBytes: 3);
        sender.Clear();

        var payload = JsonSerializer.SerializeToUtf8Bytes(new ChunkRequestPayload
        {
            RootHash = descriptor.RootHash,
            ChunkIndices = Array.Empty<int>(),
        }, JsonOptions);

        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.ChunkRequest,
            SourceUhid = "requester",
            Payload = payload,
        });

        var chunkBroadcasts = sender.Broadcasts.Where(p => p.Type == PacketType.ChunkData).ToArray();
        Assert.Equal(descriptor.ChunkCount, chunkBroadcasts.Length);
    }

    [Fact]
    public async Task HandleAsync_ChunkRequest_UnknownRoot_NoResponse()
    {
        var (svc, sender, _, _) = NewService();

        var payload = JsonSerializer.SerializeToUtf8Bytes(new ChunkRequestPayload
        {
            RootHash = "0000000000000000000000000000000000000000000000000000000000000000",
            ChunkIndices = new[] { 0 },
        }, JsonOptions);

        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.ChunkRequest,
            SourceUhid = "requester",
            Payload = payload,
        });

        Assert.DoesNotContain(sender.Broadcasts, p => p.Type == PacketType.ChunkData);
        Assert.DoesNotContain(sender.Unicasts, u => u.Packet.Type == PacketType.ChunkData);
    }

    // ─── ContentDescriptor sanity ────────────────────────────────

    [Fact]
    public void ContentDescriptor_FromBytes_VerifySelfAndPerChunk()
    {
        var data = BuildPayload(11);
        var descriptor = ContentDescriptor.FromBytes("x.bin", data, chunkSizeBytes: 4);

        Assert.True(descriptor.VerifySelf());
        Assert.Equal(3, descriptor.ChunkCount); // ceil(11/4) = 3
        Assert.Equal(11, descriptor.TotalBytes);

        // Chunk 0 verifies, a wrong chunk does not.
        Assert.True(descriptor.VerifyChunk(0, data.AsSpan(0, 4)));
        Assert.False(descriptor.VerifyChunk(0, new byte[] { 0, 0, 0, 0 }));
        Assert.False(descriptor.VerifyChunk(99, data.AsSpan(0, 4))); // out of range
    }
}
