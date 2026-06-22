// SPDX-License-Identifier: MIT

using System.Text;
using SIPSorcery.Net;
using Xunit;

namespace AetherNet.Transport.WebRtc.Tests;

/// <summary>
/// Integration tests that stand up two real <see cref="WebRtcTransportService"/> instances wired
/// only through an in-process signalling bus — no central server, no STUN/TURN — and prove a direct
/// WebRTC <c>RTCDataChannel</c> negotiates over host candidates and carries bytes both ways.
/// </summary>
public class WebRtcTransportTests
{
    // Empty (not null) => host-candidate-only ICE: loopback negotiation with no network dependency.
    private static readonly RTCIceServer[] HostOnly = Array.Empty<RTCIceServer>();

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task TwoPeers_OpenDataChannel_AndExchangeBytes()
    {
        await using var bus = new InMemoryWebRtcSignalingBus();
        await using var alice = new WebRtcTransportService("alice-uhid", bus.CreateEndpoint("alice-uhid"), HostOnly);
        await using var bob = new WebRtcTransportService("bob-uhid", bus.CreateEndpoint("bob-uhid"), HostOnly);

        var received = new TaskCompletionSource<(string From, byte[] Data)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bob.DataReceived += (from, data) => received.TrySetResult((from, data));

        var payload = Encoding.UTF8.GetBytes("hello over a serverless webrtc datachannel");
        var sent = await alice.SendAsync("bob-uhid", payload);

        Assert.True(sent, "alice should negotiate a direct channel to bob and send");

        var got = await received.Task.WaitAsync(Timeout);
        Assert.Equal("alice-uhid", got.From);
        Assert.Equal(payload, got.Data);
        Assert.True(alice.IsConnected("bob-uhid"));
        Assert.True(bob.IsConnected("alice-uhid"));
    }

    [Fact]
    public async Task EstablishedLink_CarriesBytesBothDirections()
    {
        await using var bus = new InMemoryWebRtcSignalingBus();
        await using var alice = new WebRtcTransportService("alice", bus.CreateEndpoint("alice"), HostOnly);
        await using var bob = new WebRtcTransportService("bob", bus.CreateEndpoint("bob"), HostOnly);

        var atBob = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var atAlice = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.DataReceived += (_, d) => atBob.TrySetResult(d);
        alice.DataReceived += (_, d) => atAlice.TrySetResult(d);

        Assert.True(await alice.SendAsync("bob", Encoding.UTF8.GetBytes("ping")));
        Assert.Equal("ping", Encoding.UTF8.GetString(await atBob.Task.WaitAsync(Timeout)));

        // Reuse the now-open link in the reverse direction (no re-negotiation).
        Assert.True(await bob.SendAsync("alice", Encoding.UTF8.GetBytes("pong")));
        Assert.Equal("pong", Encoding.UTF8.GetString(await atAlice.Task.WaitAsync(Timeout)));
    }

    [Fact]
    public async Task Transport_AdvertisesItsRungInTheLadder()
    {
        await using var bus = new InMemoryWebRtcSignalingBus();
        await using var t = new WebRtcTransportService("x", bus.CreateEndpoint("x"), HostOnly);

        Assert.Equal("WebRTC P2P", t.Name);
        Assert.True(t.IsAvailable);
        Assert.InRange(t.PowerCostRelative, 1, 99); // dearer than the radio mesh, cheaper than the relay (100)
        Assert.Equal(0, t.MaxRangeMeters);          // internet — unbounded range
    }
}
