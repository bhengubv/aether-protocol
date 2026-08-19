// SPDX-License-Identifier: MIT

using System.Diagnostics;
using AetherNet.Messaging;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Routing;
using AetherNet.Voice;
using AetherNet.Voice.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// A voice frame must never wait for route discovery.
///
/// <para>
/// <see cref="IRoutingService.FindRouteAsync"/> falls through to discovery when nothing is cached:
/// it broadcasts a route request and waits <see cref="AetherNet.Constants.ProtocolConstants.RouteTimeoutMs"/>
/// — five seconds — for a reply. Media used to call it per frame. On a direct radio link there is no
/// router to answer, so every frame paid the full timeout and then broadcast anyway: measured on two
/// phones at 5040ms per frame against a microphone producing fifty a second, which discards 249 of
/// every 250 frames and leaves the call silent. Nothing looked wrong — connected, encrypted, both
/// microphones open, not a line in any log.
/// </para>
///
/// <para>
/// Signalling may still discover; an offer is worth waiting for. A frame of speech is not.
/// </para>
/// </summary>
public class VoiceFramePacingTests
{
    private const string Me = "me-uhid-pacing";
    private const string Peer = "peer-uhid-pacing";

    /// <summary>A router that behaves like a real one with nobody to answer: discovery costs the timeout.</summary>
    private sealed class SilentRouting : IRoutingService
    {
        public int DiscoveryCalls;
        public int CachedLookups;
        public TimeSpan DiscoveryCost { get; init; } = TimeSpan.FromSeconds(5);

        public async Task<RouteEntry?> FindRouteAsync(string destinationUhid, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref DiscoveryCalls);
            await Task.Delay(DiscoveryCost, cancellationToken).ConfigureAwait(false);
            return null;
        }

        public RouteEntry? GetCachedRoute(string destinationUhid)
        {
            Interlocked.Increment(ref CachedLookups);
            return null;
        }

        public IReadOnlyList<RouteEntry> GetAllRoutes() => [];
        public Task HandleRouteRequestAsync(MeshPacket routeRequest, string? linkLayerSenderUhid = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HandleRouteReplyAsync(MeshPacket routeReply, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InvalidateRouteAsync(string destinationUhid, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task AddOrUpdateRouteAsync(RouteEntry route, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PruneAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CountingSender : IMeshSender
    {
        public int Sent;
        public string LocalUhid { get; } = Me;
        public string? LocalGeohash => null;
        public IReadOnlyList<PeerInfo> GetConnectedPeers() => [];

        public Task<bool> SendAsync(MeshPacket packet, string nextHopUhid, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Sent);
            return Task.FromResult(true);
        }

        public Task<int> BroadcastAsync(MeshPacket packet, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Sent);
            return Task.FromResult(1);
        }
    }

    private static async Task<(VoiceCallService Voice, Guid CallId, SilentRouting Routing, CountingSender Sender)> ConnectedCallAsync()
    {
        var routing = new SilentRouting();
        var sender = new CountingSender();
        var voice = new VoiceCallService(sender, routing, incentives: null, logger: NullLogger<VoiceCallService>.Instance);

        var session = await voice.PlaceAsync(Peer, ["opus"]);
        session.State = CallState.Connected;

        // Placing the call is signalling, and signalling is allowed to discover a route — an offer is
        // worth waiting for. Count from here, so what these measure is the media path alone.
        routing.DiscoveryCalls = 0;
        routing.CachedLookups = 0;
        sender.Sent = 0;
        return (voice, session.Id, routing, sender);
    }

    // ── the defect ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fifty frames — one second of speech — must not take anything like fifty route discoveries.
    /// </summary>
    [Fact]
    public async Task Sending_a_second_of_speech_never_waits_for_route_discovery()
    {
        var (voice, callId, routing, sender) = await ConnectedCallAsync();

        var clock = Stopwatch.StartNew();
        for (uint i = 0; i < 50; i++)
            await voice.SendFrameAsync(callId, new byte[80], i);
        clock.Stop();

        Assert.Equal(0, routing.DiscoveryCalls);
        Assert.Equal(50, sender.Sent);
        Assert.True(clock.ElapsedMilliseconds < 1000,
            $"a second of speech took {clock.ElapsedMilliseconds}ms to hand to the radio");
    }

    /// <summary>Each frame must cost a cache lookup, which is free, rather than a discovery.</summary>
    [Fact]
    public async Task Every_frame_asks_the_cache_and_never_the_network()
    {
        var (voice, callId, routing, _) = await ConnectedCallAsync();

        for (uint i = 0; i < 10; i++)
            await voice.SendFrameAsync(callId, new byte[80], i);

        Assert.Equal(10, routing.CachedLookups);
        Assert.Equal(0, routing.DiscoveryCalls);
    }

    /// <summary>
    /// With no route known, the frame still goes out — broadcast is the fallback, and it happens
    /// immediately rather than after a timeout.
    /// </summary>
    [Fact]
    public async Task A_frame_with_no_known_route_still_goes_out_at_once()
    {
        var (voice, callId, _, sender) = await ConnectedCallAsync();

        var clock = Stopwatch.StartNew();
        await voice.SendFrameAsync(callId, new byte[80], 0);
        clock.Stop();

        Assert.Equal(1, sender.Sent);
        Assert.True(clock.ElapsedMilliseconds < 500,
            $"one frame took {clock.ElapsedMilliseconds}ms with no route known");
    }

    /// <summary>
    /// The pace has to hold for a real call, not just a burst. Ten seconds of speech at fifty frames
    /// a second is five hundred frames, and none of them may block.
    /// </summary>
    [Theory]
    [InlineData(50)]
    [InlineData(250)]
    [InlineData(500)]
    public async Task A_whole_call_stays_off_the_discovery_path(int frames)
    {
        var (voice, callId, routing, sender) = await ConnectedCallAsync();

        for (uint i = 0; i < frames; i++)
            await voice.SendFrameAsync(callId, new byte[80], i);

        Assert.Equal(0, routing.DiscoveryCalls);
        Assert.Equal(frames, sender.Sent);
    }

    /// <summary>A frame for a call that is not connected is still ignored — this changed nothing there.</summary>
    [Fact]
    public async Task A_frame_for_a_call_that_is_not_connected_goes_nowhere()
    {
        var routing = new SilentRouting();
        var sender = new CountingSender();
        var voice = new VoiceCallService(sender, routing, incentives: null, logger: NullLogger<VoiceCallService>.Instance);

        var session = await voice.PlaceAsync(Peer, ["opus"]);   // still Outgoing, never answered
        sender.Sent = 0;                                       // ignore the offer itself

        await voice.SendFrameAsync(session.Id, new byte[80], 0);

        Assert.Equal(0, sender.Sent);
    }
}
