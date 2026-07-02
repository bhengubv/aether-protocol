// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Channels;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Unit tests for <see cref="ChannelMessageService"/> (PacketType.ChannelMessage). Fake
/// <see cref="IMeshSender"/> captures broadcasts — no transport needed.
/// </summary>
public sealed class ChannelMessageTests
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

    private static ChannelMessageService Build(FakeMeshSender sender)
        => new(sender, NullLogger<ChannelMessageService>.Instance);

    private static MeshPacket ChannelPacket(
        string channelId, Guid messageId, string sender, string content, long sentAtMs, int ttl = 7) => new()
    {
        Type = PacketType.ChannelMessage,
        SourceUhid = sender,
        DestinationUhid = "*",
        Ttl = ttl,
        Payload = JsonSerializer.SerializeToUtf8Bytes(new ChannelMessagePayload
        {
            ChannelId = channelId,
            MessageId = messageId,
            SenderUhid = sender,
            Content = content,
            SentAtMs = sentAtMs,
        }, JsonOpts),
    };

    [Theory]
    [InlineData("res-floor-3", "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f", "aether:alice:01", "meeting at 6", 1700000000000L,
        "{\"channel_id\":\"res-floor-3\",\"message_id\":\"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f\",\"sender_uhid\":\"aether:alice:01\",\"content\":\"meeting at 6\",\"sent_at_ms\":1700000000000}")]
    [InlineData("g", "00000000-0000-0000-0000-000000000000", "n", "", 0L,
        "{\"channel_id\":\"g\",\"message_id\":\"00000000-0000-0000-0000-000000000000\",\"sender_uhid\":\"n\",\"content\":\"\",\"sent_at_ms\":0}")]
    public void ChannelMessagePayload_SerializesToCanonicalBytes(
        string channelId, string messageId, string sender, string content, long sentAtMs, string expected)
    {
        var payload = new ChannelMessagePayload
        {
            ChannelId = channelId,
            MessageId = Guid.Parse(messageId),
            SenderUhid = sender,
            Content = content,
            SentAtMs = sentAtMs,
        };
        var json = System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        Assert.Equal(expected, json);
    }

    [Fact]
    public async Task Publish_BroadcastsChannelMessage()
    {
        var sender = new FakeMeshSender { LocalUhid = "aether:alice:01" };
        var svc = Build(sender);

        await svc.PublishAsync("res-floor-3", "meeting at 6");

        var pkt = Assert.Single(sender.Broadcasts);
        Assert.Equal(PacketType.ChannelMessage, pkt.Type);
        var body = JsonSerializer.Deserialize<ChannelMessagePayload>(pkt.Payload, JsonOpts)!;
        Assert.Equal("res-floor-3", body.ChannelId);
        Assert.Equal("meeting at 6", body.Content);
        Assert.Equal("aether:alice:01", body.SenderUhid);
    }

    [Fact]
    public async Task Handle_SubscribedChannel_RaisesEvent()
    {
        var svc = Build(new FakeMeshSender { LocalUhid = "aether:local:01" });
        svc.Subscribe("res-floor-3");

        ChannelMessageReceived? got = null;
        svc.MessageReceived += (_, e) => got = e;

        var ok = await svc.HandleAsync(
            ChannelPacket("res-floor-3", Guid.NewGuid(), "aether:bob:02", "hello floor", 1700000000000L));

        Assert.True(ok);
        Assert.NotNull(got);
        Assert.Equal("res-floor-3", got!.ChannelId);
        Assert.Equal("hello floor", got.Content);
        Assert.Equal("aether:bob:02", got.SenderUhid);
    }

    [Fact]
    public async Task Handle_UnsubscribedChannel_NoEventButProcessed()
    {
        var svc = Build(new FakeMeshSender { LocalUhid = "aether:local:01" });
        var raised = false;
        svc.MessageReceived += (_, _) => raised = true;

        var ok = await svc.HandleAsync(
            ChannelPacket("society-x", Guid.NewGuid(), "aether:bob:02", "hi", 1L));

        Assert.True(ok);     // processed + relayed
        Assert.False(raised); // but not surfaced — we aren't subscribed
    }

    [Fact]
    public async Task Handle_DuplicateMessageId_ReturnsFalse()
    {
        var svc = Build(new FakeMeshSender { LocalUhid = "aether:local:01" });
        svc.Subscribe("res-floor-3");
        var id = Guid.NewGuid();

        var events = 0;
        svc.MessageReceived += (_, _) => events++;

        Assert.True(await svc.HandleAsync(ChannelPacket("res-floor-3", id, "aether:bob:02", "one", 1L)));
        Assert.False(await svc.HandleAsync(ChannelPacket("res-floor-3", id, "aether:bob:02", "one", 1L)));
        Assert.Equal(1, events);
    }

    [Fact]
    public async Task Handle_WrongPacketType_ReturnsFalse()
    {
        var svc = Build(new FakeMeshSender());
        var pkt = ChannelPacket("res-floor-3", Guid.NewGuid(), "aether:bob:02", "x", 1L);
        pkt.Type = PacketType.Data;
        Assert.False(await svc.HandleAsync(pkt));
    }

    [Fact]
    public async Task Handle_RelaysWhenTtlAllows()
    {
        var relaySender = new FakeMeshSender { LocalUhid = "aether:relay:09" };
        var svc = Build(relaySender); // not subscribed — pure relay
        await svc.HandleAsync(ChannelPacket("res-floor-3", Guid.NewGuid(), "aether:bob:02", "hop", 1L, ttl: 5));

        var relayed = Assert.Single(relaySender.Broadcasts);
        Assert.Equal(PacketType.ChannelMessage, relayed.Type);
        Assert.Equal(4, relayed.Ttl);
    }
}
