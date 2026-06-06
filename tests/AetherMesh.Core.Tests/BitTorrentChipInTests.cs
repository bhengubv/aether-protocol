// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherMesh.Core.Tests.Fakes;
using AetherMesh.Protocol;
using AetherMesh.Streaming;
using AetherMesh.Streaming.Models;
using Xunit;

namespace AetherMesh.Core.Tests;

public class BitTorrentChipInTests
{
    private const string Host = "host-uhid";
    private const string Follower = "follower-uhid";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static (WatchTogetherService svc, FakeMeshSender sender) NewService(string localUhid)
    {
        var sender = new FakeMeshSender(localUhid);
        var routing = new FakeRoutingService();
        var svc = new WatchTogetherService(sender, routing);
        return (svc, sender);
    }

    private static TorrentInfo SampleTorrent() => new()
    {
        InfoHash = "aabbccddeeff00112233445566778899aabbccdd",
        MagnetLink = "magnet:?xt=urn:btih:aabbccddeeff00112233445566778899aabbccdd",
        Name = "test-movie",
        TotalSizeBytes = 2_000_000_000L,
        PieceCount = 8_000,
        PieceSizeBytes = 262_144,
        Files = new List<TorrentFile>
        {
            new() { Path = "test-movie.mkv", SizeBytes = 2_000_000_000L }
        },
    };

    // ─── BroadcastTorrentAsync ───────────────────────────────────────────────

    [Fact]
    public async Task BroadcastTorrentAsync_EmitsTorrentMetadataPacket()
    {
        var (svc, sender) = NewService(Host);
        var session = await svc.HostAsync("root", "Movie Night");
        sender.Clear();

        var torrent = SampleTorrent();
        await svc.BroadcastTorrentAsync(session.Id, torrent);

        var pkt = sender.Broadcasts.SingleOrDefault(p => p.Type == PacketType.TorrentMetadata);
        Assert.NotNull(pkt);
        Assert.Equal(Host, pkt!.SourceUhid);

        var parsed = JsonSerializer.Deserialize<TorrentInfo>(pkt.Payload, JsonOptions)!;
        Assert.Equal(torrent.InfoHash, parsed.InfoHash);
        Assert.Equal(torrent.Name, parsed.Name);
        Assert.Single(parsed.Files);
        Assert.Equal("test-movie.mkv", parsed.Files[0].Path);
    }

    // ─── HandleAsync TorrentMetadata ─────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_TorrentMetadata_FiresTorrentReceivedEvent()
    {
        var (hostSvc, hostSender) = NewService(Host);
        var (followerSvc, _) = NewService(Follower);

        var session = await hostSvc.HostAsync("root", "Movie Night");
        // Follower learns about the session
        var joinPkt = hostSender.Broadcasts.First(p => p.Type == PacketType.WatchSync);
        await followerSvc.HandleAsync(joinPkt);

        hostSender.Clear();
        await hostSvc.BroadcastTorrentAsync(session.Id, SampleTorrent());
        var torrentPkt = hostSender.Broadcasts.Single(p => p.Type == PacketType.TorrentMetadata);

        (Guid sessionId, TorrentInfo torrent)? received = null;
        followerSvc.TorrentReceived += (_, e) => received = e;
        await followerSvc.HandleAsync(torrentPkt);

        Assert.NotNull(received);
        Assert.Equal("aabbccddeeff00112233445566778899aabbccdd", received!.Value.torrent.InfoHash);
    }

    // ─── ChipIn lifecycle ────────────────────────────────────────────────────

    [Fact]
    public async Task StartChipInAsync_CreatesPoolAndBroadcasts()
    {
        var (svc, sender) = NewService(Host);
        var session = await svc.HostAsync("root", "Movie Night");
        sender.Clear();

        ChipInPool? updatedPool = null;
        svc.ChipInUpdated += (_, p) => updatedPool = p;

        var pool = await svc.StartChipInAsync(
            session.Id,
            targetAmountZar: 150m,
            contentDescription: "The Dark Knight (2008)",
            torrentInfoHash: "abc123",
            magnetLink: "magnet:?xt=urn:btih:abc123");

        Assert.NotEqual(Guid.Empty, pool.Id);
        Assert.Equal(session.Id, pool.SessionId);
        Assert.Equal(Host, pool.InitiatorUhid);
        Assert.Equal(150m, pool.TargetAmountZar);
        Assert.Equal(ChipInState.Collecting, pool.State);
        Assert.Equal("abc123", pool.TorrentInfoHash);
        Assert.NotNull(updatedPool);

        // Broadcast must be a WatchSync packet with a chip_in discriminator
        var pkt = sender.Broadcasts.SingleOrDefault(p => p.Type == PacketType.WatchSync);
        Assert.NotNull(pkt);
        var doc = JsonDocument.Parse(pkt!.Payload);
        Assert.True(doc.RootElement.TryGetProperty("chip_in", out _));
    }

