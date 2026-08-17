// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// When to give up on a radio link.
///
/// <para>
/// Getting this wrong is expensive in both directions. Hold on too long and messages queue onto a peer
/// that left the room. Let go too early and a working conversation is torn down mid-sentence — and
/// everything already handed to that link dies with it, including the receipts that tell the sender
/// their message arrived. The person then sees "failed" under a message the other phone is reading.
/// </para>
///
/// <para>
/// Watched on hardware 2026-08-13, both ways round. A P30 Lite tore a link down nine seconds after a
/// 443-byte message its peer had already received, losing the receipt in flight and showing the sender
/// a failure for a message being read on the other phone. Later the same two phones sat on a link where
/// every write completed on both sides and nothing whatsoever reached either app — a link only a
/// teardown could fix.
/// </para>
/// </summary>
public class LinkLivenessTests
{
    private static readonly DateTime Start = new(2026, 8, 13, 13, 23, 0, DateTimeKind.Utc);

    private static LinkLiveness Linked()
    {
        var link = new LinkLiveness();
        link.RecordInbound(Start);
        return link;
    }

    // ── What counts as proof the peer is there ────────────────────────────────

    [Fact]
    public void A_link_is_not_lost_while_the_peer_keeps_answering()
    {
        var link = Linked();

        link.RecordInbound(Start.AddSeconds(20));

        Assert.False(link.IsLost(Start.AddSeconds(21)));
    }

    [Fact]
    public void A_link_is_lost_when_the_peer_stops_answering()
    {
        var link = Linked();
        link.NotePingSent(Start.AddSeconds(24));

        Assert.True(link.IsLost(Start.AddSeconds(24).Add(LinkLiveness.PongWithin)));
    }

    /// <summary>
    /// The link that has quietly stopped carrying anything is the one this exists to catch: two phones
    /// were watched holding a connection where every write completed successfully on both sides while
    /// nothing at all reached either app. Sending into it forever is not evidence it works.
    /// </summary>
    [Fact]
    public void A_link_that_only_carries_our_own_traffic_is_lost()
    {
        var link = Linked();

        // Twenty minutes of us talking into it and nothing ever coming back.
        for (var minute = 1; minute <= 20; minute++)
        {
            var now = Start.AddMinutes(minute);
            if (link.ShouldPing(now)) link.NotePingSent(now);
        }

        Assert.True(link.IsLost(Start.AddMinutes(21)));
    }

    // ── Asking ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_quiet_link_gets_asked()
    {
        var link = Linked();

        Assert.True(link.ShouldPing(Start.Add(LinkLiveness.PingAfter)));
    }

    [Fact]
    public void A_busy_link_is_not_asked()
    {
        var link = Linked();

        link.RecordInbound(Start.AddSeconds(20));

        Assert.False(link.ShouldPing(Start.AddSeconds(21)));
    }

    [Fact]
    public void A_link_with_a_question_already_out_is_not_asked_again()
    {
        var link = Linked();
        link.NotePingSent(Start.AddSeconds(8));

        Assert.False(link.ShouldPing(Start.AddSeconds(16)));
    }

    [Fact]
    public void A_link_that_answered_is_asked_again_after_more_quiet()
    {
        var link = Linked();
        link.NotePingSent(Start.AddSeconds(8));
        link.RecordInbound(Start.AddSeconds(9));

        Assert.True(link.ShouldPing(Start.AddSeconds(9).Add(LinkLiveness.PingAfter)));
    }

    // ── The window is wide enough for real traffic ────────────────────────────

    /// <summary>
    /// A single BLE attribute write of a full-sized frame took just over a second on a P30 Lite, GATT
    /// operations serialise, and a message is several frames. The window has to outlast an ordinary
    /// payload with room to spare, or sending something large becomes the thing that kills the link
    /// carrying it — and the receipts in flight die with it.
    /// </summary>
    [Fact]
    public void The_window_outlasts_an_ordinary_message()
    {
        Assert.True(LinkLiveness.PongWithin >= TimeSpan.FromSeconds(12),
            $"a {LinkLiveness.PongWithin.TotalSeconds:0}s window is too close to a slow multi-frame send");
    }

    [Fact]
    public void A_link_is_asked_before_it_is_judged()
    {
        Assert.True(LinkLiveness.PingAfter < LinkLiveness.PongWithin,
            "the link would be declared dead before the question had time to be answered");
    }

    // ── Starting over ─────────────────────────────────────────────────────────

    [Fact]
    public void A_reset_link_carries_no_history_into_the_next_one()
    {
        var link = Linked();
        link.NotePingSent(Start.AddSeconds(24));

        link.Reset();

        Assert.False(link.PingOutstanding);
        Assert.False(link.IsLost(Start.AddSeconds(60)));
    }
}
