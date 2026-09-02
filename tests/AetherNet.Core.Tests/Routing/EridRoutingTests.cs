// SPDX-License-Identifier: MIT

using AetherNet.Core.Tests.Fakes;
using AetherNet.Constants;
using AetherNet.Identity;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using Xunit;

namespace AetherNet.Core.Tests.Routing;

/// <summary>
/// E1 (route tables keyed on ERID, TTL ≤ epoch) + E3 (reputation/incentive on long-term identity,
/// never the wire ERID). The routing layer resolves a received ERID to the stable long-term UHID via
/// <see cref="EridRouteResolver"/>, keys the route on that stable identity, and caps its lifetime at the
/// epoch boundary. With no directory the resolver is a pass-through, so a node that has not negotiated
/// ERID routing behaves exactly as before.
/// </summary>
public class EridRoutingTests
{
    // ─── Epoch-boundary primitive ───────────────────────────────────────────

    [Theory]
    [InlineData(0, 900, 900)]
    [InlineData(1, 900, 900)]
    [InlineData(899, 900, 900)]
    [InlineData(900, 900, 1800)]
    [InlineData(1000, 900, 1800)]
    [InlineData(1_700_000_000, 900, 1_700_000_100)] // 1_700_000_000 = 1888888*900 + 800 → next boundary +100
    public void EpochEndUnixSeconds_IsTheStartOfTheNextWindow(long now, int epochSeconds, long expectedEnd)
        => Assert.Equal(expectedEnd, EphemeralRoutingId.EpochEndUnixSeconds(now, epochSeconds));

    // ─── Epoch-bounded route TTL ─────────────────────────────────────────────

    [Fact]
    public void RefreshBoundedBy_UsesExpiry_WhenTheBoundIsFurtherOut()
    {
        var route = new RouteEntry();
        route.RefreshBoundedBy(300, DateTime.UtcNow.AddHours(1));
        Assert.InRange(route.ExpiresAt, DateTime.UtcNow.AddSeconds(295), DateTime.UtcNow.AddSeconds(305));
    }

    [Fact]
    public void RefreshBoundedBy_CapsAtTheBound_WhenTheEpochEndsSooner()
    {
        var route = new RouteEntry();
        var epochEnd = DateTime.UtcNow.AddSeconds(30); // epoch rotates well before the 300s TTL
        route.RefreshBoundedBy(300, epochEnd);
        Assert.Equal(epochEnd, route.ExpiresAt);
    }

    // ─── Resolver ────────────────────────────────────────────────────────────

    [Fact]
    public void Resolve_IsPassThrough_WithNoDirectory()
    {
        var resolver = new EridRouteResolver();
        var addr = resolver.Resolve("plain-uhid");
        Assert.Equal("plain-uhid", addr.StableUhid);
        Assert.False(addr.WasErid);
        Assert.Null(addr.EpochExpiryUtc);
        Assert.False(resolver.Enabled);
    }

    [Fact]
    public void Resolve_ReturnsUhidUnchanged_ForAnAddressThatIsNotAKnownErid()
    {
        var (dir, _, now) = Directory();
        var resolver = new EridRouteResolver(dir, nowUnixSeconds: () => now);
        var addr = resolver.Resolve("some-plain-uhid");
        Assert.Equal("some-plain-uhid", addr.StableUhid);
        Assert.False(addr.WasErid);
    }

    [Fact]
    public void Resolve_TurnsAKnownPeersCurrentErid_IntoItsStableUhid_WithEpochExpiry()
    {
        var (dir, peerUhid, now) = Directory();
        var wireErid = dir.EridForPeer(peerUhid, now);
        Assert.NotNull(wireErid);

        var resolver = new EridRouteResolver(dir, nowUnixSeconds: () => now);
        var addr = resolver.Resolve(wireErid!);

        Assert.Equal(peerUhid, addr.StableUhid);
        Assert.True(addr.WasErid);
        var expectedEnd = DateTimeOffset.FromUnixTimeSeconds(
            EphemeralRoutingId.EpochEndUnixSeconds(now)).UtcDateTime;
        Assert.Equal(expectedEnd, addr.EpochExpiryUtc);
    }

    // ─── Routing integration (E1 + E3) ───────────────────────────────────────

    [Fact]
    public async Task HandleRouteRequest_KeysTheRouteOnTheStableUhid_NotTheWireErid()
    {
        var (dir, peerUhid, now) = Directory();
        var wireErid = dir.EridForPeer(peerUhid, now)!;
        var resolver = new EridRouteResolver(dir, nowUnixSeconds: () => now);

        var sender = new FakeMeshSender("local-uhid");
        var store = new InMemoryRouteStore();
        var svc = new RoutingService(sender, store, routeResolver: resolver);

        var rreq = new MeshPacket
        {
            Id = Guid.NewGuid(),
            Type = PacketType.RouteRequest,
            SourceUhid = wireErid,     // the wire carries the rotating ERID
            DestinationUhid = "somewhere-else",
            Ttl = ProtocolConstants.DefaultTtl,
        };

        await svc.HandleRouteRequestAsync(rreq);

        // The route is keyed on the long-term identity, so a caller who knows the peer's UHID can find
        // it — and it does NOT leak the rotating ERID into the table.
        var byUhid = await store.GetAsync(peerUhid);
        Assert.NotNull(byUhid);
        Assert.Equal(peerUhid, byUhid!.NextHopUhid);

        var byErid = await store.GetAsync(wireErid);
        Assert.Null(byErid);
    }

    [Fact]
    public async Task HandleRouteRequest_WithNoResolver_StillKeysOnTheWireSource()
    {
        // Regression guard: a node without ERID routing behaves exactly as before.
        var sender = new FakeMeshSender("local-uhid");
        var store = new InMemoryRouteStore();
        var svc = new RoutingService(sender, store);

        var rreq = new MeshPacket
        {
            Id = Guid.NewGuid(),
            Type = PacketType.RouteRequest,
            SourceUhid = "alice",
            DestinationUhid = "bob",
            Ttl = ProtocolConstants.DefaultTtl,
        };

        await svc.HandleRouteRequestAsync(rreq);

        var route = await store.GetAsync("alice");
        Assert.NotNull(route);
        Assert.Equal("alice", route!.NextHopUhid);
        Assert.False(route.IsExpired);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static (EridDirectory dir, string peerUhid, long now) Directory()
    {
        var myKey = EphemeralRoutingId.DeriveRoutingKey([1, 2, 3, 4]);
        var peerKey = EphemeralRoutingId.DeriveRoutingKey([5, 6, 7, 8]);
        var dir = new EridDirectory(myKey);
        const string peerUhid = "alice-uhid";
        dir.RememberPeer(peerUhid, peerKey);
        // Pin the clock so the ERID the peer answers to and the resolver's resolution fall in one epoch.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (dir, peerUhid, now);
    }
}
