// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using Aether.Core.Tests.Fakes;
using Aether.Protocol;
using Aether.Streaming;
using Xunit;

namespace Aether.Core.Tests;

public class AdaptiveBitrateTests
{
    // ─── BitrateLadder rung counts ──────────────────────────────────────────

    [Fact]
    public void BitrateLadder_ProfileA_HasThreeRungs()
    {
        Assert.Equal(3, BitrateLadder.ProfileA.Count);
    }

    [Fact]
    public void BitrateLadder_ProfileB_HasFourRungs()
    {
        Assert.Equal(4, BitrateLadder.ProfileB.Count);
    }

    [Fact]
    public void BitrateLadder_ProfileC_HasFiveRungs()
    {
        Assert.Equal(5, BitrateLadder.ProfileC.Count);
    }

    // ─── Rung selection ─────────────────────────────────────────────────────

    [Fact]
    public void AdaptiveBitrateController_HighBandwidth_SelectsHighestRung()
    {
        // 100 Mbps — well above any rung
        var controller = new AdaptiveBitrateController(StreamProfile.ProfileB, initialBandwidthKbps: 100_000);
        var ladder = BitrateLadder.ProfileB;
        Assert.Equal(ladder[ladder.Count - 1].Label, controller.CurrentRung.Label);
    }

    [Fact]
    public void AdaptiveBitrateController_LowBandwidth_SelectsFloorRung()
    {
        // 200 Kbps — below everything except possibly the floor after 1.2× headroom
        // Profile B floor: 64+800 = 864 Kbps × 1.2 = 1036.8 Kbps. 200 < 1037 → floor.
        var controller = new AdaptiveBitrateController(StreamProfile.ProfileB, initialBandwidthKbps: 200);
        Assert.Equal(BitrateLadder.ProfileB[0].Label, controller.CurrentRung.Label);
    }

    [Fact]
    public void AdaptiveBitrateController_BelowFloor_ShouldAbandon()
    {
        // Profile A floor: 16+200 = 216 Kbps. Set bandwidth to 1 Kbps.
        var controller = new AdaptiveBitrateController(StreamProfile.ProfileA, initialBandwidthKbps: 1);
        Assert.True(controller.ShouldAbandon());
    }

    [Fact]
    public void AdaptiveBitrateController_AboveFloor_ShouldNotAbandon()
    {
        // Profile B floor total = 864 Kbps; with 20% headroom = 1036.8. 2000 > 1037 → no abandon.
        var controller = new AdaptiveBitrateController(StreamProfile.ProfileB, initialBandwidthKbps: 2_000);
        Assert.False(controller.ShouldAbandon());
    }

    [Fact]
    public void AdaptiveBitrateController_UpdateBandwidth_ChangesRung_ReturnsTrue()
    {
        // Start at high bandwidth → top rung. Then drop to floor territory.
        var controller = new AdaptiveBitrateController(StreamProfile.ProfileB, initialBandwidthKbps: 100_000);
        var topRung = controller.CurrentRung;

        // Drop to 200 Kbps — must fall to floor rung (rung 0)
        var changed = controller.UpdateBandwidth(200);

        Assert.True(changed);
        Assert.NotEqual(topRung.Label, controller.CurrentRung.Label);
        Assert.Equal(BitrateLadder.ProfileB[0].Label, controller.CurrentRung.Label);
    }

    [Fact]
    public void AdaptiveBitrateController_UpdateBandwidth_SameRung_ReturnsFalse()
    {
        // Start at 100 Mbps → top rung. Update with another large value that still selects top rung.
        var controller = new AdaptiveBitrateController(StreamProfile.ProfileB, initialBandwidthKbps: 100_000);
        // Update with a different but still very-high value → same rung
        var changed = controller.UpdateBandwidth(50_000);
        Assert.False(changed);
        Assert.Equal(BitrateLadder.ProfileB[BitrateLadder.ProfileB.Count - 1].Label, controller.CurrentRung.Label);
    }

