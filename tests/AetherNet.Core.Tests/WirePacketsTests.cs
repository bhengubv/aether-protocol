// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Forge;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Space;
using AetherNet.Space.Models;
using AetherNet.Vault;
using AetherNet.Vault.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for the Phase-2 WIRE bindings: SpaceBreadcrumb(40), ForgeAnnounce(41),
/// VaultShardRequest(42). Byte-identity gates + broadcast/handle behaviour.
/// </summary>
public sealed class WirePacketsTests
{
    private sealed class FakeMeshSender : IMeshSender
    {
        public string LocalUhid { get; set; } = "aether:local:01";
        public List<MeshPacket> Broadcasts { get; } = [];
        public List<(MeshPacket Packet, string NextHop)> Sends { get; } = [];

        public IReadOnlyList<PeerInfo> GetConnectedPeers() => Array.Empty<PeerInfo>();
        public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken ct = default)
        {
            Sends.Add((packet, nextHopUhid));
            return Task.FromResult(true);
        }
        public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken ct = default)
        {
            Broadcasts.Add(packet);
            return Task.FromResult(2);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new();

    // ── SpaceBreadcrumb (40) ────────────────────────────────────────────────

    [Fact]
    public void SpaceBreadcrumb_Emergency_SerializesToCanonicalBytes()
    {
        var p = new SpaceBreadcrumbPayload
        {
            ContentHash = "QmContentHashExample123",
            GeoHash = "u4pruy",
            AnchorUhid = "aether:alice:01",
            CreatedAtMs = 1700000000000L,
            TtlHours = 720,
            Type = 1,
            Signature = Enumerable.Repeat((byte)0x99, 64).ToArray(),
        };
        var json = System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(p, JsonOpts));
        Assert.Equal(
            "{\"content_hash\":\"QmContentHashExample123\",\"geo_hash\":\"u4pruy\",\"anchor_uhid\":\"aether:alice:01\"," +
            "\"created_at_ms\":1700000000000,\"ttl_hours\":720,\"type\":1," +
            "\"signature\":\"mZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmQ==\"}",
            json);
    }

    [Fact]
    public void SpaceBreadcrumb_NoticeUnsigned_SerializesToCanonicalBytes()
    {
        var p = new SpaceBreadcrumbPayload
        {
            ContentHash = "QmNotice777",
            GeoHash = "gcpvj0",
            AnchorUhid = "aether:bob:02",
            CreatedAtMs = 0,
            TtlHours = 72,
            Type = 0,
            Signature = Array.Empty<byte>(),
        };
        var json = System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(p, JsonOpts));
        Assert.Equal(
            "{\"content_hash\":\"QmNotice777\",\"geo_hash\":\"gcpvj0\",\"anchor_uhid\":\"aether:bob:02\"," +
            "\"created_at_ms\":0,\"ttl_hours\":72,\"type\":0,\"signature\":\"\"}",
            json);
    }

    [Fact]
    public async Task Space_Broadcast_EmitsBreadcrumbPacket_AndHandleRaisesEvent()
    {
        var sender = new FakeMeshSender { LocalUhid = "aether:alice:01" };
        var svc = new SpaceBreadcrumbService(sender, NullLogger<SpaceBreadcrumbService>.Instance);

        var crumb = new SpaceBreadcrumb
        {
            ContentHash = "QmX",
            GeoHash = "u4pruy",
            AnchorUhid = "aether:alice:01",
            CreatedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000L).UtcDateTime,
            TtlHours = 720,
            Type = BreadcrumbType.Emergency,
            Signature = Enumerable.Repeat((byte)0x99, 64).ToArray(),
        };
        var reached = await svc.BroadcastAsync(crumb);
        Assert.Equal(2, reached);
        var sent = Assert.Single(sender.Broadcasts);
        Assert.Equal(PacketType.SpaceBreadcrumb, sent.Type);

        SpaceBreadcrumb? got = null;
        svc.BreadcrumbReceived += (_, e) => got = e;
        var ok = await svc.HandleAsync(sent);
        Assert.True(ok);
        Assert.NotNull(got);
        Assert.Equal("u4pruy", got!.GeoHash);
        Assert.Equal(BreadcrumbType.Emergency, got.Type);
        Assert.Equal(720, got.TtlHours);
        Assert.Equal(64, got.Signature.Length);
    }

    [Fact]
    public async Task Space_Handle_WrongType_ReturnsFalse()
    {
        var svc = new SpaceBreadcrumbService(new FakeMeshSender(), NullLogger<SpaceBreadcrumbService>.Instance);
        Assert.False(await svc.HandleAsync(new MeshPacket { Type = PacketType.Data, Payload = Array.Empty<byte>() }));
    }

    // ── ForgeAnnounce (41) ──────────────────────────────────────────────────

    [Fact]
    public void ForgeAnnounce_SerializesToCanonicalBytes()
    {
        var p = new ForgeAnnouncePayload
        {
            PackageId = "npm:react@18.2.0",
            ContentHash = "QmForgeHash456",
            SizeBytes = 294912,
            AnnouncedAtMs = 1700000000000L,
        };
        var json = System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(p, JsonOpts));
        Assert.Equal(
            "{\"package_id\":\"npm:react@18.2.0\",\"content_hash\":\"QmForgeHash456\",\"size_bytes\":294912,\"announced_at_ms\":1700000000000}",
            json);
    }

    [Fact]
    public async Task Forge_Broadcast_EmitsAnnouncePacket_AndHandleRaisesEvent()
    {
        var sender = new FakeMeshSender { LocalUhid = "aether:alice:01" };
        var svc = new ForgeAnnounceService(sender, NullLogger<ForgeAnnounceService>.Instance);

        var reached = await svc.BroadcastAsync("npm:react@18.2.0", "QmForgeHash456", 294912, 1700000000000L);
        Assert.Equal(2, reached);
        var sent = Assert.Single(sender.Broadcasts);
        Assert.Equal(PacketType.ForgeAnnounce, sent.Type);

        ForgeAnnouncePayload? got = null;
        svc.AnnounceReceived += (_, e) => got = e;
        Assert.True(await svc.HandleAsync(sent));
        Assert.NotNull(got);
        Assert.Equal("npm:react@18.2.0", got!.PackageId);
        Assert.Equal(294912, got.SizeBytes);
    }

    [Fact]
    public async Task Forge_Handle_WrongType_ReturnsFalse()
    {
        var svc = new ForgeAnnounceService(new FakeMeshSender(), NullLogger<ForgeAnnounceService>.Instance);
        Assert.False(await svc.HandleAsync(new MeshPacket { Type = PacketType.Data, Payload = Array.Empty<byte>() }));
    }

    // ── VaultShardRequest (42) ──────────────────────────────────────────────

    [Fact]
    public void VaultShardRequest_SerializesToCanonicalBytes()
    {
        var p = new VaultShardRequestPayload { ShardHash = "QmShardHash789", RequesterUhid = "aether:bob:02" };
        var json = System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(p, JsonOpts));
        Assert.Equal("{\"shard_hash\":\"QmShardHash789\",\"requester_uhid\":\"aether:bob:02\"}", json);
    }

    [Fact]
    public async Task Vault_Request_EmitsShardRequestPacket_AndHandleRaisesEvent()
    {
        var sender = new FakeMeshSender { LocalUhid = "aether:bob:02" };
        var svc = new VaultShardRequestService(sender, NullLogger<VaultShardRequestService>.Instance);

        var reached = await svc.RequestShardAsync("QmShardHash789");
        Assert.Equal(2, reached);
        var sent = Assert.Single(sender.Broadcasts);
        Assert.Equal(PacketType.VaultShardRequest, sent.Type);
        var body = JsonSerializer.Deserialize<VaultShardRequestPayload>(sent.Payload, JsonOpts)!;
        Assert.Equal("QmShardHash789", body.ShardHash);
        Assert.Equal("aether:bob:02", body.RequesterUhid);

        VaultShardRequest? got = null;
        svc.ShardRequested += (_, e) => got = e;
        Assert.True(await svc.HandleAsync(sent));
        Assert.NotNull(got);
        Assert.Equal("QmShardHash789", got!.ShardHash);
        Assert.Equal("aether:bob:02", got.RequesterUhid);
    }

    [Fact]
    public async Task Vault_Handle_WrongType_ReturnsFalse()
    {
        var svc = new VaultShardRequestService(new FakeMeshSender(), NullLogger<VaultShardRequestService>.Instance);
        Assert.False(await svc.HandleAsync(new MeshPacket { Type = PacketType.Data, Payload = Array.Empty<byte>() }));
    }
}
