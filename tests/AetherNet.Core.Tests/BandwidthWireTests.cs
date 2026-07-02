// SPDX-License-Identifier: MIT

using AetherNet.Bandwidth;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for the ABMF WIRE bindings: BandwidthProbe(53), BandwidthAck(54), BandwidthGossip(55).
/// Binary little-endian byte-identity gates + send/handle behaviour.
/// </summary>
public sealed class BandwidthWireTests
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
            return Task.FromResult(3);
        }
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    // ── Byte-identity gates ─────────────────────────────────────────────────

    [Fact]
    public void Probe_SerializesToCanonicalBytes()
        => Assert.Equal("2a00000000401e18240a0600",
            Hex(BandwidthWireCodec.SerializeProbe(new BandwidthProbe(42, 1700000000000000L))));

    [Fact]
    public void Ack_SerializesToCanonicalBytes()
    {
        // SenderReceiveUs (999) is local-only and must NOT change the wire bytes.
        var ack = new BandwidthProbeAck(42, 1700000000000000L, 1700000000012345L, 1700000000013000L, SenderReceiveUs: 999L, ProbeBytes: 1200);
        Assert.Equal("2a00000000401e18240a060039701e18240a0600c8721e18240a0600b0040000",
            Hex(BandwidthWireCodec.SerializeAck(ack)));
    }

    [Fact]
    public void Gossip_SerializesToCanonicalBytes()
    {
        // PeerUhid/TransportName/MeasuredAt are not on the wire.
        var g = new BandwidthGossipPayload("peer", "tp", 5000000L, 25000L, BandwidthConfidence.Medium, default);
        Assert.Equal("404b4c0000000000a861000002", Hex(BandwidthWireCodec.SerializeGossip(g)));
    }

    [Fact]
    public void Ack_RoundTrips_SenderReceiveUsZeroed()
    {
        var back = BandwidthWireCodec.DeserializeAck(BandwidthWireCodec.SerializeAck(
            new BandwidthProbeAck(7, 100, 200, 300, 400, 512)));
        Assert.Equal(7u, back.Sequence);
        Assert.Equal(100, back.SenderSendUs);
        Assert.Equal(200, back.ReceiverReceiveUs);
        Assert.Equal(300, back.ReceiverSendUs);
        Assert.Equal(0, back.SenderReceiveUs); // not on wire
        Assert.Equal(512, back.ProbeBytes);
    }

    // ── Behaviour ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SendProbe_EmitsDirectedProbe()
    {
        var s = new FakeMeshSender { LocalUhid = "aether:a:01" };
        var svc = new BandwidthWireService(s, NullLogger<BandwidthWireService>.Instance);
        Assert.True(await svc.SendProbeAsync("aether:b:02", new BandwidthProbe(42, 1700000000000000L)));
        var sent = Assert.Single(s.Sends);
        Assert.Equal(PacketType.BandwidthProbe, sent.Packet.Type);
        Assert.Equal("aether:b:02", sent.NextHop);
    }

    [Fact]
    public async Task SendAck_EmitsDirectedAck()
    {
        var s = new FakeMeshSender();
        var svc = new BandwidthWireService(s, NullLogger<BandwidthWireService>.Instance);
        var ack = new BandwidthProbeAck(1, 2, 3, 4, 5, 6);
        Assert.True(await svc.SendAckAsync("aether:b:02", ack));
        Assert.Equal(PacketType.BandwidthAck, Assert.Single(s.Sends).Packet.Type);
    }

    [Fact]
    public async Task BroadcastGossip_EmitsGossip_AndHandleRaisesEvent_WithSourcePeer()
    {
        var s = new FakeMeshSender();
        var svc = new BandwidthWireService(s, NullLogger<BandwidthWireService>.Instance);
        var g = new BandwidthGossipPayload("", "", 5000000L, 25000L, BandwidthConfidence.Medium, default);
        Assert.Equal(3, await svc.BroadcastGossipAsync(g));
        var sent = Assert.Single(s.Broadcasts);
        Assert.Equal(PacketType.BandwidthGossip, sent.Type);

        BandwidthGossipPayload? got = null;
        svc.GossipReceived += (_, e) => got = e;
        sent.SourceUhid = "aether:peer:09";
        Assert.True(await svc.HandleAsync(sent));
        Assert.NotNull(got);
        Assert.Equal(5000000L, got!.BtlBwBps);
        Assert.Equal(25000L, got.RtPropUs);
        Assert.Equal(BandwidthConfidence.Medium, got.Confidence);
        Assert.Equal("aether:peer:09", got.PeerUhid);
    }

    [Fact]
    public async Task Handle_Probe_RaisesProbeReceived_WithSource()
    {
        var svc = new BandwidthWireService(new FakeMeshSender(), NullLogger<BandwidthWireService>.Instance);
        BandwidthProbeReceived? got = null;
        svc.ProbeReceived += (_, e) => got = e;
        var pkt = new MeshPacket
        {
            Type = PacketType.BandwidthProbe,
            SourceUhid = "aether:x:01",
            Payload = BandwidthWireCodec.SerializeProbe(new BandwidthProbe(9, 123)),
        };
        Assert.True(await svc.HandleAsync(pkt));
        Assert.NotNull(got);
        Assert.Equal(9u, got!.Probe.Sequence);
        Assert.Equal("aether:x:01", got.FromUhid);
    }

    [Fact]
    public async Task Handle_Ack_RaisesAckReceived()
    {
        var svc = new BandwidthWireService(new FakeMeshSender(), NullLogger<BandwidthWireService>.Instance);
        BandwidthProbeAck? got = null;
        svc.AckReceived += (_, e) => got = e;
        var pkt = new MeshPacket
        {
            Type = PacketType.BandwidthAck,
            SourceUhid = "aether:x:01",
            Payload = BandwidthWireCodec.SerializeAck(new BandwidthProbeAck(3, 10, 20, 30, 0, 64)),
        };
        Assert.True(await svc.HandleAsync(pkt));
        Assert.NotNull(got);
        Assert.Equal(3u, got!.Sequence);
        Assert.Equal(64, got.ProbeBytes);
    }

    [Fact]
    public async Task Handle_WrongType_ReturnsFalse()
    {
        var svc = new BandwidthWireService(new FakeMeshSender(), NullLogger<BandwidthWireService>.Instance);
        Assert.False(await svc.HandleAsync(new MeshPacket { Type = PacketType.Data, Payload = Array.Empty<byte>() }));
    }
}