    // ─── StreamingService ABR integration ───────────────────────────────────

    private static (StreamingService svc, FakeMeshSender sender, FakeRoutingService routing) NewService(string localUhid)
    {
        var sender = new FakeMeshSender(localUhid);
        var routing = new FakeRoutingService();
        var svc = new StreamingService(sender, routing);
        return (svc, sender, routing);
    }

    [Fact]
    public async Task StreamingService_GetCurrentBitrateRung_ReturnsRungForProfile()
    {
        var (svc, _, _) = NewService("pub");
        var session = await svc.StartStreamAsync("t", "video/h265", "h265", 2000, StreamProfile.ProfileC);

        var rung = svc.GetCurrentBitrateRung(session.Id);

        Assert.NotNull(rung);
        // C-ladder, initial bandwidth 10 000 Kbps → C-high (128+5000=5128 × 1.2 = 6153.6 < 10000)
        // but C-ultra is 192+9000=9192 × 1.2 = 11030.4 > 10000, so should land at C-high.
        Assert.Equal("C-high", rung!.Label);
    }

    [Fact]
    public async Task StreamingService_GetCurrentBitrateRung_UnknownId_ReturnsNull()
    {
        var (svc, _, _) = NewService("pub");

        Assert.Null(svc.GetCurrentBitrateRung(Guid.NewGuid()));
    }

    [Fact]
    public async Task StreamingService_UpdateBandwidthEstimate_AffectsRungSelection()
    {
        var (svc, _, _) = NewService("pub");
        var session = await svc.StartStreamAsync("t", "video/h264", "h264", 2000, StreamProfile.ProfileB);

        // Drop bandwidth to 200 Kbps → floor rung
        var changed = svc.UpdateBandwidthEstimate(session.Id, 200);

        Assert.True(changed);
        Assert.Equal("B-low", svc.GetCurrentBitrateRung(session.Id)!.Label);
    }

    [Fact]
    public async Task StreamingService_PublishSegment_BelowFloorBandwidth_EmitsAbandonPacket()
    {
        var (svc, sender, routing) = NewService("pub");
        var session = await svc.StartStreamAsync("t", "video/h264", "h264", 2000, StreamProfile.ProfileB);

        // Add a subscriber so the service has someone to fan-out to
        const string sub = "sub-1";
        routing.SetRoute(sub, sub);
        var subscribePayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new Aether.Streaming.Models.StreamSubscribePayload { StreamId = session.Id, LiveOnly = true },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });
        await svc.HandleAsync(new MeshPacket
        {
            Type = PacketType.StreamSubscribe,
            SourceUhid = sub,
            Payload = subscribePayload,
        });

        // Set bandwidth below the Profile B floor (floor = 64+800 = 864 Kbps)
        svc.UpdateBandwidthEstimate(session.Id, 1); // 1 Kbps — well below floor
        sender.Clear();

        await svc.PublishSegmentAsync(session.Id, new byte[] { 0xDE, 0xAD }, sequence: 1, isKeyframe: false);

        // Must emit StreamAbandon — NOT StreamSegment
        var broadcasts = sender.Broadcasts.ToList();
        Assert.DoesNotContain(broadcasts, p => p.Type == PacketType.StreamSegment);
        Assert.DoesNotContain(sender.Unicasts.ToList(), u => u.Packet.Type == PacketType.StreamSegment);

        var abandon = broadcasts.SingleOrDefault(p => p.Type == PacketType.StreamAbandon);
        Assert.NotNull(abandon);

        // Verify the abandon payload contains our stream id and sequence
        var span = abandon!.Payload.AsSpan();
        Assert.True(span.Length >= 21); // 16 + 4 + 1
        var parsedId = new Guid(span[..16], bigEndian: true);
        Assert.Equal(session.Id, parsedId);
        var parsedSeq = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(16, 4));
        Assert.Equal(1u, parsedSeq);
        Assert.Equal(0, span[20]); // reason = congestion
    }
}
