// SPDX-License-Identifier: MIT

using AetherNet.Constants;
using AetherNet.Core.Tests.Fakes;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Reputation;
using Xunit;

namespace AetherNet.Core.Tests;

public class RoutingServiceTests
{
    private const string Local = "local-uhid";

    private static (RoutingService svc, FakeMeshSender sender, InMemoryRouteStore store) NewService(
        string localUhid = Local,
        IRouteReplyVerifier? verifier = null)
    {
        var sender = new FakeMeshSender(localUhid);
        var store = new InMemoryRouteStore();
        var svc = new RoutingService(sender, store, verifier);
        return (svc, sender, store);
    }

    private static MeshPacket NewRreq(string source, string destination, int ttl = ProtocolConstants.DefaultTtl)
    {
        return new MeshPacket
        {
            Id = Guid.NewGuid(),
            Type = PacketType.RouteRequest,
            SourceUhid = source,
            DestinationUhid = destination,
            Ttl = ttl,
        };
    }

    private static MeshPacket NewRrep(string source, string destination, int ttl = ProtocolConstants.DefaultTtl)
    {
        return new MeshPacket
        {
            Id = Guid.NewGuid(),
            Type = PacketType.RouteReply,
            SourceUhid = source,
            DestinationUhid = destination,
            Ttl = ttl,
        };
    }

    // ─── HandleRouteRequest ──────────────────────────────────────

    [Fact]
    public async Task HandleRouteRequest_FloodCap_ScoresTheNeighbour_NotTheSpoofedSource()
    {
        var sender = new FakeMeshSender(Local);
        var reputation = new InMemoryNodeReputationService();
        var svc = new RoutingService(sender, reputation: reputation);

        // A malicious neighbour "flooder" relays 11 distinct-Id RREQs, each FORGING the source to
        // a victim's UHID to try to frame them. Distinct Ids slip past the duplicate set, so only
        // the relay rate cap (default burst of 10) stops the flood.
        for (int i = 0; i < 11; i++)
            await svc.HandleRouteRequestAsync(NewRreq("victim", "bob"), linkLayerSenderUhid: "flooder");

        // The neighbour we actually received the bytes from is scored down (toward corroborated
        // excommunication); the spoofed victim is untouched — the cap can't be a framing weapon.
        Assert.Equal(0.95, await reputation.GetReputationScoreAsync("flooder"), precision: 6); // 1.0 - 0.05
        Assert.Equal(1.0,  await reputation.GetReputationScoreAsync("victim"),  precision: 6); // never dinged
    }

    [Fact]
    public async Task HandleRouteRequest_DropsDuplicateById()
    {
        var (svc, sender, _) = NewService();
        var rreq = NewRreq("alice", "bob");

        await svc.HandleRouteRequestAsync(rreq);
        sender.Clear();
        await svc.HandleRouteRequestAsync(rreq);

        Assert.Empty(sender.Broadcasts);
        Assert.Empty(sender.Unicasts);
    }

    [Fact]
    public async Task HandleRouteRequest_IgnoresSelfOriginated()
    {
        var (svc, sender, store) = NewService();
        var rreq = NewRreq(Local, "bob");

        await svc.HandleRouteRequestAsync(rreq);

        Assert.Empty(sender.Broadcasts);
        Assert.Empty(sender.Unicasts);
        var stored = await store.GetAllAsync();
        Assert.Empty(stored);
    }

    [Fact]
    public async Task HandleRouteRequest_InstallsReverseRouteToSource()
    {
        var (svc, _, store) = NewService();
        var rreq = NewRreq("alice", "bob");

        await svc.HandleRouteRequestAsync(rreq);

        var route = await store.GetAsync("alice");
        Assert.NotNull(route);
        Assert.Equal("alice", route!.NextHopUhid);
        Assert.True(route.HopCount >= 1);
        Assert.False(route.IsExpired);
    }

    [Fact]
    public async Task HandleRouteRequest_AsDestination_SendsRrepBack()
    {
        var (svc, sender, _) = NewService();
        var rreq = NewRreq("alice", Local);

        await svc.HandleRouteRequestAsync(rreq);

        // Should send unicast RREP via the reverse route (next-hop = alice).
        Assert.Single(sender.Unicasts);
        var (rrep, nextHop) = sender.Unicasts.First();
        Assert.Equal(PacketType.RouteReply, rrep.Type);
        Assert.Equal(Local, rrep.SourceUhid);
        Assert.Equal("alice", rrep.DestinationUhid);
        Assert.Equal("alice", nextHop);
    }

    [Fact]
    public async Task HandleRouteRequest_WithCachedRouteToDestination_RepliesOnBehalf()
    {
        var (svc, sender, store) = NewService();

        // Pre-populate a route to "carol" via "carol".
        await store.SaveAsync(new RouteEntry
        {
            DestinationUhid = "carol",
            NextHopUhid = "carol",
            HopCount = 1,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        });
        await svc.FindRouteAsync("carol"); // populates cache

        sender.Clear();

        var rreq = NewRreq("alice", "carol");
        await svc.HandleRouteRequestAsync(rreq);

        var rrep = sender.Unicasts.FirstOrDefault().Packet
                   ?? sender.Broadcasts.FirstOrDefault();
        Assert.NotNull(rrep);
        Assert.Equal(PacketType.RouteReply, rrep!.Type);
        Assert.Equal("carol", rrep.SourceUhid); // replying on carol's behalf
    }

