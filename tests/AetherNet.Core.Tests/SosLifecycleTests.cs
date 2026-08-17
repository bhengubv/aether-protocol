// SPDX-License-Identifier: MIT

using System.Text.Json;
using AetherNet.Core.Tests.Fakes;
using AetherNet.Models;
using AetherNet.Protocol;
using AetherNet.Sos;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AetherNet.Core.Tests;

/// <summary>
/// SOS reach modes + the check-in → escalate → locator-beacon lifecycle. The decision rules are covered
/// deterministically by <see cref="SosEscalationPolicy"/>; the timer-driven behaviour uses short per-SOS
/// intervals with a poll-until-condition wait, so it stays robust on a slow / loaded box.
/// </summary>
public sealed class SosLifecycleTests
{
    private const string Local = "aether:local:01";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private static SosBroadcastService Build(FakeMeshSender sender)
        => new(sender, backend: null, incentives: null, logger: NullLogger<SosBroadcastService>.Instance);

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("SOS lifecycle condition was not met within the timeout.");
            await Task.Delay(15);
        }
    }

    // ── Pure policy (no clock) ─────────────────────────────────────────────────

    [Fact]
    public void Policy_ContactsOnly_PastWindow_Escalates()
        => Assert.True(SosEscalationPolicy.ShouldEscalate(SosReach.Contacts, alreadyEscalated: false,
            sinceOrigin: TimeSpan.FromSeconds(3), escalateAfter: TimeSpan.FromSeconds(2)));

    [Fact]
    public void Policy_ContactsOnly_BeforeWindow_DoesNotEscalate()
        => Assert.False(SosEscalationPolicy.ShouldEscalate(SosReach.Contacts, false,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));

    [Fact]
    public void Policy_AlreadyEscalated_DoesNotEscalateAgain()
        => Assert.False(SosEscalationPolicy.ShouldEscalate(SosReach.Contacts, alreadyEscalated: true,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)));

    [Theory]
    [InlineData(SosReach.Nearby)]
    [InlineData(SosReach.Both)]
    public void Policy_BroadcastingReach_NeverEscalates(SosReach reach)
        => Assert.False(SosEscalationPolicy.ShouldEscalate(reach, false,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)));

    [Theory]
    [InlineData(SosReach.Nearby, false, true)]
    [InlineData(SosReach.Both, false, true)]
    [InlineData(SosReach.Contacts, false, false)]
    [InlineData(SosReach.Contacts, true, true)]   // an escalated contacts-only alert is now broadcasting
    public void Policy_IsBroadcasting(SosReach reach, bool escalated, bool expected)
        => Assert.Equal(expected, SosEscalationPolicy.IsBroadcasting(reach, escalated));

    // ── Reach modes (synchronous initial emission) ─────────────────────────────

    [Fact]
    public async Task Nearby_FloodsOnly()
    {
        var sender = new FakeMeshSender(Local);
        using var svc = Build(sender);

        await svc.BroadcastSosAsync("sos", null, 0, 0, SosReach.Nearby,
            beaconInterval: TimeSpan.FromMinutes(10)); // long beacon so nothing re-emits during the test

        Assert.Single(sender.Broadcasts);
        Assert.Empty(sender.Unicasts);
    }

    [Fact]
    public async Task ContactsOnly_DirectedOnly_NoFlood()
    {
        var sender = new FakeMeshSender(Local);
        using var svc = Build(sender);

        await svc.BroadcastSosAsync("sos", null, 0, 0, SosReach.Contacts,
            contacts: new[] { "aether:contact:aa", "aether:contact:bb" },
            escalateAfter: TimeSpan.FromMinutes(10)); // long window so it doesn't escalate mid-test

        Assert.Empty(sender.Broadcasts);
        Assert.Equal(2, sender.Unicasts.Count);
    }

    [Fact]
    public async Task Both_FloodAndDirected()
    {
        var sender = new FakeMeshSender(Local);
        using var svc = Build(sender);

        await svc.BroadcastSosAsync("sos", null, 0, 0, SosReach.Both,
            contacts: new[] { "aether:contact:aa" },
            beaconInterval: TimeSpan.FromMinutes(10));

        Assert.Single(sender.Broadcasts);
        Assert.Single(sender.Unicasts);
    }

    // ── Check-in escalation (dead-man's switch) ────────────────────────────────

    [Fact]
    public async Task ContactsOnly_NotMarkedSafe_EscalatesToBroadcast()
    {
        var sender = new FakeMeshSender(Local);
        using var svc = Build(sender);

        await svc.BroadcastSosAsync("sos", null, 0, 0, SosReach.Contacts,
            contacts: new[] { "aether:contact:aa" },
            escalateAfter: TimeSpan.FromMilliseconds(120),
            beaconInterval: TimeSpan.FromMinutes(10)); // isolate escalation from the beacon

        var alert = svc.GetActiveAlerts()[0];
        Assert.Equal(SosReach.Contacts, alert.Reach); // starts contacts-only
        Assert.Empty(sender.Broadcasts);              // no flood yet

        await WaitUntil(() => alert.Escalated);

        Assert.True(alert.Escalated);
        Assert.Equal(SosReach.Both, alert.Reach);     // widened
        Assert.NotEmpty(sender.Broadcasts);           // a flood went out
    }

    [Fact]
    public async Task ContactsOnly_MarkedSafeBeforeWindow_DoesNotEscalate()
    {
        var sender = new FakeMeshSender(Local);
        using var svc = Build(sender);

        await svc.BroadcastSosAsync("sos", null, 0, 0, SosReach.Contacts,
            contacts: new[] { "aether:contact:aa" },
            escalateAfter: TimeSpan.FromMilliseconds(150));
        var alert = svc.GetActiveAlerts()[0];

        await svc.ResolveAsync(alert.Id); // source marks safe, immediately

        await Task.Delay(400);            // well past the check-in window
        Assert.False(alert.Escalated);
        Assert.Empty(sender.Broadcasts);  // never widened to a flood
    }

    // ── Locator beacon ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Broadcasting_ReEmitsLocatorBeacon()
    {
        var sender = new FakeMeshSender(Local);
        using var svc = Build(sender);

        await svc.BroadcastSosAsync("sos", null, 0, 0, SosReach.Nearby,
            beaconInterval: TimeSpan.FromMilliseconds(60));

        await WaitUntil(() => sender.Broadcasts.Count >= 3); // initial flood + repeated beacons
        Assert.True(sender.Broadcasts.Count >= 3);
    }

    [Fact]
    public async Task MarkSafe_StopsTheBeacon()
    {
        var sender = new FakeMeshSender(Local);
        using var svc = Build(sender);

        await svc.BroadcastSosAsync("sos", null, 0, 0, SosReach.Nearby,
            beaconInterval: TimeSpan.FromMilliseconds(60));
        var id = svc.GetActiveAlerts()[0].Id;

        await WaitUntil(() => sender.Broadcasts.Count >= 2);
        var beforeResolve = sender.Broadcasts.Count;

        await svc.ResolveAsync(id); // source marks safe → beacon must stop

        await Task.Delay(300);      // several beacon intervals; if it kept going this would be ~+5
        Assert.True(sender.Broadcasts.Count <= beforeResolve + 1,
            $"beacon kept emitting after mark-safe: {beforeResolve} -> {sender.Broadcasts.Count}");
    }

    // ── An acknowledgement is not rescue ───────────────────────────────────────

    [Fact]
    public async Task Acknowledgement_DoesNotStopEscalation()
    {
        var sender = new FakeMeshSender(Local);
        using var svc = Build(sender);

        await svc.BroadcastSosAsync("sos", null, 0, 0, SosReach.Contacts,
            contacts: new[] { "aether:contact:aa" },
            escalateAfter: TimeSpan.FromMilliseconds(120),
            beaconInterval: TimeSpan.FromMinutes(10));
        var alert = svc.GetActiveAlerts()[0];

        // A contact says "on my way" — that must NOT hold the escalation back.
        await svc.HandleAckAsync(MakeAck(alert.Id, "aether:responder:cc"));

        await WaitUntil(() => alert.Escalated);
        Assert.True(alert.Escalated);                                 // widened despite the ack
        Assert.Contains("aether:responder:cc", alert.AcknowledgedBy); // ack still recorded
    }

    private static MeshPacket MakeAck(Guid broadcastId, string responderUhid) => new()
    {
        Type = PacketType.SosAck,
        SourceUhid = responderUhid,
        DestinationUhid = Local,
        Payload = JsonSerializer.SerializeToUtf8Bytes(
            new SosAckPayload { BroadcastId = broadcastId, ReceivedAtMs = 1_700_000_000_000 }, JsonOpts),
    };
}
