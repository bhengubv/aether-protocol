// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using AetherNet.Core.Tests.Fakes;
using AetherNet.Dtn;
using AetherNet.Messaging;
using AetherNet.Messaging.Models;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Tests for <see cref="MeshInboundDispatcher"/> — the inbound last mile the library used to make every
/// host re-write: deserialize, run the relay, and dispatch each packet type to the layer that owns it.
/// </summary>
public class MeshInboundDispatcherTests
{
    private const string Me = "ME";
    private const string Alice = "ALICE";
    private const string Bob = "BOB";
    private const string Charlie = "CHARLIE"; // a stranger — recognised by nobody

    // ── recording fakes ──────────────────────────────────────────────────────

    private sealed class RecordingMessaging : IMessagingService
    {
        public ConcurrentBag<MeshPacket> Handled { get; } = new();
#pragma warning disable CS0067 // events are part of the interface but unused by these tests
        public event EventHandler<MeshMessage>? MessageReceived;
        public event EventHandler<DeliveryReceipt>? DeliveryConfirmed;
        public event EventHandler<string>? SessionRequired;
#pragma warning restore CS0067
        public Task<bool> SendAsync(MeshMessage m, byte[] p, CancellationToken ct = default) => Task.FromResult(true);
        public Task HandleAsync(MeshPacket packet, CancellationToken ct = default) { Handled.Add(packet); return Task.CompletedTask; }
        public Task<int> ProcessOutboxAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<MeshMessage>> GetInboxAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MeshMessage>>([]);
        public Task<IReadOnlyList<MeshMessage>> GetOutboxAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MeshMessage>>([]);
    }

