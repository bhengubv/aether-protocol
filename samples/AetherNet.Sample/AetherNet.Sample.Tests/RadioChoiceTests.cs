// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Which radio carries, decided without asking anybody.
///
/// <para>
/// The person picked a contact, not a transport. "Connect over: Wi-Fi Direct / Wi-Fi Aware / Internet
/// / NFC / LoRa", with a note about mid-range chipsets, is handing them the plumbing — and it used to
/// win outright: choosing BLE while Wi-Fi Direct was up changed the label on the screen and moved the
/// traffic onto eleven kilobits.
/// </para>
/// </summary>
public class RadioChoiceTests
{
    private static RadioSpeed Wifi(bool linked = true, long measured = 0) =>
        new("Wi-Fi", linked, measured, 100_000_000);

    private static RadioSpeed Direct(bool linked = true, long measured = 0) =>
        new("Wi-Fi Direct", linked, measured, 250_000_000);

    private static RadioSpeed Ble(bool linked = true, long measured = 0) =>
        new("BLE", linked, measured, 11_000);

    private static RadioSpeed Lora(bool linked = true, long measured = 0) =>
        new("LoRa", linked, measured, 300);

    // ── Best, not first ───────────────────────────────────────────────────────

    /// <summary>
    /// The widest linked radio carries, whatever order they came up in.
    /// </summary>
    /// <remarks>
    /// LoRa often connects first and moves a few hundred bits a second; Wi-Fi Direct arrives later and
    /// carries a call. First-through would leave the conversation on the wrong one.
    /// </remarks>
    [Fact]
    public void The_widest_linked_radio_carries()
    {
        var best = RadioChoice.Best([Lora(), Ble(), Direct(), Wifi()]);

        Assert.Equal("Wi-Fi Direct", best!.Value.Name);
    }

    /// <summary>A radio that is not linked does not carry, however fast it claims to be.</summary>
    [Fact]
    public void An_unlinked_radio_does_not_carry()
    {
        var best = RadioChoice.Best([Direct(linked: false), Ble()]);

        Assert.Equal("BLE", best!.Value.Name);
    }

    [Fact]
    public void Nothing_linked_is_nothing_carrying()
    {
        Assert.Null(RadioChoice.Best([Direct(linked: false), Ble(linked: false)]));
        Assert.Empty(RadioChoice.Order([]));
    }

    /// <summary>
    /// Everything linked stays in the list behind the winner.
    /// </summary>
    /// <remarks>
    /// A send that fails on the best radio drops to the next rather than failing outright, so a call
    /// does not die the instant its radio does.
    /// </remarks>
    [Fact]
    public void Everything_linked_stays_behind_the_winner()
    {
        var order = RadioChoice.Order([Ble(), Direct(), Lora()]);

        Assert.Equal(["Wi-Fi Direct", "BLE", "LoRa"], order.Select(r => r.Name));
    }

    // ── Measured beats advertised ─────────────────────────────────────────────

    /// <summary>
    /// What has actually crossed wins over what a radio says about itself.
    /// </summary>
    /// <remarks>
    /// Every advertised figure here has been wrong. BLE published 2 Mbps and delivered 11 kbps one
    /// way; Wi-Fi Direct still reports a flat 250 Mbps nothing has checked. A radio measured at a
    /// trickle should not keep the traffic because of its own brochure.
    /// </remarks>
    [Fact]
    public void What_has_crossed_beats_what_it_claims()
    {
        var best = RadioChoice.Best([Direct(measured: 40_000), Wifi(measured: 30_000_000)]);

        Assert.Equal("Wi-Fi", best!.Value.Name);
    }

    /// <summary>Until something has crossed, the claim is all there is.</summary>
    [Fact]
    public void With_nothing_measured_the_claim_is_used()
    {
        var best = RadioChoice.Best([Ble(measured: 0), Wifi(measured: 0)]);

        Assert.Equal("Wi-Fi", best!.Value.Name);
    }

    // ── Handing over, and not thrashing ───────────────────────────────────────

    /// <summary>
    /// A clearly wider radio takes the traffic over mid-conversation.
    /// </summary>
    /// <remarks>
    /// LoRa gets through first, you use LoRa; Wi-Fi Direct comes up ten seconds later and the call
    /// moves to it without anybody being told.
    /// </remarks>
    [Fact]
    public void A_wider_radio_takes_over()
    {
        var best = RadioChoice.Best([Lora(), Direct()], carrying: "LoRa");

        Assert.Equal("Wi-Fi Direct", best!.Value.Name);
    }

    /// <summary>
    /// But a near-tie does not move it.
    /// </summary>
    /// <remarks>
    /// The measured figure moves with every packet. Sorting purely by speed ping-pongs the traffic
    /// between two similar radios mid-call, re-handshaking each time — which reads as a bad line and
    /// is really two radios being polite at each other.
    /// </remarks>
    [Fact]
    public void A_near_tie_does_not_move_the_traffic()
    {
        var carrying = new RadioSpeed("Wi-Fi", true, 30_000_000, 100_000_000);
        var rival = new RadioSpeed("Wi-Fi Direct", true, 31_000_000, 250_000_000);

        Assert.Equal("Wi-Fi", RadioChoice.Best([rival, carrying], carrying: "Wi-Fi")!.Value.Name);
    }

    /// <summary>And a radio that has dropped does not keep it out of politeness.</summary>
    [Fact]
    public void A_radio_that_dropped_loses_the_traffic()
    {
        var best = RadioChoice.Best([Wifi(linked: false), Ble()], carrying: "Wi-Fi");

        Assert.Equal("BLE", best!.Value.Name);
    }
}
