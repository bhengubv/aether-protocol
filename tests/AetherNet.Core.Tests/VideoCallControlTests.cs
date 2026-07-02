// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.VideoCallControl;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for <see cref="VideoCallControlService"/> (PacketType.VideoCall call-control).
/// Directed signalling — a fake <see cref="IMeshSender"/> captures directed sends.
/// </summary>
public sealed class VideoCallControlTests
{
    private sealed class FakeMeshSender : IMeshSender
    {
        public string LocalUhid { get; set; } = "aether:local:01";
        public List<(MeshPacket Packet, string NextHop)> Sends { get; } = [];

        public IReadOnlyList<PeerInfo> GetConnectedPeers() => Array.Empty<PeerInfo>();
        public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken ct = default)
        {
            Sends.Add((packet, nextHopUhid));
            return Task.FromResult(true);
        }
        public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken ct = default) => Task.FromResult(0);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static VideoCallControlService Build(FakeMeshSender sender)
        => new(sender, NullLogger<VideoCallControlService>.Instance);

    private static MeshPacket ControlPacket(Guid callId, string action, string fromUhid) => new()
    {
        Type = PacketType.VideoCall,
        SourceUhid = fromUhid,
        DestinationUhid = "aether:local:01",
        Payload = JsonSerializer.SerializeToUtf8Bytes(
            new VideoCallControlPayload { CallId = callId, Action = action, SentAtMs = 1L }, JsonOpts),
    };

    [Theory]
    [InlineData("0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f", "ring", 1700000000000L,
        "{\"call_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"action\":\"ring\",\"sent_at_ms\":1700000000000}")]
    [InlineData("00000000-0000-0000-0000-000000000000", "hangup", 0L,
        "{\"call_id\":\"00000000-0000-0000-0000-000000000000\",\"action\":\"hangup\",\"sent_at_ms\":0}")]
    public void VideoCallControlPayload_SerializesToCanonicalBytes(string callId, string action, long ms, string expected)
    {
        var payload = new VideoCallControlPayload { CallId = Guid.Parse(callId), Action = action, SentAtMs = ms };
        var json = System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        Assert.Equal(expected, json);
    }

    [Fact]
    public async Task Ring_SendsDirectedRingToPeer_AndReturnsCallId()
    {
        var sender = new FakeMeshSender { LocalUhid = "aether:alice:01" };
        var svc = Build(sender);

        var callId = await svc.RingAsync("aether:bob:02");

        Assert.NotEqual(Guid.Empty, callId);
        var sent = Assert.Single(sender.Sends);
        Assert.Equal(PacketType.VideoCall, sent.Packet.Type);
        Assert.Equal("aether:bob:02", sent.NextHop);
        var body = JsonSerializer.Deserialize<VideoCallControlPayload>(sent.Packet.Payload, JsonOpts)!;
        Assert.Equal("ring", body.Action);
        Assert.Equal(callId, body.CallId);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    [InlineData("hangup")]
    public async Task Respond_SendsDirectedActionToPeer(string action)
    {
        var sender = new FakeMeshSender();
        var svc = Build(sender);
        var callId = Guid.NewGuid();

        var ok = action switch
        {
            "accept" => await svc.AcceptAsync(callId, "aether:bob:02"),
            "decline" => await svc.DeclineAsync(callId, "aether:bob:02"),
            _ => await svc.HangupAsync(callId, "aether:bob:02"),
        };

        Assert.True(ok);
        var sent = Assert.Single(sender.Sends);
        Assert.Equal("aether:bob:02", sent.NextHop);
        var body = JsonSerializer.Deserialize<VideoCallControlPayload>(sent.Packet.Payload, JsonOpts)!;
        Assert.Equal(action, body.Action);
        Assert.Equal(callId, body.CallId);
    }

    [Fact]
    public async Task Handle_RaisesCallStateChanged()
    {
        var svc = Build(new FakeMeshSender { LocalUhid = "aether:local:01" });
        VideoCallStateChanged? got = null;
        svc.CallStateChanged += (_, e) => got = e;

        var callId = Guid.NewGuid();
        var ok = await svc.HandleAsync(ControlPacket(callId, "ring", "aether:bob:02"));

        Assert.True(ok);
        Assert.NotNull(got);
        Assert.Equal(callId, got!.CallId);
        Assert.Equal("ring", got.Action);
        Assert.Equal("aether:bob:02", got.FromUhid);
    }

    [Fact]
    public async Task Handle_WrongPacketType_ReturnsFalse()
    {
        var svc = Build(new FakeMeshSender());
        var pkt = ControlPacket(Guid.NewGuid(), "ring", "aether:bob:02");
        pkt.Type = PacketType.Data;
        Assert.False(await svc.HandleAsync(pkt));
    }
}