    [Fact]
    public async Task HandleRouteRequest_ForwardsWhenTtlAllows()
    {
        var (svc, sender, _) = NewService();
        var rreq = NewRreq("alice", "carol", ttl: 5);

        await svc.HandleRouteRequestAsync(rreq);

        Assert.Single(sender.Broadcasts);
        var fwd = sender.Broadcasts.First();
        Assert.Equal(PacketType.RouteRequest, fwd.Type);
        Assert.Equal(4, fwd.Ttl); // decremented
    }

    [Fact]
    public async Task HandleRouteRequest_DropsWhenTtlExhausted()
    {
        var (svc, sender, _) = NewService();
        var rreq = NewRreq("alice", "carol", ttl: 1);

        await svc.HandleRouteRequestAsync(rreq);

        Assert.Empty(sender.Broadcasts); // not re-broadcast
        Assert.Empty(sender.Unicasts);
    }

    // ─── HandleRouteReply ──────────────────────────────────────

    [Fact]
    public async Task HandleRouteReply_InstallsForwardRoute()
    {
        var (svc, _, store) = NewService();
        var rrep = NewRrep("carol", Local);

        await svc.HandleRouteReplyAsync(rrep);

        var route = await store.GetAsync("carol");
        Assert.NotNull(route);
        Assert.Equal("carol", route!.NextHopUhid);
    }

    [Fact]
    public async Task HandleRouteReply_RejectsWhenVerifierFails()
    {
        var verifier = new RejectingVerifier();
        var (svc, _, store) = NewService(verifier: verifier);
        var rrep = NewRrep("carol", Local);

        await svc.HandleRouteReplyAsync(rrep);

        var route = await store.GetAsync("carol");
        Assert.Null(route);
    }

    [Fact]
    public async Task HandleRouteReply_ForwardsTowardOriginalRequester()
    {
        var (svc, sender, store) = NewService();

        // Reverse route to alice exists (via direct neighbour bob).
        await store.SaveAsync(new RouteEntry
        {
            DestinationUhid = "alice",
            NextHopUhid = "bob",
            HopCount = 2,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        });
        await svc.FindRouteAsync("alice"); // populate cache
        sender.Clear();

        var rrep = NewRrep("carol", "alice", ttl: 4);
        await svc.HandleRouteReplyAsync(rrep);

        var unicast = sender.Unicasts.FirstOrDefault(u => u.Packet.Type == PacketType.RouteReply);
        Assert.NotNull(unicast.Packet);
        Assert.Equal("bob", unicast.NextHopUhid);
        Assert.Equal(3, unicast.Packet.Ttl); // decremented
    }

    // ─── FindRoute ──────────────────────────────────────────────

    [Fact]
    public async Task FindRoute_ReturnsCachedRouteWithoutBroadcasting()
    {
        var (svc, sender, store) = NewService();
        await store.SaveAsync(new RouteEntry
        {
            DestinationUhid = "bob",
            NextHopUhid = "bob",
            HopCount = 1,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        });

        var route = await svc.FindRouteAsync("bob");

        Assert.NotNull(route);
        Assert.Equal("bob", route!.NextHopUhid);
        Assert.Empty(sender.Broadcasts);
    }

    [Fact]
    public async Task FindRoute_WithNoPeers_ReturnsNullImmediately()
    {
        var (svc, _, _) = NewService();

        var route = await svc.FindRouteAsync("bob");

        Assert.Null(route);
    }

    [Fact]
    public async Task FindRoute_TimesOutWhenNoRrepArrives()
    {
        // Override timeout to a much smaller value so the test runs fast — we give the
        // service one peer (so it broadcasts) but never feed back a matching RREP.
        var sender = new FakeMeshSender(Local);
        sender.AddPeer(new PeerInfo { Uhid = "bob" });
        var svc = new RoutingService(sender);

        // We still respect the protocol-level timeout (5s) since this is the only
        // path that proves the timer works. Test takes ~5s which is acceptable for
        // a CI signal.
        var route = await svc.FindRouteAsync("carol");

        Assert.Null(route);
        Assert.Single(sender.Broadcasts);
        Assert.Equal(PacketType.RouteRequest, sender.Broadcasts.First().Type);
    }

    [Fact]
    public async Task GetCachedRoute_ReturnsNullWhenExpired()
    {
        var (svc, _, store) = NewService();
        await store.SaveAsync(new RouteEntry
        {
            DestinationUhid = "bob",
            NextHopUhid = "bob",
            HopCount = 1,
            ExpiresAt = DateTime.UtcNow.AddSeconds(-1),
        });
        // Force cache load
        await svc.FindRouteAsync("does-not-exist");

        // Even after FindRoute populated the cache from the store, expired entries
        // should not be returned by GetCachedRoute.
        Assert.Null(svc.GetCachedRoute("bob"));
    }

    [Fact]
    public async Task PruneAsync_RemovesExpiredRoutes()
    {
        var (svc, _, store) = NewService();
        await store.SaveAsync(new RouteEntry
        {
            DestinationUhid = "stale",
            NextHopUhid = "stale",
            ExpiresAt = DateTime.UtcNow.AddSeconds(-10),
        });
        await store.SaveAsync(new RouteEntry
        {
            DestinationUhid = "fresh",
            NextHopUhid = "fresh",
            ExpiresAt = DateTime.UtcNow.AddMinutes(5),
        });
        await svc.FindRouteAsync("fresh"); // primes the cache

        await svc.PruneAsync();

        Assert.Null(await store.GetAsync("stale"));
        Assert.NotNull(await store.GetAsync("fresh"));
    }

    private sealed class RejectingVerifier : IRouteReplyVerifier
    {
        public Task<bool> VerifyAsync(MeshPacket routeReply, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
    }
}
