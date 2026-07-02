// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Identity;
using AetherNet.Models;
using AetherNet.Presence;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for PresenceBeacon(21)/PresenceQuery(22) (<see cref="PresenceService"/>) and the
/// EridAnnounce(56) mesh binding (<see cref="EridAnnounceService"/>). Presence carries the rotating
/// erid + coarse geohash; ERID-announce is opaque encrypted transport whose byte-identity is re-pinned
/// against the existing <see cref="EridAnnouncementCodec"/> vector.
/// </summary>
public sealed class PresenceEridAnnounceTests
{
    private sealed class FakeMeshSender : IMeshSender
    {
        public string LocalUhid { get; set; } = "aether:local:01";
        public List<(MeshPacket Packet, string NextHop)> Sends { get; } = [];
        public List<MeshPacket> Broadcasts { get; } = [];

        public IReadOnlyList<PeerInfo> GetConnectedPeers() => Array.Empty<PeerInfo>();
        public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken ct = default)
        {
            Sends.Add((packet, nextHopUhid));
            return Task.FromResult(true);
        }
        public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken ct = default)
        {
            Broadcasts.Add(packet);
            return Task.FromResult(4);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new();
    private static string Utf8(byte[] b) => System.Text.Encoding.UTF8.GetString(b);

    // ── Presence byte-identity ──────────────────────────────────────────────

    [Fact]
    public void Beacon_Available_SerializesToCanonicalBytes()
    {
        var p = new PresenceBeaconPayload { Erid = "3B38HPPFG9JXE37Q", Geohash = "u4pru", Capabilities = 73, Status = 1, SentAtMs = 1700000000000L };
        Assert.Equal(
            "{\"erid\":\"3B38HPPFG9JXE37Q\",\"geohash\":\"u4pru\",\"capabilities\":73,\"status\":1,\"sent_at_ms\":1700000000000}",
            Utf8(JsonSerializer.SerializeToUtf8Bytes(p, JsonOpts)));
    }

    [Fact]
    public void Beacon_HiddenOffline_SerializesToCanonicalBytes()
    {
        var p = new PresenceBeaconPayload { Erid = "0Z5BD0HB1Q7W76MY", Geohash = "", Capabilities = 0, Status = 5, SentAtMs = 0 };
        Assert.Equal(
            "{\"erid\":\"0Z5BD0HB1Q7W76MY\",\"geohash\":\"\",\"capabilities\":0,\"status\":5,\"sent_at_ms\":0}",
            Utf8(JsonSerializer.SerializeToUtf8Bytes(p, JsonOpts)));
    }

    [Fact]
    public void Query_SerializesToCanonicalBytes()
    {
        var p = new PresenceQueryPayload { QueryId = Guid.Parse("11112222-3333-4444-5555-666677778888"), Geohash = "u4pru" };
        Assert.Equal(
            "{\"query_id\":\"11112222-3333-4444-5555-666677778888\",\"geohash\":\"u4pru\"}",
            Utf8(JsonSerializer.SerializeToUtf8Bytes(p, JsonOpts)));
    }

    // ── Presence behaviour ──────────────────────────────────────────────────

    [Fact]
    public async Task BroadcastBeacon_EmitsBeaconPacket_AndHandleRaisesEvent()
    {
        var s = new FakeMeshSender { LocalUhid = "aether:alice:01" };
        var svc = new PresenceService(s, NullLogger<PresenceService>.Instance);
        var beacon = new PresenceBeaconPayload { Erid = "3B38HPPFG9JXE37Q", Geohash = "u4pru", Capabilities = 73, Status = 1, SentAtMs = 1700000000000L };

        Assert.Equal(4, await svc.BroadcastBeaconAsync(beacon));
        var sent = Assert.Single(s.Broadcasts);
        Assert.Equal(PacketType.PresenceBeacon, sent.Type);

        PresenceBeaconReceived? got = null;
        svc.BeaconReceived += (_, e) => got = e;
        sent.SourceUhid = "aether:alice:01";
        Assert.True(await svc.HandleAsync(sent));
        Assert.NotNull(got);
        Assert.Equal("3B38HPPFG9JXE37Q", got!.Beacon.Erid);
        Assert.Equal("aether:alice:01", got.FromUhid);
    }

    [Fact]
    public async Task Query_EmitsQueryPacket_AndHandleRaisesEvent()
    {
        var s = new FakeMeshSender { LocalUhid = "aether:bob:02" };
        var svc = new PresenceService(s, NullLogger<PresenceService>.Instance);

        var qid = await svc.QueryAsync("u4pru");
        Assert.NotEqual(Guid.Empty, qid);
        var sent = Assert.Single(s.Broadcasts);
        Assert.Equal(PacketType.PresenceQuery, sent.Type);
        var body = JsonSerializer.Deserialize<PresenceQueryPayload>(sent.Payload, JsonOpts)!;
        Assert.Equal(qid, body.QueryId);
        Assert.Equal("u4pru", body.Geohash);

        PresenceQueryReceived? got = null;
        svc.QueryReceived += (_, e) => got = e;
        Assert.True(await svc.HandleAsync(sent));
        Assert.NotNull(got);
        Assert.Equal(qid, got!.Query.QueryId);
    }

    [Fact]
    public async Task Presence_Handle_WrongType_ReturnsFalse()
    {
        var svc = new PresenceService(new FakeMeshSender(), NullLogger<PresenceService>.Instance);
        Assert.False(await svc.HandleAsync(new MeshPacket { Type = PacketType.Data, Payload = Array.Empty<byte>() }));
    }

    [Fact]
    public async Task Presence_Handle_BeaconWithEmptyErid_ReturnsFalse()
    {
        var svc = new PresenceService(new FakeMeshSender(), NullLogger<PresenceService>.Instance);
        var pkt = new MeshPacket
        {
            Type = PacketType.PresenceBeacon,
            SourceUhid = "aether:x:01",
            Payload = JsonSerializer.SerializeToUtf8Bytes(new PresenceBeaconPayload { Erid = "" }, JsonOpts),
        };
        Assert.False(await svc.HandleAsync(pkt));
    }

    // ── EridAnnounce(56) transport ──────────────────────────────────────────

    [Fact]
    public async Task EridAnnounce_Send_EmitsDirectedPacket_AndHandleRaisesEvent()
    {
        var s = new FakeMeshSender { LocalUhid = "aether:alice:01" };
        var svc = new EridAnnounceService(s, NullLogger<EridAnnounceService>.Instance);
        var enc = new byte[] { 1, 2, 3, 4, 5 }; // opaque Signal-encrypted announcement

        Assert.True(await svc.SendAnnounceAsync("aether:bob:02", enc));
        var sent = Assert.Single(s.Sends);
        Assert.Equal(PacketType.EridAnnounce, sent.Packet.Type);
        Assert.Equal("aether:bob:02", sent.NextHop);

        EridAnnounceReceived? got = null;
        svc.AnnounceReceived += (_, e) => got = e;
        sent.Packet.SourceUhid = "aether:bob:02";
        Assert.True(await svc.HandleAsync(sent.Packet));
        Assert.NotNull(got);
        Assert.Equal(enc, got!.EncryptedAnnouncement);
        Assert.Equal("aether:bob:02", got.FromUhid);
    }

    [Fact]
    public async Task EridAnnounce_Handle_WrongTypeOrEmpty_ReturnsFalse()
    {
        var svc = new EridAnnounceService(new FakeMeshSender(), NullLogger<EridAnnounceService>.Instance);
        Assert.False(await svc.HandleAsync(new MeshPacket { Type = PacketType.Data, Payload = new byte[] { 1 } }));
        Assert.False(await svc.HandleAsync(new MeshPacket { Type = PacketType.EridAnnounce, Payload = Array.Empty<byte>() }));
    }

    /// <summary>Re-pin the shared ERID-announcement frame byte-identity (existing 8/8 codec) against fixtures/erid.</summary>
    [Fact]
    public void EridAnnouncementCodec_MatchesCanonicalFrame()
    {
        var routingKey = Convert.FromHexString("8f3aa76cdbe9a2b47c5813504023a77bda134c31aa096b51392fb29cdd57ddca");
        var frame = EridAnnouncementCodec.Encode(routingKey, epochSeconds: 900, eridLength: 16);
        Assert.Equal(
            "41455244010000038400000010000000208f3aa76cdbe9a2b47c5813504023a77bda134c31aa096b51392fb29cdd57ddca",
            Convert.ToHexString(frame).ToLowerInvariant());
    }
}
