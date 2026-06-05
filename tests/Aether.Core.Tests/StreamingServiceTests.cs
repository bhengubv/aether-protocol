// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Text.Json;
using AetherMesh.Core.Tests.Fakes;
using AetherMesh.Protocol;
using AetherMesh.Streaming;
using AetherMesh.Streaming.Models;
using Xunit;

namespace AetherMesh.Core.Tests;

public class StreamingServiceTests
{
    private const string Publisher = "publisher-uhid";
    private const string Subscriber = "subscriber-uhid";
    private const string Subscriber2 = "subscriber-2";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static (StreamingService svc, FakeMeshSender sender, FakeRoutingService routing) NewService(string localUhid)
    {
        var sender = new FakeMeshSender(localUhid);
        var routing = new FakeRoutingService();
        var svc = new StreamingService(sender, routing);
        return (svc, sender, routing);
    }

    private static MeshPacket BuildSubscribePacket(string subscriberUhid, Guid streamId)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new StreamSubscribePayload
        {
            StreamId = streamId,
            LiveOnly = true,
        }, JsonOptions);

        return new MeshPacket
        {
            Type = PacketType.StreamSubscribe,
            SourceUhid = subscriberUhid,
            DestinationUhid = Publisher,
            Ttl = 7,
            Payload = payload,
        };
    }

    private static MeshPacket BuildUnsubscribePacket(string subscriberUhid, Guid streamId)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new StreamUnsubscribePayload
        {
            StreamId = streamId,
        }, JsonOptions);

        return new MeshPacket
        {
            Type = PacketType.StreamUnsubscribe,
            SourceUhid = subscriberUhid,
            DestinationUhid = Publisher,
            Ttl = 7,
            Payload = payload,
        };
    }

    private static MeshPacket CaptureAnnouncePacket(FakeMeshSender sender)
        => sender.Broadcasts.Single(p => p.Type == PacketType.StreamAnnounce);

    /// <summary>
    /// Mirror of the (internal) <c>StreamingService.TryDeserializeSegment</c> wire format.
    /// Reproduced here because the production helper is internal-static.
    /// Wire layout: [16] StreamId BE | [4] Sequence LE | [8] Timestamp LE | [1] IsKeyframe | [N] payload.
    /// </summary>
    private static (Guid StreamId, uint Sequence, long TimestampMs, bool IsKeyframe, byte[] Encoded) DecodeSegment(byte[] payload)
    {
        Assert.True(payload.Length >= 29);
        var span = payload.AsSpan();
        var streamId = new Guid(span[..16], bigEndian: true);
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4));
        var timestampMs = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(20, 8));
        var isKeyframe = span[28] == 1;
        var encoded = span[29..].ToArray();
        return (streamId, sequence, timestampMs, isKeyframe, encoded);
    }

    // ─── StartStreamAsync ─────────────────────────────────────────

    [Fact]
    public async Task StartStreamAsync_CreatesPublisherSessionAndBroadcastsAnnounce()
    {
        var (svc, sender, _) = NewService(Publisher);

        var session = await svc.StartStreamAsync("title", "video/h264", "h264", 2000);

        Assert.NotEqual(Guid.Empty, session.Id);
        Assert.Equal(Publisher, session.PublisherUhid);
        Assert.Equal(StreamRole.Publisher, session.Role);
        Assert.Equal(StreamState.Live, session.State);
        Assert.Single(svc.GetActiveStreams());

        var announce = CaptureAnnouncePacket(sender);
        Assert.Equal(Publisher, announce.SourceUhid);
        var body = JsonSerializer.Deserialize<StreamAnnouncePayload>(announce.Payload, JsonOptions)!;
        Assert.Equal(session.Id, body.StreamId);
        Assert.Equal("title", body.Title);
        Assert.Equal(StreamState.Live, body.State);
    }

    [Fact]
    public async Task StartStreamAsync_RejectsEmptyTitle()
    {
        var (svc, _, _) = NewService(Publisher);

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            svc.StartStreamAsync(string.Empty, "video/h264", "h264", 2000));
    }

    // ─── Subscribers + segment flow ────────────────────────────────

    [Fact]
    public async Task HandleSubscribe_RegistersSubscriberAndRaisesEvent()
    {
        var (svc, sender, _) = NewService(Publisher);
        var session = await svc.StartStreamAsync("t", "video/h264", "h264", 2000);
        sender.Clear();

        SubscriberJoinedEventArgs? observed = null;
        svc.SubscriberJoined += (_, e) => observed = e;

        await svc.HandleAsync(BuildSubscribePacket(Subscriber, session.Id));

        Assert.NotNull(observed);
        Assert.Equal(session.Id, observed!.StreamId);
        Assert.Equal(Subscriber, observed.SubscriberUhid);
    }

    [Fact]
    public async Task PublishSegmentAsync_FansOutToAllSubscribers()
    {
        var (svc, sender, routing) = NewService(Publisher);
        var session = await svc.StartStreamAsync("t", "video/h264", "h264", 2000);
        await svc.HandleAsync(BuildSubscribePacket(Subscriber, session.Id));
        await svc.HandleAsync(BuildSubscribePacket(Subscriber2, session.Id));

        // Direct routes — exercises the unicast path explicitly.
        routing.SetRoute(Subscriber, Subscriber);
        routing.SetRoute(Subscriber2, Subscriber2);
        sender.Clear();

        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        await svc.PublishSegmentAsync(session.Id, payload, sequence: 1, isKeyframe: true);

        var unicasts = sender.Unicasts
            .Where(u => u.Packet.Type == PacketType.StreamSegment)
            .ToList();
        Assert.Equal(2, unicasts.Count);
        Assert.Contains(unicasts, u => u.NextHopUhid == Subscriber);
        Assert.Contains(unicasts, u => u.NextHopUhid == Subscriber2);

        // Each segment carries the publisher's stream id and our payload bytes.
        foreach (var (pkt, _) in unicasts)
        {
            var (streamId, sequence, _, isKeyframe, encoded) = DecodeSegment(pkt.Payload);
            Assert.Equal(session.Id, streamId);
            Assert.Equal(1u, sequence);
            Assert.True(isKeyframe);
            Assert.Equal(payload, encoded);
        }
    }

    [Fact]
    public async Task PublishSegmentAsync_FallsBackToBroadcastWhenNoRoute()
    {
        var (svc, sender, _) = NewService(Publisher);
        var session = await svc.StartStreamAsync("t", "video/h264", "h264", 2000);
        await svc.HandleAsync(BuildSubscribePacket(Subscriber, session.Id));
        sender.Clear();

        await svc.PublishSegmentAsync(session.Id, new byte[] { 1, 2, 3 }, 1, false);

        // No route was set on the FakeRoutingService — broadcast fallback expected.
        Assert.Single(sender.Broadcasts);
        Assert.Equal(PacketType.StreamSegment, sender.Broadcasts.First().Type);
    }

    [Fact]
    public async Task PublishSegmentAsync_NoSubscribers_DoesNotSendAnything()
    {
        var (svc, sender, _) = NewService(Publisher);
        var session = await svc.StartStreamAsync("t", "video/h264", "h264", 2000);
        sender.Clear();

        await svc.PublishSegmentAsync(session.Id, new byte[] { 1, 2 }, 1, false);

        Assert.Empty(sender.Broadcasts);
        Assert.Empty(sender.Unicasts);
    }

    [Fact]
    public async Task PublishSegmentAsync_UnknownStreamId_IsNoOp()
    {
        var (svc, sender, _) = NewService(Publisher);

        await svc.PublishSegmentAsync(Guid.NewGuid(), new byte[] { 1 }, 1, false);

        Assert.Empty(sender.Broadcasts);
        Assert.Empty(sender.Unicasts);
    }

    // ─── End-to-end across two services ────────────────────────────

    [Fact]
    public async Task EndToEnd_AnnounceSubscribeSegmentEndStream_PropagatesToSubscriber()
    {
        // Two services share the same in-memory transport via test-side wiring.
        var (publisherSvc, publisherSender, publisherRouting) = NewService(Publisher);
        var (subscriberSvc, subscriberSender, _) = NewService(Subscriber);

        // 1. Publisher starts → captures announce packet from broadcast log.
        var session = await publisherSvc.StartStreamAsync("end-to-end", "video/h264", "h264", 2000);
        var announce = publisherSender.Broadcasts.Single(p => p.Type == PacketType.StreamAnnounce);

        // 2. Deliver announce to subscriber → subscriber learns about stream.
        StreamSession? announced = null;
        subscriberSvc.StreamAnnounced += (_, s) => announced = s;
        await subscriberSvc.HandleAsync(announce);
        Assert.NotNull(announced);
        Assert.Equal(session.Id, announced!.Id);

        // 3. Subscriber subscribes → captures the StreamSubscribe packet from its own outbox.
        await subscriberSvc.SubscribeAsync(session.Id);
        var subscribe = subscriberSender.Broadcasts
            .Concat(subscriberSender.Unicasts.Select(u => u.Packet))
            .First(p => p.Type == PacketType.StreamSubscribe);
        await publisherSvc.HandleAsync(subscribe);

        // 4. Publisher publishes a segment → captured on publisher transport.
        publisherRouting.SetRoute(Subscriber, Subscriber);
        publisherSender.Clear();
        var encoded = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };
        await publisherSvc.PublishSegmentAsync(session.Id, encoded, sequence: 42, isKeyframe: true);
        var segPacket = publisherSender.Unicasts
            .Single(u => u.Packet.Type == PacketType.StreamSegment).Packet;

        // 5. Subscriber receives segment → SegmentReceived event fires with our bytes.
        StreamSegment? received = null;
        subscriberSvc.SegmentReceived += (_, s) => received = s;
        await subscriberSvc.HandleAsync(segPacket);
        Assert.NotNull(received);
        Assert.Equal(session.Id, received!.StreamId);
        Assert.Equal(42u, received.Sequence);
        Assert.True(received.IsKeyframe);
        Assert.Equal(encoded, received.EncodedPayload);

        // 6. Publisher ends → final announce with state=Ended is delivered to subscriber.
        publisherSender.Clear();
        await publisherSvc.EndStreamAsync(session.Id);
        var endAnnounce = publisherSender.Broadcasts.Single(p => p.Type == PacketType.StreamAnnounce);
        StreamSession? endedOnSubscriber = null;
        subscriberSvc.StreamEnded += (_, s) => endedOnSubscriber = s;
        await subscriberSvc.HandleAsync(endAnnounce);
        Assert.NotNull(endedOnSubscriber);
        Assert.Equal(StreamState.Ended, endedOnSubscriber!.State);
    }

    // ─── EndStream / late subscribers / unsubscribe ────────────────

    [Fact]
    public async Task EndStreamAsync_TransitionsStateAndRebroadcastsAnnounce()
    {
        var (svc, sender, _) = NewService(Publisher);
        var session = await svc.StartStreamAsync("t", "video/h264", "h264", 2000);
        sender.Clear();

        await svc.EndStreamAsync(session.Id);

        Assert.Equal(StreamState.Ended, session.State);
        Assert.NotNull(session.EndedAt);
        var endAnnounce = sender.Broadcasts.Single(p => p.Type == PacketType.StreamAnnounce);
        var body = JsonSerializer.Deserialize<StreamAnnouncePayload>(endAnnounce.Payload, JsonOptions)!;
        Assert.Equal(StreamState.Ended, body.State);
    }

    [Fact]
    public async Task PublishSegmentAsync_AfterEndStream_DoesNotEmitFurtherPackets()
    {
        var (svc, sender, routing) = NewService(Publisher);
        var session = await svc.StartStreamAsync("t", "video/h264", "h264", 2000);
        await svc.HandleAsync(BuildSubscribePacket(Subscriber, session.Id));
        routing.SetRoute(Subscriber, Subscriber);

        await svc.EndStreamAsync(session.Id);
        sender.Clear();

        // After EndStream the session.State is Ended → segment publishes are silently dropped.
        await svc.PublishSegmentAsync(session.Id, new byte[] { 1 }, 99, false);

        Assert.DoesNotContain(sender.Unicasts, u => u.Packet.Type == PacketType.StreamSegment);
        Assert.DoesNotContain(sender.Broadcasts, p => p.Type == PacketType.StreamSegment);
    }

    [Fact]
    public async Task HandleUnsubscribe_RemovesSubscriberAndRaisesEvent()
    {
        var (svc, _, _) = NewService(Publisher);
        var session = await svc.StartStreamAsync("t", "video/h264", "h264", 2000);
        await svc.HandleAsync(BuildSubscribePacket(Subscriber, session.Id));

        SubscriberLeftEventArgs? observed = null;
        svc.SubscriberLeft += (_, e) => observed = e;

        await svc.HandleAsync(BuildUnsubscribePacket(Subscriber, session.Id));

        Assert.NotNull(observed);
        Assert.Equal(session.Id, observed!.StreamId);
        Assert.Equal(Subscriber, observed.SubscriberUhid);
    }

    [Fact]
    public async Task SubscribeAsync_UnknownStreamId_IsNoOp()
    {
        var (svc, sender, _) = NewService(Subscriber);

        await svc.SubscribeAsync(Guid.NewGuid());

        Assert.Empty(sender.Broadcasts);
        Assert.Empty(sender.Unicasts);
    }

    [Fact]
    public async Task HandleAsync_NonStreamPacketType_IsIgnored()
    {
        var (svc, _, _) = NewService(Publisher);
        var pkt = new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = "other",
            Payload = new byte[] { 0 },
        };

        // Should not throw and should remain a no-op.
        await svc.HandleAsync(pkt);
        Assert.Empty(svc.GetActiveStreams());
    }
}
