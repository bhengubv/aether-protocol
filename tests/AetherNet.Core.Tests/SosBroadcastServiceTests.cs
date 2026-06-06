// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Constants;
using AetherNet.Core.Tests.Fakes;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Sos;
using Xunit;

namespace AetherNet.Core.Tests;

public class SosBroadcastServiceTests
{
    private const string Local = "local-uhid";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static (SosBroadcastService svc, FakeMeshSender sender) NewService(string localUhid = Local)
    {
        var sender = new FakeMeshSender(localUhid);
        var svc = new SosBroadcastService(sender);
        return (svc, sender);
    }

    private static MeshPacket BuildSosPacketFromOther(string sourceUhid, Guid? broadcastId = null, int ttl = ProtocolConstants.SosTtl)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            broadcast_id = broadcastId ?? Guid.NewGuid(),
            broadcast_type = "sos",
            message = (string?)"help",
            latitude = -33.9,
            longitude = 18.4,
            geohash = (string?)null,
        }, JsonOptions);

        return new MeshPacket
        {
            Type = PacketType.SosBroadcast,
            SourceUhid = sourceUhid,
            DestinationUhid = string.Empty,
            Ttl = ttl,
            Priority = ProtocolConstants.SosPriority,
            Payload = body,
        };
    }

    // ─── BroadcastSosAsync ────────────────────────────────────

    [Fact]
    public async Task BroadcastSosAsync_FloodsAndStoresAlert()
    {
        var (svc, sender) = NewService();

        var ok = await svc.BroadcastSosAsync("sos", "help", -33.9, 18.4);

        Assert.True(ok);
        Assert.Single(sender.Broadcasts);
        var pkt = sender.Broadcasts.First();
        Assert.Equal(PacketType.SosBroadcast, pkt.Type);
        Assert.Equal(ProtocolConstants.SosTtl, pkt.Ttl);
        Assert.Equal(ProtocolConstants.SosPriority, pkt.Priority);
        Assert.Single(svc.GetActiveAlerts());
    }

    [Fact]
    public async Task BroadcastSosAsync_RateLimitedAfterMax()
    {
        var (svc, _) = NewService();

        for (var i = 0; i < ProtocolConstants.MaxSosBroadcastsPerHour; i++)
        {
            Assert.True(await svc.BroadcastSosAsync("sos", "help", 0, 0));
        }

        // Next one in the same rolling hour must be rejected.
        Assert.False(await svc.BroadcastSosAsync("sos", "help", 0, 0));
    }

    [Fact]
    public async Task BroadcastSosAsync_RejectsEmptyBroadcastType()
    {
        var (svc, _) = NewService();

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            svc.BroadcastSosAsync(string.Empty, "help", 0, 0));
    }

    // ─── HandleAsync ──────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_DropsDuplicatePacketId()
    {
        var (svc, sender) = NewService();
        var pkt = BuildSosPacketFromOther("alice");

        await svc.HandleAsync(pkt);
        sender.Clear();
        var alertsAfterFirst = svc.GetActiveAlerts().Count;

        await svc.HandleAsync(pkt);

        // Same id => no re-broadcast and no extra alert.
        Assert.Empty(sender.Broadcasts);
        Assert.Equal(alertsAfterFirst, svc.GetActiveAlerts().Count);
    }

    [Fact]
    public async Task HandleAsync_IgnoresSelfOriginated()
    {
        var (svc, sender) = NewService();
        var pkt = BuildSosPacketFromOther(Local);

        await svc.HandleAsync(pkt);

        Assert.Empty(sender.Broadcasts);
    }

    [Fact]
    public async Task HandleAsync_RaisesSosReceived()
    {
        var (svc, _) = NewService();
        SosAlert? observed = null;
        svc.SosReceived += (_, alert) => observed = alert;

        var pkt = BuildSosPacketFromOther("alice");
        await svc.HandleAsync(pkt);

        Assert.NotNull(observed);
        Assert.Equal("alice", observed!.SenderUhid);
        Assert.Equal("sos", observed.BroadcastType);
    }

    [Fact]
    public async Task HandleAsync_RebroadcastsWhenTtlAllows()
    {
        var (svc, sender) = NewService();
        var pkt = BuildSosPacketFromOther("alice", ttl: 5);

        await svc.HandleAsync(pkt);

        Assert.Single(sender.Broadcasts);
        var fwd = sender.Broadcasts.First();
        Assert.Equal(4, fwd.Ttl); // decremented before re-broadcast
    }

    [Fact]
    public async Task HandleAsync_DoesNotRebroadcastWhenTtlExhausted()
    {
        var (svc, sender) = NewService();
        var pkt = BuildSosPacketFromOther("alice", ttl: 1);

        await svc.HandleAsync(pkt);

        Assert.Empty(sender.Broadcasts);
    }

    [Fact]
    public async Task HandleAsync_RejectsWrongPacketType()
    {
        var (svc, _) = NewService();
        var pkt = new MeshPacket
        {
            Type = PacketType.Data,
            SourceUhid = "alice",
        };

        await Assert.ThrowsAsync<ArgumentException>(() => svc.HandleAsync(pkt));
    }

    // ─── Resolve ──────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_RemovesAlertAndRaisesEvent()
    {
        var (svc, _) = NewService();
        Guid? resolvedId = null;
        svc.SosResolved += (_, id) => resolvedId = id;

        await svc.BroadcastSosAsync("sos", "help", 0, 0);
        var alert = svc.GetActiveAlerts().Single();

        await svc.ResolveAsync(alert.Id);

        Assert.Empty(svc.GetActiveAlerts());
        Assert.Equal(alert.Id, resolvedId);
    }

    [Fact]
    public async Task ResolveAsync_UnknownIdIsNoOp()
    {
        var (svc, _) = NewService();
        var raised = false;
        svc.SosResolved += (_, _) => raised = true;

        await svc.ResolveAsync(Guid.NewGuid());

        Assert.False(raised);
    }
}