    private sealed class RecordingRouting : IRoutingService
    {
        public ConcurrentBag<(MeshPacket Packet, string? LinkSender)> Requests { get; } = new();
        public ConcurrentBag<MeshPacket> Replies { get; } = new();
        public Task<RouteEntry?> FindRouteAsync(string d, CancellationToken ct = default) => Task.FromResult<RouteEntry?>(null);
        public RouteEntry? GetCachedRoute(string d) => null;
        public IReadOnlyList<RouteEntry> GetAllRoutes() => [];
        public Task HandleRouteRequestAsync(MeshPacket p, string? linkSender = null, CancellationToken ct = default) { Requests.Add((p, linkSender)); return Task.CompletedTask; }
        public Task HandleRouteReplyAsync(MeshPacket p, CancellationToken ct = default) { Replies.Add(p); return Task.CompletedTask; }
        public Task PruneAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingDtn : IDtnService
    {
        public ConcurrentBag<MeshPacket> Handled { get; } = new();
#pragma warning disable CS0067 // events are part of the interface but unused by these tests
        public event EventHandler<DtnDeliveryReceipt>? BundleDelivered;
        public event EventHandler<DtnBundleReceivedEventArgs>? BundleReceived;
#pragma warning restore CS0067
        public Task HandleAsync(MeshPacket packet, CancellationToken ct = default) { Handled.Add(packet); return Task.CompletedTask; }
        // The dispatcher only ever calls HandleAsync; the rest are here to satisfy the interface.
        public Task<DtnBundle> CreateBundleAsync(string r, byte[] p, BundlePriority pr = BundlePriority.Normal, string? g = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RunDeliveryScanAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> ExpireStaleAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<IReadOnlyList<DtnBundle>> GetActiveBundlesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DtnBundle>>([]);
    }

    private sealed class FakeResolver(IEnumerable<string> mine, Dictionary<string, string> known) : IWireAddressResolver
    {
        private readonly HashSet<string> _mine = new(mine, StringComparer.Ordinal);
        public bool IsLocal(string wireAddress) => _mine.Contains(wireAddress);
        public string? Recognise(string wireAddress) => known.TryGetValue(wireAddress, out var t) ? t : null;
    }

    private static MeshPacket Packet(PacketType type, string source = Alice, string dest = Bob, int ttl = 7) => new()
    {
        Type = type,
        SourceUhid = source,
        DestinationUhid = dest,
        Ttl = ttl,
        Payload = [1, 2, 3],
    };

    // ── core type dispatch ───────────────────────────────────────────────────

    [Theory]
    [InlineData(PacketType.Data)]
    [InlineData(PacketType.Ack)]
    public async Task Data_and_ack_go_to_the_messaging_service(PacketType type)
    {
        var messaging = new RecordingMessaging();
        var d = new MeshInboundDispatcher(new FakeMeshSender(Me), messaging);

        await d.DispatchAsync(Packet(type, dest: Me));

        var handled = Assert.Single(messaging.Handled);
        Assert.Equal(type, handled.Type);
    }

    [Fact]
    public async Task Route_request_goes_to_routing_with_the_link_sender()
    {
        var routing = new RecordingRouting();
        var d = new MeshInboundDispatcher(new FakeMeshSender(Me), routing: routing);

        await d.DispatchAsync(Packet(PacketType.RouteRequest, dest: ""), linkSenderUhid: "neighbour");

        var (_, linkSender) = Assert.Single(routing.Requests);
        Assert.Equal("neighbour", linkSender);
    }

    [Fact]
    public async Task Route_reply_goes_to_routing()
    {
        var routing = new RecordingRouting();
        var d = new MeshInboundDispatcher(new FakeMeshSender(Me), routing: routing);

        await d.DispatchAsync(Packet(PacketType.RouteReply, dest: Me));

        Assert.Single(routing.Replies);
    }

    [Theory]
    [InlineData(PacketType.DtnBundle)]
    [InlineData(PacketType.DtnCustodyAck)]
    [InlineData(PacketType.DtnDeliveryReceipt)]
    public async Task Dtn_packets_go_to_the_dtn_service(PacketType type)
    {
        var dtn = new RecordingDtn();
        var d = new MeshInboundDispatcher(new FakeMeshSender(Me), dtn: dtn);

        await d.DispatchAsync(Packet(type, dest: Me));

        Assert.Single(dtn.Handled);
    }

    // ── registration extension point ─────────────────────────────────────────

    [Fact]
    public async Task A_registered_handler_receives_a_type_the_library_does_not_know()
    {
        var got = new List<MeshPacket>();
        var d = new MeshInboundDispatcher(new FakeMeshSender(Me));
        d.Register(PacketType.PresenceBeacon, (p, _) => { got.Add(p); return Task.CompletedTask; });

        await d.DispatchAsync(Packet(PacketType.PresenceBeacon, dest: ""));

        Assert.Single(got);
    }

    [Fact]
    public async Task A_registered_handler_overrides_a_built_in_type()
    {
        var messaging = new RecordingMessaging();
        var got = new List<MeshPacket>();
        var d = new MeshInboundDispatcher(new FakeMeshSender(Me), messaging);
        d.Register(PacketType.Data, (p, _) => { got.Add(p); return Task.CompletedTask; });

        await d.DispatchAsync(Packet(PacketType.Data, dest: Me));

        Assert.Single(got);
        Assert.Empty(messaging.Handled); // messaging did NOT also see it
    }

    // ── the pump: bytes in ───────────────────────────────────────────────────

    [Fact]
    public async Task OnBytes_deserializes_and_dispatches_a_real_packet()
    {
        var messaging = new RecordingMessaging();
        var d = new MeshInboundDispatcher(new FakeMeshSender(Me), messaging);
        var bytes = PacketSerializer.Serialize(Packet(PacketType.Data, dest: Me));

        await d.OnBytesAsync("neighbour", bytes);

        Assert.Single(messaging.Handled);
    }

    [Fact]
    public async Task OnBytes_drops_a_malformed_frame_without_throwing()
    {
        var messaging = new RecordingMessaging();
        var d = new MeshInboundDispatcher(new FakeMeshSender(Me), messaging);

        await d.OnBytesAsync("neighbour", [0xDE, 0xAD]); // too short to be a MeshPacket

        Assert.Empty(messaging.Handled);
    }

    // ── relay plane ──────────────────────────────────────────────────────────

    private static MeshInboundDispatcher WithRelay(FakeMeshSender sender, RecordingMessaging messaging, MeshRelay relay) =>
        new(sender, messaging, relay: relay,
            resolver: new FakeResolver([Me], new() { [Alice] = Alice, [Bob] = Bob }));

    [Fact]
    public async Task A_packet_for_a_recognised_third_party_is_carried_not_delivered()
    {
        var sender = new FakeMeshSender(Me);
        var messaging = new RecordingMessaging();
        var d = WithRelay(sender, messaging, new MeshRelay());

        await d.DispatchAsync(Packet(PacketType.Data, source: Alice, dest: Bob, ttl: 5));

        var (carried, nextHop) = Assert.Single(sender.Unicasts);
        Assert.Equal(Bob, nextHop);
        Assert.Equal(4, carried.Ttl);              // one hop shorter
        Assert.Empty(messaging.Handled);           // carried, never delivered locally
    }

    [Fact]
    public async Task A_packet_for_me_is_delivered_not_carried_even_with_a_relay()
    {
        var sender = new FakeMeshSender(Me);
        var messaging = new RecordingMessaging();
        var d = WithRelay(sender, messaging, new MeshRelay());

        await d.DispatchAsync(Packet(PacketType.Data, source: Alice, dest: Me));

        Assert.Single(messaging.Handled);
        Assert.Empty(sender.Unicasts);
    }

    [Fact]
    public async Task A_packet_from_a_stranger_is_neither_carried_nor_delivered()
    {
        var sender = new FakeMeshSender(Me);
        var messaging = new RecordingMessaging();
        var d = WithRelay(sender, messaging, new MeshRelay());

        await d.DispatchAsync(Packet(PacketType.Data, source: Charlie, dest: Bob));

        Assert.Empty(sender.Unicasts);
        Assert.Empty(messaging.Handled);
    }

    [Fact]
    public async Task A_route_request_is_never_taken_by_the_relay()
    {
        // RouteRequest has its own forwarder (routing). Even with a relay wired, it must reach routing.
        var sender = new FakeMeshSender(Me);
        var routing = new RecordingRouting();
        var d = new MeshInboundDispatcher(sender, routing: routing, relay: new MeshRelay(),
            resolver: new FakeResolver([Me], new() { [Alice] = Alice, [Bob] = Bob }));

        await d.DispatchAsync(Packet(PacketType.RouteRequest, source: Alice, dest: Bob));

        Assert.Single(routing.Requests);
        Assert.Empty(sender.Unicasts);
    }

    [Fact]
    public async Task Without_a_resolver_nothing_is_carried_even_with_a_relay()
    {
        // No resolver ⇒ recognises nobody ⇒ never an open relay. Non-local DATA still reaches messaging,
        // which will drop it — but the relay must not carry it.
        var sender = new FakeMeshSender(Me);
        var messaging = new RecordingMessaging();
        var d = new MeshInboundDispatcher(sender, messaging, relay: new MeshRelay()); // resolver omitted

        await d.DispatchAsync(Packet(PacketType.Data, source: Alice, dest: Bob));

        Assert.Empty(sender.Unicasts);
        Assert.Single(messaging.Handled);
    }

    // ── ERID normalisation ───────────────────────────────────────────────────
    //
    // When a resolver is wired the node speaks rotating addresses: a packet delivered locally must reach
    // the messaging/handler layer keyed on stable identities, not the ephemeral address the radio saw.

    [Fact]
    public async Task A_source_that_is_a_recognised_rotating_address_is_resolved_before_local_delivery()
    {
        var messaging = new RecordingMessaging();
        var d = new MeshInboundDispatcher(new FakeMeshSender(Me), messaging,
            resolver: new FakeResolver([Me], new() { ["ERID_ALICE"] = Alice }));

        await d.DispatchAsync(Packet(PacketType.Data, source: "ERID_ALICE", dest: Me));

        var handled = Assert.Single(messaging.Handled);
        Assert.Equal(Alice, handled.SourceUhid); // resolved from the rotating address to the stable tag
    }

    [Fact]
    public async Task Our_own_rotating_destination_is_rewritten_to_our_stable_uhid()
    {
        // The messaging layer's "is this for me?" check compares against the stable LocalUhid, so a packet
        // addressed to one of our rotating ERIDs has to be rewritten or it would be dropped as not-ours.
        var messaging = new RecordingMessaging();
        var d = new MeshInboundDispatcher(new FakeMeshSender(Me), messaging,
            resolver: new FakeResolver(["ERID_ME"], new() { ["ERID_ALICE"] = Alice }));

        await d.DispatchAsync(Packet(PacketType.Data, source: "ERID_ALICE", dest: "ERID_ME"));

        var handled = Assert.Single(messaging.Handled);
        Assert.Equal(Alice, handled.SourceUhid);
        Assert.Equal(Me, handled.DestinationUhid);
    }

    [Fact]
    public async Task An_unrecognised_source_passes_through_untouched()
    {
        // A peer that has not shared a routing key yet still arrives on its stable tag — nothing to resolve.
        var messaging = new RecordingMessaging();
        var d = new MeshInboundDispatcher(new FakeMeshSender(Me), messaging,
            resolver: new FakeResolver([Me], new() { ["ERID_ALICE"] = Alice }));

        await d.DispatchAsync(Packet(PacketType.Data, source: Bob, dest: Me));

        var handled = Assert.Single(messaging.Handled);
        Assert.Equal(Bob, handled.SourceUhid);
    }

    [Fact]
    public async Task A_registered_handler_also_sees_the_resolved_identity()
    {
        var got = new List<MeshPacket>();
        var d = new MeshInboundDispatcher(new FakeMeshSender(Me),
            resolver: new FakeResolver([Me], new() { ["ERID_ALICE"] = Alice }));
        d.Register(PacketType.EridAnnounce, (p, _) => { got.Add(p); return Task.CompletedTask; });

        await d.DispatchAsync(Packet(PacketType.EridAnnounce, source: "ERID_ALICE", dest: Me));

        var p = Assert.Single(got);
        Assert.Equal(Alice, p.SourceUhid);
    }

    [Fact]
    public async Task A_carried_packet_keeps_its_rotating_addresses_and_routes_to_the_resolved_next_hop()
    {
        // The relay must not normalise what it forwards: the next hop needs the rotating destination to
        // recognise the recipient in turn. Only the next-hop routing target is the resolved stable tag.
        var sender = new FakeMeshSender(Me);
        var messaging = new RecordingMessaging();
        var d = new MeshInboundDispatcher(sender, messaging, relay: new MeshRelay(),
            resolver: new FakeResolver([Me], new() { ["ERID_ALICE"] = Alice, ["ERID_BOB"] = Bob }));

        await d.DispatchAsync(Packet(PacketType.Data, source: "ERID_ALICE", dest: "ERID_BOB", ttl: 5));

        var (carried, nextHop) = Assert.Single(sender.Unicasts);
        Assert.Equal(Bob, nextHop);                        // next hop resolved to the stable tag
        Assert.Equal("ERID_BOB", carried.DestinationUhid); // wire address stays rotating
        Assert.Equal("ERID_ALICE", carried.SourceUhid);
        Assert.Equal(4, carried.Ttl);
        Assert.Empty(messaging.Handled);
    }
}
