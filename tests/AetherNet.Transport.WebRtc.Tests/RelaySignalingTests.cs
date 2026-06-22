// SPDX-License-Identifier: MIT

using System.Text;
using SIPSorcery.Net;
using Xunit;

namespace AetherNet.Transport.WebRtc.Tests;

/// <summary>
/// Proves the production signalling path: the SDP/ICE handshake is framed by
/// <see cref="RelayWebRtcSignaling"/> and carried over an <see cref="LoopbackTransport"/> (standing in
/// for the relay), after which a direct WebRTC data channel carries the payload peer-to-peer.
/// </summary>
public class RelaySignalingTests
{
    private static readonly RTCIceServer[] HostOnly = Array.Empty<RTCIceServer>();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Handshake_RidesRelay_ThenDataGoesDirect()
    {
        // Two "relay" endpoints wired to each other — the only thing the peers share.
        var aliceRelay = new LoopbackTransport("alice");
        var bobRelay = new LoopbackTransport("bob");
        aliceRelay.Peer = bobRelay;
        bobRelay.Peer = aliceRelay;

        using var aliceSignalling = new RelayWebRtcSignaling(aliceRelay);
        using var bobSignalling = new RelayWebRtcSignaling(bobRelay);

        await using var alice = new WebRtcTransportService("alice", aliceSignalling, HostOnly);
        await using var bob = new WebRtcTransportService("bob", bobSignalling, HostOnly);

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        bob.DataReceived += (_, d) => received.TrySetResult(d);

        var payload = Encoding.UTF8.GetBytes("handshake rode the relay; the data went direct");
        Assert.True(await alice.SendAsync("bob", payload), "negotiation over the relay should succeed");

        var got = await received.Task.WaitAsync(Timeout);
        Assert.Equal(payload, got);
        Assert.True(alice.IsConnected("bob"));
        Assert.True(bob.IsConnected("alice"));
    }

    [Fact]
    public void NonSignallingBytes_AreIgnored()
    {
        // App traffic without the AWS1 prefix must not surface as a signal.
        var relay = new LoopbackTransport("self") { Peer = new LoopbackTransport("peer") };
        relay.Peer!.Peer = relay;
        using var signalling = new RelayWebRtcSignaling(relay);

        var raised = false;
        signalling.SignalReceived += _ => raised = true;

        // Drive plain bytes into `relay` by sending from its peer.
        Assert.True(relay.Peer.SendAsync("self", Encoding.UTF8.GetBytes("ordinary app data")).IsCompletedSuccessfully);
        Assert.False(raised, "non-prefixed app bytes must not be decoded as signalling");
    }
}
