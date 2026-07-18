// SPDX-License-Identifier: MIT

using AetherNet.BitTorrent.Gateway;
using AetherNet.BitTorrent.Metainfo;
using AetherNet.Content;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using Xunit;

namespace AetherNet.BitTorrent.Gateway.Tests;

public class TorrentMeshGatewayTests
{
    private static byte[] MakeFile(int size)
    {
        var f = new byte[size];
        for (int i = 0; i < size; i++) f[i] = (byte)(i * 97 + 13);
        return f;
    }

    [Fact]
    public async Task Swarm_to_mesh_to_offline_node_roundtrips_the_file()
    {
        var file = MakeFile(200_000);

        // 1. A real BitTorrent representation of the file (SHA-1 pieces), verified by parsing back.
        var torrentBytes = TorrentBuilder.CreateSingleFile("movie.bin", file, 65536, "http://tracker/announce");
        var torrent = TorrentMetainfo.Parse(torrentBytes);
        Assert.Equal(file.Length, torrent.TotalLength);
        Assert.Equal(40, torrent.InfoHashV1Hex.Length); // SHA-1 identity

        // 2. The gateway ingests the completed download bytes into node A's content store.
        var storeA = new InMemoryContentStore();
        var descriptor = await TorrentMeshGateway.IngestAsync(storeA, "movie.bin", file);
        Assert.True(descriptor.VerifySelf());
        Assert.True(descriptor.ChunkSizeBytes > 0);
        Assert.Equal(64, descriptor.RootHash.Length); // SHA-256 identity — distinct from the SHA-1 info-hash

        // 3. The mesh delivers the content-addressed chunks to an offline node B (its own store).
        var storeB = new InMemoryContentStore();
        await storeB.SaveDescriptorAsync(descriptor);
        foreach (var i in await storeA.ListChunksAsync(descriptor.RootHash))
        {
            var chunk = await storeA.GetChunkAsync(descriptor.RootHash, i);
            await storeB.SaveChunkAsync(descriptor.RootHash, i, chunk!);
        }

        // 4. Node B reassembles the identical file purely from mesh chunks.
        var reassembled = await TorrentMeshGateway.AssembleFromStoreAsync(storeB, descriptor.RootHash);
        Assert.NotNull(reassembled);
        Assert.Equal(file, reassembled);

        // 5. Identity bridge: both the SHA-1 torrent identity and the SHA-256 mesh identity map to
        //    the same underlying file, reproducibly.
        var mapped = TorrentMeshGateway.MapToMeshDescriptor(torrent, file);
        Assert.Equal(descriptor.RootHash, mapped.RootHash);
    }

    [Fact]
    public async Task Forward_bridge_over_live_ContentService_publishes_and_reassembles()
    {
        var file = MakeFile(50_000);

        var storeA = new InMemoryContentStore();
        var nodeA = new ContentService(new NoopMeshSender(), new NoopRoutingService(), storeA);
        var descriptor = await TorrentMeshGateway.IngestAsync(nodeA, "clip.bin", file);

        // Deliver chunks to node B, then assemble through the real ContentService.AssembleAsync.
        var storeB = new InMemoryContentStore();
        var nodeB = new ContentService(new NoopMeshSender(), new NoopRoutingService(), storeB);
        await storeB.SaveDescriptorAsync(descriptor);
        foreach (var i in await storeA.ListChunksAsync(descriptor.RootHash))
            await storeB.SaveChunkAsync(descriptor.RootHash, i, (await storeA.GetChunkAsync(descriptor.RootHash, i))!);

        var reassembled = await nodeB.AssembleAsync(descriptor.RootHash);
        Assert.Equal(file, reassembled);
    }

    [Fact]
    public void Export_as_torrent_produces_a_parseable_torrent_for_mesh_content()
    {
        var content = MakeFile(40_000);
        var torrentBytes = TorrentMeshGateway.ExportAsTorrent("shared.bin", content, 16384, "http://tracker/announce");

        var parsed = TorrentMetainfo.Parse(torrentBytes);
        Assert.Equal("shared.bin", parsed.Name);
        Assert.Equal(content.Length, parsed.TotalLength);
        Assert.Equal((40_000 + 16384 - 1) / 16384, parsed.PieceHashes.Count);
    }

    [Fact]
    public async Task Assemble_returns_null_when_a_chunk_is_tampered()
    {
        var file = MakeFile(30_000);
        var store = new InMemoryContentStore();
        var descriptor = await TorrentMeshGateway.IngestAsync(store, "x.bin", file);

        // Corrupt chunk 0.
        await store.SaveChunkAsync(descriptor.RootHash, 0, new byte[descriptor.ChunkSizeBytes]);

        Assert.Null(await TorrentMeshGateway.AssembleFromStoreAsync(store, descriptor.RootHash));
    }
}

// ── Minimal no-op mesh doubles: the gateway's content translation is under test here, not the
//    routing/transport layer (which has its own tests). ─────────────────────────────────────────
file sealed class NoopMeshSender : IMeshSender
{
    public string LocalUhid => "gateway-node";
    public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default) => Task.FromResult(0);
}

file sealed class NoopRoutingService : IRoutingService
{
    public Task<RouteEntry?> FindRouteAsync(string destinationUhid, CancellationToken cancellationToken = default) => Task.FromResult<RouteEntry?>(null);
    public RouteEntry? GetCachedRoute(string destinationUhid) => null;
    public IReadOnlyList<RouteEntry> GetAllRoutes() => Array.Empty<RouteEntry>();
    public Task HandleRouteRequestAsync(MeshPacket routeRequest, string? linkLayerSenderUhid = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task HandleRouteReplyAsync(MeshPacket routeReply, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PruneAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
