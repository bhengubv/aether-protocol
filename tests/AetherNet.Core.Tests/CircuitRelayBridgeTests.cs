// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AetherNet.Transport.CircuitRelay;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// Behavioural proof of native circuit-relay-v2: a three-node topology where A and B can
/// each reach relay R but <b>cannot</b> reach each other directly. A message from A must
/// therefore traverse the relay bridge to reach B — with the server off, no libp2p, no
/// central relay box: just three peers and the native protocol.
/// </summary>
public class CircuitRelayBridgeTests
{
    // ── In-process one-hop mesh (models real transport adjacency) ──────────────

    private sealed class InProcMesh
    {
        private readonly ConcurrentDictionary<string, InProcLink> _links = new();
        private readonly HashSet<string> _edges = new();

        public void Connect(string x, string y) { _edges.Add($"{x}|{y}"); _edges.Add($"{y}|{x}"); }
        public bool Adjacent(string x, string y) => _edges.Contains($"{x}|{y}");
        public InProcLink Link(string node) => _links.GetOrAdd(node, n => new InProcLink(this, n));

        public void Deliver(string from, string to, byte[] frame)
        {
            if (!Adjacent(from, to)) return;
            var link = Link(to);
            _ = Task.Run(() => link.Raise(from, frame)); // async hop, like a real transport
        }
    }

    private sealed class InProcLink(InProcMesh mesh, string node) : IRelayLink
    {
        public event Action<string, byte[]>? FrameReceived;
        public bool CanReach(string nodeUhid) => mesh.Adjacent(node, nodeUhid);

        public Task<bool> SendFrameAsync(string nodeUhid, byte[] frame, CancellationToken ct = default)
        {
            if (!mesh.Adjacent(node, nodeUhid)) return Task.FromResult(false);
            mesh.Deliver(node, nodeUhid, frame);
            return Task.FromResult(true);
        }

        public void Raise(string from, byte[] frame) => FrameReceived?.Invoke(from, frame);
    }

    private sealed class MutableClock
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch.AddYears(56); // 2026-ish, fixed
        public DateTimeOffset Now => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static async Task<bool> WaitFor(Func<bool> cond, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            await Task.Delay(15);
        }
        return cond();
    }

    // ── Fixture: A ── R ── B, with NO A–B edge ─────────────────────────────────

    private sealed record Topology(
        CircuitRelayTransportService A,
        CircuitRelayTransportService R,
        CircuitRelayTransportService B,
        ConcurrentQueue<(string sender, byte[] data)> BReceived,
        ConcurrentQueue<(string sender, byte[] data)> AReceived);

    private static Topology BuildLine(CircuitRelayOptions? relayOptions = null, Func<DateTimeOffset>? relayClock = null)
    {
        var mesh = new InProcMesh();
        mesh.Connect("A", "R");
        mesh.Connect("R", "B");
        // deliberately NOT mesh.Connect("A", "B")

        var a = new CircuitRelayTransportService("A", mesh.Link("A"));
        var r = new CircuitRelayTransportService("R", mesh.Link("R"), relayOptions, relayClock);
        var b = new CircuitRelayTransportService("B", mesh.Link("B"));

        var bReceived = new ConcurrentQueue<(string, byte[])>();
        var aReceived = new ConcurrentQueue<(string, byte[])>();
        b.DataReceived += (s, d) => bReceived.Enqueue((s, d));
        a.DataReceived += (s, d) => aReceived.Enqueue((s, d));

        return new Topology(a, r, b, bReceived, aReceived);
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Message_Traverses_Relay_From_A_To_B_With_No_Direct_Link()
    {
        var t = BuildLine();

        // A cannot reach B directly.
        Assert.False(t.A.IsConnected("B"));

        // B makes itself reachable via R; A learns the route (in prod: from the directory).
        Assert.True(await t.B.ReserveAsync("R"));
        t.A.SetRoute("B", "R");

        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        Assert.True(await t.A.SendAsync("B", payload));

        Assert.True(await WaitFor(() => !t.BReceived.IsEmpty), "B never received the relayed message");
        Assert.True(t.BReceived.TryPeek(out var got));
        Assert.Equal("A", got.sender);
        Assert.Equal(payload, got.data);
        Assert.Equal(1, t.R.ActiveBridgeCount); // R is genuinely bridging
    }

    [Fact]
    public async Task Bridge_Is_Bidirectional_B_Can_Reply_To_A()
    {
        var t = BuildLine();
        Assert.True(await t.B.ReserveAsync("R"));
        t.A.SetRoute("B", "R");

        Assert.True(await t.A.SendAsync("B", new byte[] { 1 }));
        Assert.True(await WaitFor(() => !t.BReceived.IsEmpty));

        // B replies over the same bridge — no route/reservation needed, it already knows A via R.
        var reply = new byte[] { 9, 8, 7 };
        Assert.True(await t.B.SendAsync("A", reply));

        Assert.True(await WaitFor(() => !t.AReceived.IsEmpty), "A never received B's reply");
        Assert.True(t.AReceived.TryPeek(out var got));
        Assert.Equal("B", got.sender);
        Assert.Equal(reply, got.data);
    }

    [Fact]
    public async Task Connect_Refused_When_Target_Has_No_Reservation()
    {
        var t = BuildLine();
        t.A.SetRoute("B", "R");           // route known...
        // ...but B never reserved on R.

        Assert.False(await t.A.SendAsync("B", new byte[] { 1 }));
        await Task.Delay(200);
        Assert.True(t.BReceived.IsEmpty);
        Assert.Equal(0, t.R.ActiveBridgeCount);
    }

    [Fact]
    public async Task Send_Fails_Fast_When_No_Relay_Route_Known()
    {
        var t = BuildLine();
        Assert.True(await t.B.ReserveAsync("R"));
        // No SetRoute for B → A has no way to know which relay to use.
        Assert.False(await t.A.SendAsync("B", new byte[] { 1 }));
        Assert.True(t.BReceived.IsEmpty);
    }

    [Fact]
    public async Task Relay_Enforces_Per_Bridge_Data_Budget()
    {
        var t = BuildLine(new CircuitRelayOptions { BridgeDataLimitBytes = 10 });
        Assert.True(await t.B.ReserveAsync("R"));
        t.A.SetRoute("B", "R");

        // 5 bytes — within the 10-byte budget → delivered.
        Assert.True(await t.A.SendAsync("B", new byte[] { 1, 2, 3, 4, 5 }));
        Assert.True(await WaitFor(() => t.BReceived.Count == 1));

        // 8 more bytes → cumulative 13 > 10 → relay drops the bridge, message not delivered.
        await t.A.SendAsync("B", new byte[] { 6, 7, 8, 9, 10, 11, 12, 13 });
        await Task.Delay(300);
        Assert.Single(t.BReceived);                  // still only the first
        Assert.Equal(0, t.R.ActiveBridgeCount);      // bridge torn down on budget breach
    }

    [Fact]
    public async Task Reservation_Expiry_Refuses_Later_Connect()
    {
        var clock = new MutableClock();
        var t = BuildLine(
            relayOptions: new CircuitRelayOptions { ReservationTtl = TimeSpan.FromMinutes(30) },
            relayClock: () => clock.Now);

        Assert.True(await t.B.ReserveAsync("R"));
        t.A.SetRoute("B", "R");

        // Fast-forward past the reservation TTL on the relay's clock.
        clock.Advance(TimeSpan.FromMinutes(31));

        Assert.False(await t.A.SendAsync("B", new byte[] { 1 }));
        await Task.Delay(200);
        Assert.True(t.BReceived.IsEmpty);
    }
}
