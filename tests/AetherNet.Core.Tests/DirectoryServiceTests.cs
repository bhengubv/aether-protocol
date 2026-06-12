// SPDX-License-Identifier: MIT

using AetherNet.Content;
using AetherNet.Content.Models;
using AetherNet.Core.Tests.Fakes;
using AetherNet.Models;
using AetherNet.Protocol;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Tests for <see cref="DirectoryService"/> — application-layer name resolution
/// added in v1.2.0 (Issue #60).
/// </summary>
public class DirectoryServiceTests
{
    private static ContentDescriptor SampleDescriptor(string rootHash = "deadbeef")
    {
        return new ContentDescriptor
        {
            RootHash = rootHash,
            Name = "ignored-publisher-hint",
            TotalBytes = 1024,
            ChunkSizeBytes = 256,
            ChunkCount = 4,
            ChunkHashes = new[] { "h0", "h1", "h2", "h3" },
            ContentType = "audio/flac",
        };
    }

    // Mirrors DirectoryService.HashName — pins the salted-hash value so the 7 language ports must
    // reproduce it exactly (cross-language parity), and proves the plaintext name never hits the wire.
    private const string NameHashSalt = "aether-dir-name-v1:";
    private static string ExpectedHash(string name)
        => System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
               System.Text.Encoding.UTF8.GetBytes(NameHashSalt + name))).ToLowerInvariant();

    // ─── PublishAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task PublishAsync_StoresLocallyAndBroadcastsNamePublish()
    {
        var sender = new FakeMeshSender("publisher");
        sender.AddPeer(new PeerInfo { Uhid = "peer-1" });
        sender.AddPeer(new PeerInfo { Uhid = "peer-2" });
        var dir = new DirectoryService(sender);

        await dir.PublishAsync("podcast:abc", SampleDescriptor("root-abc"));

        // Local resolve hits the catalogue immediately.
        var hit = await dir.ResolveAsync("podcast:abc");
        Assert.NotNull(hit);
        Assert.Equal("root-abc", hit!.RootHash);

        // Broadcast went out.
        Assert.Single(sender.Broadcasts);
        Assert.Equal(PacketType.NamePublish, sender.Broadcasts.First().Type);
    }

    [Fact]
    public async Task ResolveAsync_LocalCatalogueHit_ReturnsImmediately_NoQueryBroadcast()
    {
        var sender = new FakeMeshSender("local");
        sender.AddPeer(new PeerInfo { Uhid = "peer-1" });
        var dir = new DirectoryService(sender);

        await dir.PublishAsync("track:xyz", SampleDescriptor("root-xyz"));
        sender.Clear();

        var hit = await dir.ResolveAsync("track:xyz");

        Assert.NotNull(hit);
        Assert.Equal("root-xyz", hit!.RootHash);
        Assert.Empty(sender.Broadcasts); // no NameQuery sent — local hit
    }

    // ─── Inbound NamePublish ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_InboundNamePublish_PopulatesCatalogueAndFiresEvent()
    {
        var sender = new FakeMeshSender("local");
        var dir = new DirectoryService(sender);

        DirectoryEntryAnnouncedEventArgs? captured = null;
        dir.EntryAnnounced += (_, e) => captured = e;

        // Build a NamePublish packet from a peer.
        var peerSender = new FakeMeshSender("peer-publisher");
        peerSender.AddPeer(new PeerInfo { Uhid = "local" });
        var peerDir = new DirectoryService(peerSender);
        var descriptor = SampleDescriptor("from-peer");
        await peerDir.PublishAsync("reel:hello", descriptor);

        // Take the broadcast and replay it into the local service.
        var broadcast = peerSender.Broadcasts.First();
        broadcast.SourceUhid = "peer-publisher";
        await dir.HandleAsync(broadcast);

        // Local catalogue now has the entry.
        var hit = await dir.ResolveAsync("reel:hello");
        Assert.NotNull(hit);
        Assert.Equal("from-peer", hit!.RootHash);

        // Event fired.
        Assert.NotNull(captured);
        // PRIVACY: the event carries only the salted hash; the plaintext name is not on the wire.
        Assert.Equal(ExpectedHash("reel:hello"), captured!.NameHash);
        Assert.DoesNotContain("reel:hello", System.Text.Encoding.UTF8.GetString(broadcast.Payload));
        Assert.Equal("peer-publisher", captured.SourceUhid);
        Assert.Equal("from-peer", captured.Descriptor.RootHash);
    }

    // ─── Query / Response roundtrip ──────────────────────────────────────

    [Fact]
    public async Task HandleAsync_QueryWithMatchingName_UnicastsNamePublishResponse()
    {
        var holderSender = new FakeMeshSender("holder");
        holderSender.AddPeer(new PeerInfo { Uhid = "asker" });
        var holder = new DirectoryService(holderSender);

        await holder.PublishAsync("album:test", SampleDescriptor("album-root"));
        holderSender.Clear();

        // Build a NameQuery as if from `asker`.
        var queryId = Guid.NewGuid();
        var queryPayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new NameQueryPayload { NameHash = ExpectedHash("album:test"), QueryId = queryId },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
        var queryPacket = new MeshPacket
        {
            Type = PacketType.NameQuery,
            SourceUhid = "asker",
            Payload = queryPayload,
        };

        await holder.HandleAsync(queryPacket);

        // Holder unicasts back a NamePublish with InResponseToQueryId set.
        Assert.Single(holderSender.Unicasts);
        var (responsePacket, nextHop) = holderSender.Unicasts.First();
        Assert.Equal("asker", nextHop);
        Assert.Equal(PacketType.NamePublish, responsePacket.Type);

        var responseBody = System.Text.Json.JsonSerializer.Deserialize<NamePublishPayload>(
            responsePacket.Payload,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
        Assert.NotNull(responseBody);
        Assert.Equal(ExpectedHash("album:test"), responseBody!.NameHash);
        Assert.Equal("album-root", responseBody.Descriptor.RootHash);
        Assert.Equal(queryId, responseBody.InResponseToQueryId);
    }

    [Fact]
    public async Task HandleAsync_QueryForUnknownName_DoesNothing()
    {
        var sender = new FakeMeshSender("local");
        sender.AddPeer(new PeerInfo { Uhid = "asker" });
        var dir = new DirectoryService(sender);

        var queryPayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new NameQueryPayload { NameHash = ExpectedHash("nothing-here"), QueryId = Guid.NewGuid() },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
        var queryPacket = new MeshPacket
        {
            Type = PacketType.NameQuery,
            SourceUhid = "asker",
            Payload = queryPayload,
        };

        await dir.HandleAsync(queryPacket);

        Assert.Empty(sender.Unicasts);
        Assert.Empty(sender.Broadcasts);
    }

    [Fact]
    public async Task ResolveAsync_MissAndTimeout_ReturnsNull()
    {
        var sender = new FakeMeshSender("local");
        sender.AddPeer(new PeerInfo { Uhid = "peer-1" });
        var dir = new DirectoryService(sender);

        var hit = await dir.ResolveAsync("unknown-name", TimeSpan.FromMilliseconds(50));

        Assert.Null(hit);
        // A NameQuery WAS broadcast — we tried.
        Assert.Single(sender.Broadcasts);
        Assert.Equal(PacketType.NameQuery, sender.Broadcasts.First().Type);
    }

    [Fact]
    public async Task ResolveAsync_QueryAndAnswerArrives_ReturnsDescriptor()
    {
        var sender = new FakeMeshSender("local");
        sender.AddPeer(new PeerInfo { Uhid = "peer-1" });
        var dir = new DirectoryService(sender);

        // Start a resolve in the background.
        var resolveTask = dir.ResolveAsync("podcast:remote", TimeSpan.FromSeconds(2));

        // Wait briefly for the NameQuery to be broadcast.
        await Task.Delay(50);

        Assert.Single(sender.Broadcasts);
        var queryBroadcast = sender.Broadcasts.First();
        Assert.Equal(PacketType.NameQuery, queryBroadcast.Type);

        var query = System.Text.Json.JsonSerializer.Deserialize<NameQueryPayload>(
            queryBroadcast.Payload,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });

        // Simulate a peer responding with a NamePublish carrying InResponseToQueryId.
        var descriptor = SampleDescriptor("remote-root");
        var responsePayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new NamePublishPayload
            {
                NameHash = ExpectedHash("podcast:remote"),
                Descriptor = descriptor,
                InResponseToQueryId = query!.QueryId,
            },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
        var responsePacket = new MeshPacket
        {
            Type = PacketType.NamePublish,
            SourceUhid = "peer-1",
            Payload = responsePayload,
        };
        await dir.HandleAsync(responsePacket);

        var result = await resolveTask;
        Assert.NotNull(result);
        Assert.Equal("remote-root", result!.RootHash);
    }

    // ─── Listing ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ListNamesAsync_ReturnsCatalogueSnapshot()
    {
        var sender = new FakeMeshSender("local");
        var dir = new DirectoryService(sender);

        await dir.PublishAsync("a", SampleDescriptor("hash-a"));
        await dir.PublishAsync("b", SampleDescriptor("hash-b"));
        await dir.PublishAsync("c", SampleDescriptor("hash-c"));

        var names = await dir.ListNamesAsync();

        Assert.Equal(3, names.Count);
        Assert.Contains("a", names);
        Assert.Contains("b", names);
        Assert.Contains("c", names);
    }
}
