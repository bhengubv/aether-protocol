// SPDX-License-Identifier: MIT

using AetherNetNodeService.Host;
using Xunit;

namespace AetherNetNodeService.Host.Tests;

/// <summary>
/// Sizing media to a link that is measured rather than believed.
///
/// <para>
/// Every bandwidth figure this app trusted turned out to be arithmetic: BLE published 2 Mbps and
/// delivered 11 kbps one way, a voice note was described from the bitrate the encoder was asked for
/// and measured ten times that, and Wi-Fi Direct still reports a flat 250 Mbps nothing has checked.
/// These cover the replacement — what actually crossed, and how hard the link worked to carry it.
/// </para>
/// </summary>
public class LinkSizingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static LinkQuality Sending(int frames, int bytes, double ms, bool sent = true)
    {
        var q = new LinkQuality();
        for (var i = 0; i < frames; i++)
            q.Record(bytes, TimeSpan.FromMilliseconds(ms), sent, T0.AddMilliseconds(i * 20));
        return q;
    }

    // ── saying nothing until there is something to say ─────────────────────

    /// <summary>
    /// A fresh link has carried nothing, so it knows nothing. Reporting a number here is how a guess
    /// becomes a fact — the exact move that produced every wrong figure this replaces.
    /// </summary>
    [Fact]
    public void A_link_that_has_carried_nothing_claims_nothing()
    {
        var quiet = new LinkQuality();

        Assert.False(quiet.HasEnough(T0));
        Assert.Equal(0, quiet.ThroughputBps(T0));
        Assert.Equal(0, quiet.Strain(T0));
    }

    [Fact]
    public void One_or_two_sends_are_a_coincidence_not_a_measurement()
    {
        var barely = Sending(frames: 3, bytes: 1000, ms: 5);

        Assert.False(barely.HasEnough(T0.AddMilliseconds(60)));
        Assert.Equal(0, barely.ThroughputBps(T0.AddMilliseconds(60)));
    }

    // ── throughput is a floor, never a capacity ────────────────────────────

    /// <summary>
    /// Twenty frames of a thousand bytes in four hundred milliseconds is real traffic and produces a
    /// real number. It says what crossed — not what could have.
    /// </summary>
    [Fact]
    public void What_crossed_is_reported_once_there_is_enough_of_it()
    {
        var busy = Sending(frames: 20, bytes: 1000, ms: 5);

        var bps = busy.ThroughputBps(T0.AddMilliseconds(400));

        Assert.True(bps > 0, "traffic crossed and nothing was reported");
        Assert.InRange(bps, 300_000, 500_000);   // 20KB over ~0.4s ≈ 400kbps
    }

    // ── strain: the honest signal ──────────────────────────────────────────

    /// <summary>A link taking a few milliseconds per send is not working hard.</summary>
    [Fact]
    public void A_comfortable_link_reports_no_strain()
    {
        var easy = Sending(frames: 20, bytes: 1000, ms: 4);

        Assert.Equal(0, easy.Strain(T0.AddMilliseconds(400)), 1);
    }

    /// <summary>
    /// Sends taking far longer than a frame interval mean the next frame is queueing behind this one.
    /// That is congestion, and it shows up before anything is lost.
    /// </summary>
    [Fact]
    public void Slow_sends_show_as_strain_before_anything_is_lost()
    {
        var struggling = Sending(frames: 20, bytes: 1000, ms: 120);

        Assert.True(struggling.Strain(T0.AddMilliseconds(400)) > 0.5,
            "sends taking 120ms each should read as a link in trouble");
    }

    /// <summary>A refusal is the loudest thing a link can say. Everything refused is not "slow".</summary>
    [Fact]
    public void Refusals_read_as_a_link_that_is_gone()
    {
        var refusing = Sending(frames: 20, bytes: 1000, ms: 1, sent: false);

        Assert.True(refusing.Strain(T0.AddMilliseconds(400)) > 0.9);
        Assert.Equal(0, refusing.ThroughputBps(T0.AddMilliseconds(400)));   // nothing actually crossed
    }

    /// <summary>The window moves. A link that was bad a minute ago is not bad now.</summary>
    [Fact]
    public void Trouble_ages_out_of_the_window()
    {
        var was = Sending(frames: 20, bytes: 1000, ms: 200);

        Assert.Equal(0, was.Strain(T0.AddMinutes(1)));
    }

}
