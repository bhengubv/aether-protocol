// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Sos;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for the SOS acknowledgement path (<see cref="PacketType.SosAck"/>). A receiving node
/// sends a directed ack back to the originator; the originator tallies distinct reach and raises
/// <see cref="SosAcknowledgement"/>. Uses a fake <see cref="IMeshSender"/> — no transport needed.
/// </summary>
public sealed class SosAckTests
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
            return Task.FromResult(1);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static SosBroadcastService Build(FakeMeshSender sender)
        => new(sender, backend: null, incentives: null, logger: NullLogger<SosBroadcastService>.Instance);

    /// <summary>Originate a real SosBroadcast packet on a separate node and return it + its id.</summary>
    private static async Task<(MeshPacket Sos, Guid Id)> OriginateSos(string originUhid)
    {
        var originSender = new FakeMeshSender { LocalUhid = originUhid };
        var origin = Build(originSender);
        await origin.BroadcastSosAsync("medical", "help", -26.20, 28.04, geohash: "ke7g");
        return (originSender.Broadcasts[0], origin.GetActiveAlerts()[0].Id);
    }

    private static MeshPacket MakeAck(Guid broadcastId, string responderUhid) => new()
    {
        Type = PacketType.SosAck,
        SourceUhid = responderUhid,
        DestinationUhid = "aether:origin:aa",
        Payload = JsonSerializer.SerializeToUtf8Bytes(
            new SosAckPayload { BroadcastId = broadcastId, ReceivedAtMs = 1_700_000_000_000 }, JsonOpts),
    };

    [Fact]
    public async Task Handle_ReceivingSos_SendsDirectedAckToOriginator()
    {
        var (sos, id) = await OriginateSos("aether:origin:aa");

        var receiverSender = new FakeMeshSender { LocalUhid = "aether:receiver:bb" };
        await Build(receiverSender).HandleAsync(sos);

        var ack = Assert.Single(receiverSender.Sends);
        Assert.Equal(PacketType.SosAck, ack.Packet.Type);
        Assert.Equal("aether:origin:aa", ack.NextHop);
        Assert.Equal("aether:origin:aa", ack.Packet.DestinationUhid);

        var body = JsonSerializer.Deserialize<SosAckPayload>(ack.Packet.Payload, JsonOpts)!;
        Assert.Equal(id, body.BroadcastId);
    }

    [Fact]
    public async Task Handle_OwnSos_DoesNotAck()
    {
        var localSender = new FakeMeshSender { LocalUhid = "aether:origin:aa" };
        var svc = Build(localSender);
        await svc.BroadcastSosAsync("panic", null, 0, 0);

        // Re-handling our own broadcast must not generate an ack.
        await svc.HandleAsync(localSender.Broadcasts[0]);
        Assert.Empty(localSender.Sends);
    }

    [Fact]
    public async Task HandleAck_OnOriginator_RecordsResponderAndRaisesEvent()
    {
        var origin = Build(new FakeMeshSender { LocalUhid = "aether:origin:aa" });
        await origin.BroadcastSosAsync("fire", "north wing", -26.1, 28.0);
        var id = origin.GetActiveAlerts()[0].Id;

        SosAcknowledgement? captured = null;
        origin.SosAcknowledged += (_, e) => captured = e;

        await origin.HandleAckAsync(MakeAck(id, "aether:responder:cc"));

        Assert.NotNull(captured);
        Assert.Equal(id, captured!.BroadcastId);
        Assert.Equal("aether:responder:cc", captured.ResponderUhid);
        Assert.Equal(1, captured.TotalAcknowledgements);
        Assert.Contains("aether:responder:cc", origin.GetActiveAlerts()[0].AcknowledgedBy);
    }

    [Fact]
    public async Task HandleAck_DuplicateResponder_CountedOnce()
    {
        var origin = Build(new FakeMeshSender { LocalUhid = "aether:origin:aa" });
        await origin.BroadcastSosAsync("medical", null, 0, 0);
        var id = origin.GetActiveAlerts()[0].Id;

        var events = 0;
        origin.SosAcknowledged += (_, _) => events++;

        await origin.HandleAckAsync(MakeAck(id, "aether:responder:cc"));
        await origin.HandleAckAsync(MakeAck(id, "aether:responder:cc")); // same responder again

        Assert.Equal(1, events);
        Assert.Single(origin.GetActiveAlerts()[0].AcknowledgedBy);
    }

    [Fact]
    public async Task HandleAck_TwoDistinctResponders_CountsTwo()
    {
        var origin = Build(new FakeMeshSender { LocalUhid = "aether:origin:aa" });
        await origin.BroadcastSosAsync("medical", null, 0, 0);
        var id = origin.GetActiveAlerts()[0].Id;

        await origin.HandleAckAsync(MakeAck(id, "aether:responder:cc"));
        await origin.HandleAckAsync(MakeAck(id, "aether:responder:dd"));

        Assert.Equal(2, origin.GetActiveAlerts()[0].AcknowledgedBy.Count);
    }

    [Fact]
    public async Task HandleAck_UnknownBroadcast_IsNoOp()
    {
        var svc = Build(new FakeMeshSender { LocalUhid = "aether:local:01" });
        var raised = false;
        svc.SosAcknowledged += (_, _) => raised = true;

        await svc.HandleAckAsync(MakeAck(Guid.NewGuid(), "aether:responder:cc"));
        Assert.False(raised);
    }

    [Fact]
    public async Task HandleAck_WrongPacketType_Throws()
    {
        var svc = Build(new FakeMeshSender());
        var pkt = MakeAck(Guid.NewGuid(), "aether:responder:cc");
        pkt.Type = PacketType.Data;
        await Assert.ThrowsAsync<ArgumentException>(() => svc.HandleAckAsync(pkt));
    }

    // Byte-identity gate: SosAckPayload must serialize to exactly these bytes in every language
    // (fixtures/sos/vectors.json). snake_case, field order broadcast_id then received_at_ms, no
    // whitespace, GUID lowercase-dashed, received_at_ms a bare integer.
    [Theory]
    [InlineData("0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f", 1700000000000L,
        "{\"broadcast_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"received_at_ms\":1700000000000}")]
    [InlineData("00000000-0000-0000-0000-000000000000", 0L,
        "{\"broadcast_id\":\"00000000-0000-0000-0000-000000000000\",\"received_at_ms\":0}")]
    public void SosAckPayload_SerializesToCanonicalBytes(string guid, long ms, string expected)
    {
        var payload = new SosAckPayload { BroadcastId = Guid.Parse(guid), ReceivedAtMs = ms };
        var json = System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        Assert.Equal(expected, json);
    }
}
