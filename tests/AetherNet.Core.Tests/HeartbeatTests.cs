// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Heartbeat;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for <see cref="HeartbeatService"/> (PacketType.Heartbeat). Uses a fake
/// <see cref="IMeshSender"/> — no transport needed.
/// </summary>
public sealed class HeartbeatTests
{
    private sealed class FakeMeshSender : IMeshSender
    {
        public string LocalUhid { get; set; } = "aether:local:01";
        public List<MeshPacket> Broadcasts { get; } = [];

        public IReadOnlyList<PeerInfo> GetConnectedPeers() => Array.Empty<PeerInfo>();
        public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken ct = default)
        {
            Broadcasts.Add(packet);
            return Task.FromResult(1);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static HeartbeatService Build(FakeMeshSender sender)
        => new(sender, NullLogger<HeartbeatService>.Instance);

    private static MeshPacket HeartbeatFrom(string source, int sequence, long sentAtMs) => new()
    {
        Type = PacketType.Heartbeat,
        SourceUhid = source,
        DestinationUhid = "*",
        Payload = JsonSerializer.SerializeToUtf8Bytes(
            new HeartbeatPayload { Sequence = sequence, SentAtMs = sentAtMs }, JsonOpts),
    };

    [Theory]
    [InlineData(1, 1700000000000L, "{\"sequence\":1,\"sent_at_ms\":1700000000000}")]
    [InlineData(0, 0L, "{\"sequence\":0,\"sent_at_ms\":0}")]
    public void HeartbeatPayload_SerializesToCanonicalBytes(int sequence, long ms, string expected)
    {
        var payload = new HeartbeatPayload { Sequence = sequence, SentAtMs = ms };
        var json = System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        Assert.Equal(expected, json);
    }

    [Fact]
    public async Task Send_BroadcastsHeartbeat_WithIncrementingSequence()
    {
        var sender = new FakeMeshSender();
        var svc = Build(sender);

        await svc.SendHeartbeatAsync();
        await svc.SendHeartbeatAsync();

        Assert.Equal(2, sender.Broadcasts.Count);
        Assert.All(sender.Broadcasts, p => Assert.Equal(PacketType.Heartbeat, p.Type));
        Assert.All(sender.Broadcasts, p => Assert.Equal(1, p.Ttl));

        var first = JsonSerializer.Deserialize<HeartbeatPayload>(sender.Broadcasts[0].Payload, JsonOpts)!;
        var second = JsonSerializer.Deserialize<HeartbeatPayload>(sender.Broadcasts[1].Payload, JsonOpts)!;
        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
    }

    [Fact]
    public async Task Handle_RecordsPeerAndRaisesEvent()
    {
        var svc = Build(new FakeMeshSender { LocalUhid = "aether:local:01" });
        PeerLiveness? seen = null;
        svc.PeerSeen += (_, e) => seen = e;

        var ok = await svc.HandleAsync(HeartbeatFrom("aether:peer:aa", 7, 1700000000000L));

        Assert.True(ok);
        Assert.NotNull(seen);
        Assert.Equal("aether:peer:aa", seen!.Uhid);
        Assert.Equal(7, seen.LastSequence);
        Assert.Equal(1700000000000L, seen.LastSentAtMs);

        var known = svc.GetKnownPeers();
        Assert.Single(known);
        Assert.Equal("aether:peer:aa", known[0].Uhid);
    }

    [Fact]
    public async Task Handle_RefreshesExistingPeer()
    {
        var svc = Build(new FakeMeshSender());
        await svc.HandleAsync(HeartbeatFrom("aether:peer:aa", 1, 1000L));
        await svc.HandleAsync(HeartbeatFrom("aether:peer:aa", 2, 2000L));

        var known = svc.GetKnownPeers();
        Assert.Single(known);
        Assert.Equal(2, known[0].LastSequence);
    }

    [Fact]
    public async Task Handle_OwnHeartbeat_IsIgnored()
    {
        var svc = Build(new FakeMeshSender { LocalUhid = "aether:local:01" });
        var ok = await svc.HandleAsync(HeartbeatFrom("aether:local:01", 1, 1000L));
        Assert.False(ok);
        Assert.Empty(svc.GetKnownPeers());
    }

    [Fact]
    public async Task Handle_WrongPacketType_ReturnsFalse()
    {
        var svc = Build(new FakeMeshSender());
        var pkt = HeartbeatFrom("aether:peer:aa", 1, 1000L);
        pkt.Type = PacketType.Data;
        Assert.False(await svc.HandleAsync(pkt));
    }

    [Fact]
    public async Task GetLivePeers_IncludesRecentlySeenPeer()
    {
        var svc = Build(new FakeMeshSender());
        await svc.HandleAsync(HeartbeatFrom("aether:peer:aa", 1, 1000L));

        // A just-received heartbeat is live within any generous window.
        var live = svc.GetLivePeers(withinSeconds: 3600);
        Assert.Single(live);
        Assert.Equal("aether:peer:aa", live[0].Uhid);

        // A negative window pushes the recency horizon into the future, so it excludes even a
        // just-seen peer — a deterministic proof the filter filters (no wall-clock race).
        Assert.Empty(svc.GetLivePeers(withinSeconds: -1));
    }
}
