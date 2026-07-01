// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AetherNet.Protocol;
using AetherNet.Transport.CircuitRelay;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Integration proof that the circuit-relay-v2 engine works as a real mesh transport: the
/// <see cref="MeshRelayLink"/> wraps every relay frame in a <see cref="MeshPacket"/> of type
/// <see cref="PacketType.CircuitRelayControl"/> and routes it one hop through a mesh hub whose
/// adjacency is A─R─B with NO direct A–B edge. A message from A reaches B only via the relay,
/// and surfaces at B through the <c>ITransportService.DataReceived</c> contract — exactly how
/// <c>TransportManager</c> consumes it. The hub stands in for the real radios (BLE/Wi-Fi/…),
/// which in production are the <c>sendOneHop</c> delegate.
/// </summary>
public class CircuitRelayMeshIntegrationTests
{
    private sealed class MeshHub
    {
        private readonly Dictionary<string, MeshRelayLink> _links = new();
        private readonly HashSet<string> _edges = new();

        public void Connect(string x, string y) { _edges.Add($"{x}|{y}"); _edges.Add($"{y}|{x}"); }
        private bool Adjacent(string x, string y) => _edges.Contains($"{x}|{y}");
        public void Register(string node, MeshRelayLink link) => _links[node] = link;

        // The one-hop send: deliver to the destination's link iff directly adjacent (async hop).
        public Func<MeshPacket, CancellationToken, Task<bool>> SendFrom(string node) =>
            (pkt, _ct) =>
            {
                if (!Adjacent(node, pkt.DestinationUhid)) return Task.FromResult(false);
                if (_links.TryGetValue(pkt.DestinationUhid, out var link))
                    _ = Task.Run(() => link.HandleIncomingPacket(pkt));
                return Task.FromResult(true);
            };

        public Func<string, bool> CanReachFrom(string node) => other => Adjacent(node, other);
    }

    [Fact]
    public async Task Relay_Works_As_Mesh_Transport_Over_MeshPacket_Frames()
    {
        var hub = new MeshHub();
        hub.Connect("A", "R");
        hub.Connect("R", "B"); // deliberately NO A-B edge

        var (aT, aL) = MeshCircuitRelay.Create("A", hub.SendFrom("A"), hub.CanReachFrom("A"));
        var (rT, rL) = MeshCircuitRelay.Create("R", hub.SendFrom("R"), hub.CanReachFrom("R"));
        var (bT, bL) = MeshCircuitRelay.Create("B", hub.SendFrom("B"), hub.CanReachFrom("B"));
        hub.Register("A", aL);
        hub.Register("R", rL);
        hub.Register("B", bL);

        // B surfaces the relayed message through the ITransportService.DataReceived contract.
        var received = new TaskCompletionSource<(string sender, byte[] data)>(TaskCreationOptions.RunContinuationsAsynchronously);
        bT.DataReceived += (s, d) => received.TrySetResult((s, d));

        Assert.False(aT.IsConnected("B"));                 // no direct path
        Assert.True(await bT.ReserveAsync("R"));            // B reserves on the relay
        aT.SetRoute("B", "R");                              // A learns B is reachable via R

        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        Assert.True(await aT.SendAsync("B", payload));      // relayed A -> R -> B

        var done = await Task.WhenAny(received.Task, Task.Delay(3000));
        Assert.True(done == received.Task, "B never received the relayed message via the mesh link");
        var got = await received.Task;
        Assert.Equal("A", got.sender);
        Assert.Equal(payload, got.data);
        Assert.Equal(1, rT.ActiveBridgeCount);             // R is genuinely bridging over real packets

        _ = (aT, rT, bT); // keep transports alive for the test's duration
    }
}
