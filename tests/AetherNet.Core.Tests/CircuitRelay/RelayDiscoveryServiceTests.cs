// SPDX-License-Identifier: MIT

using AetherNet.CircuitRelay;
using AetherNet.Models;
using AetherNet.Protocol;
using Xunit;

namespace AetherNet.Core.Tests.CircuitRelay;

/// <summary>
/// Native relay discovery — the cold-start gossip. A NAT'd node announces "reach me via relay R"; peers
/// learn dest→relay on their own, so no one has to configure a route by hand.
/// </summary>
public sealed class RelayDiscoveryServiceTests
{
    private const long NowMs = 1_700_000_000_000L;
    private static readonly Func<DateTimeOffset> Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(NowMs);

    private static MeshPacket AnnouncePacket(string target, string relay, long expiryMs) => new()
    {
        Type = PacketType.CircuitRelayControl,
        SourceUhid = target,
        Payload = RelayFrameSerializer.Serialize(new RelayFrame
        {
            Type = RelayMessageType.RouteAnnounce,
            SourceUhid = target,
            RelayUhid = relay,
            ReservationExpiresAtMs = expiryMs,
        }),
    };

    [Fact]
    public async Task Announce_BroadcastsARouteAnnounceFrame()
    {
        byte[]? sent = null;
        var svc = new RelayDiscoveryService("B", (bytes, _) => { sent = bytes; return Task.CompletedTask; }, (_, _, _) => { }, Clock);

        await svc.AnnounceReachabilityAsync("R", NowMs + 900_000);

        Assert.NotNull(sent);
        var frame = RelayFrameSerializer.Deserialize(sent!);
        Assert.Equal(RelayMessageType.RouteAnnounce, frame.Type);
        Assert.Equal("B", frame.SourceUhid);
        Assert.Equal("R", frame.RelayUhid);
    }

    [Fact]
    public void Handle_FreshAnnounceFromPeer_LearnsRoute()
    {
        (string Target, string Relay, long Expiry)? learned = null;
        var svc = new RelayDiscoveryService("A", (_, _) => Task.CompletedTask, (t, r, e) => learned = (t, r, e), Clock);

        var ok = svc.Handle(AnnouncePacket("B", "R", NowMs + 900_000));

        Assert.True(ok);
        Assert.Equal(("B", "R", NowMs + 900_000), learned);
    }

    [Fact]
    public void Handle_OwnAnnounce_IsIgnored()
    {
        var learned = 0;
        var svc = new RelayDiscoveryService("A", (_, _) => Task.CompletedTask, (_, _, _) => learned++, Clock);

        Assert.False(svc.Handle(AnnouncePacket("A", "R", NowMs + 900_000)));
        Assert.Equal(0, learned);
    }

    [Fact]
    public void Handle_ExpiredReservation_IsIgnored()
    {
        var learned = 0;
        var svc = new RelayDiscoveryService("A", (_, _) => Task.CompletedTask, (_, _, _) => learned++, Clock);

        Assert.False(svc.Handle(AnnouncePacket("B", "R", NowMs - 1)));
        Assert.Equal(0, learned);
    }

    [Fact]
    public void Handle_NonRelayPacket_IsIgnored()
    {
        var svc = new RelayDiscoveryService("A", (_, _) => Task.CompletedTask, (_, _, _) => Assert.Fail("must not learn"), Clock);

        Assert.False(svc.Handle(new MeshPacket { Type = PacketType.Data, Payload = [1, 2, 3] }));
    }
}