    [Fact]
    public async Task ContributeAsync_AddsContribution_UpdatesCollected()
    {
        var (svc, _) = NewService(Host);
        var session = await svc.HostAsync("root", "Movie Night");
        var pool = await svc.StartChipInAsync(session.Id, 150m, "content", null, null);

        var updated = await svc.ContributeAsync(pool.Id, "user-1", 50m);

        Assert.NotNull(updated);
        Assert.Equal(50m, updated!.CollectedAmountZar);
        Assert.Single(updated.Contributions);
        Assert.Equal("user-1", updated.Contributions[0].ContributorUhid);
        Assert.Equal(50m, updated.Contributions[0].AmountZar);
        Assert.Equal(ChipInState.Collecting, updated.State); // not yet funded
    }

    [Fact]
    public async Task ContributeAsync_ReachesTarget_TransitionToFunded()
    {
        var (svc, _) = NewService(Host);
        var session = await svc.HostAsync("root", "Movie Night");
        var pool = await svc.StartChipInAsync(session.Id, 100m, "content", null, null);

        await svc.ContributeAsync(pool.Id, "user-1", 60m);

        ChipInPool? fundedPool = null;
        svc.ChipInUpdated += (_, p) => fundedPool = p;

        // Second contribution puts us at 100 ZAR — exactly funded
        var updated = await svc.ContributeAsync(pool.Id, "user-2", 40m);

        Assert.NotNull(updated);
        Assert.Equal(100m, updated!.CollectedAmountZar);
        Assert.Equal(ChipInState.Funded, updated.State);
        Assert.True(updated.IsFunded);
        Assert.NotNull(fundedPool);
        Assert.Equal(ChipInState.Funded, fundedPool!.State);
    }

    [Fact]
    public async Task ContributeAsync_UnknownPool_ReturnsNull()
    {
        var (svc, _) = NewService(Host);

        var result = await svc.ContributeAsync(Guid.NewGuid(), "user-1", 10m);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetChipIn_ReturnsExistingPool()
    {
        var (svc, _) = NewService(Host);
        var session = await svc.HostAsync("root", "Movie Night");
        var pool = await svc.StartChipInAsync(session.Id, 50m, null, null, null);

        var found = svc.GetChipIn(pool.Id);

        Assert.NotNull(found);
        Assert.Equal(pool.Id, found!.Id);
    }

    [Fact]
    public async Task GetChipIn_UnknownId_ReturnsNull()
    {
        var (svc, _) = NewService(Host);

        Assert.Null(svc.GetChipIn(Guid.NewGuid()));
    }

    [Fact]
    public void ChipInPool_IsFunded_TrueWhenCollectedGteTarget()
    {
        var pool = new ChipInPool { TargetAmountZar = 100m, CollectedAmountZar = 100m };
        Assert.True(pool.IsFunded);

        pool.CollectedAmountZar = 150m;
        Assert.True(pool.IsFunded);

        pool.CollectedAmountZar = 99.99m;
        Assert.False(pool.IsFunded);
    }

    // ─── WatchChunkRequest handling ──────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WatchChunkRequest_IsHandledWithoutThrowing()
    {
        var (svc, _) = NewService(Follower);

        // Should handle gracefully — chunk serving is delegated to AetherMesh.Content
        var exception = await Record.ExceptionAsync(() => svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.WatchChunkRequest,
            SourceUhid = Host,
            Payload = new byte[] { 1, 2, 3 },
        }));

        Assert.Null(exception);
    }

    // ─── Follower receives ChipIn broadcast ──────────────────────────────────

    [Fact]
    public async Task HandleAsync_WatchSync_ChipInEnvelope_StoresPoolAndFiresEvent()
    {
        var (hostSvc, hostSender) = NewService(Host);
        var (followerSvc, _) = NewService(Follower);

        var session = await hostSvc.HostAsync("root", "Movie Night");
        var joinPkt = hostSender.Broadcasts.First(p => p.Type == PacketType.WatchSync);
        await followerSvc.HandleAsync(joinPkt);
        hostSender.Clear();

        var pool = await hostSvc.StartChipInAsync(session.Id, 200m, "Big Buck Bunny", null, null);
        var syncPkt = hostSender.Broadcasts.First(p => p.Type == PacketType.WatchSync);

        ChipInPool? received = null;
        followerSvc.ChipInUpdated += (_, p) => received = p;
        await followerSvc.HandleAsync(syncPkt);

        Assert.NotNull(received);
        Assert.Equal(pool.Id, received!.Id);
        Assert.Equal(200m, received.TargetAmountZar);

        // GetChipIn should also work on the follower now
        Assert.NotNull(followerSvc.GetChipIn(pool.Id));
    }
}
