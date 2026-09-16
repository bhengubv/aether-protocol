// SPDX-License-Identifier: MIT

using AetherNet.Transport.Services;
using Xunit;

namespace AetherNet.Transport.Tests;

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

    // ── Capability-aware: prefer what the PEER can also carry ──────────────────

    private static RadioSpeed TDirect(bool linked = true, long measured = 0) =>
        Direct(linked, measured) with { Transport = TransportCapability.WifiDirect };

    private static RadioSpeed TBle(bool linked = true, long measured = 0) =>
        Ble(linked, measured) with { Transport = TransportCapability.Ble };

    private static RadioSpeed TAware(bool linked = true, long measured = 0) =>
        new("Wi-Fi Aware", linked, measured, 50_000_000) { Transport = TransportCapability.WifiAware };

    private static IReadOnlySet<string> Carries(params string[] tags) =>
        new HashSet<string>(tags, StringComparer.Ordinal);

    /// <summary>
    /// With nothing negotiated, the choice is exactly the peer-agnostic one.
    /// </summary>
    /// <remarks>
    /// Two phones that have not finished the capability handshake must behave as the app always did, or
    /// the first message of every conversation would wait on a negotiation that has not happened yet.
    /// </remarks>
    [Fact]
    public void No_peer_transports_behaves_as_before()
    {
        IReadOnlySet<string>? none = null;

        var withNull = RadioChoice.Order([TBle(), TDirect()], carrying: null, none);
        var withEmpty = RadioChoice.Order([TBle(), TDirect()], carrying: null, Carries());
        var plain = RadioChoice.Order([TBle(), TDirect()]);

        Assert.Equal(plain.Select(r => r.Name), withNull.Select(r => r.Name));
        Assert.Equal(plain.Select(r => r.Name), withEmpty.Select(r => r.Name));
    }

    /// <summary>
    /// A radio measured faster locally yields to a slower one the peer can actually hear.
    /// </summary>
    /// <remarks>
    /// This is the whole point. BLE at five megabits to a phone that only carries Wi-Fi Direct delivers
    /// nothing — the peer has no BLE transport to receive it — so the slower Wi-Fi Direct link, which
    /// both ends have, must lead however fast BLE looks from this side.
    /// </remarks>
    [Fact]
    public void A_transport_the_peer_lacks_yields_to_one_it_has()
    {
        var best = RadioChoice.Best(
            [TBle(measured: 5_000_000), TDirect(measured: 1_000)],
            carrying: null,
            Carries(TransportCapability.WifiDirect));

        Assert.Equal("Wi-Fi Direct", best!.Value.Name);
    }

    /// <summary>
    /// The SAME radios yield a different winner depending on what the peer can carry.
    /// </summary>
    /// <remarks>
    /// Aware measured wider here, so between two Aware-capable phones it carries. Against a phone
    /// without Aware, that same wider link is demoted below the Wi-Fi Direct both ends share — a link
    /// the peer cannot hear does not win for being fast. The preference is the peer's capability, not
    /// an opinion about which radio is nicer: throughput still decides within what both can carry.
    /// </remarks>
    [Fact]
    public void The_mutual_best_depends_on_the_peer()
    {
        RadioSpeed[] radios = [TDirect(measured: 20_000_000), TAware(measured: 40_000_000)];

        var awarePeer = RadioChoice.Best(radios, null,
            Carries(TransportCapability.WifiDirect, TransportCapability.WifiAware));
        var directOnlyPeer = RadioChoice.Best(radios, null,
            Carries(TransportCapability.WifiDirect));

        Assert.Equal("Wi-Fi Aware", awarePeer!.Value.Name);      // wider AND mutual → carries
        Assert.Equal("Wi-Fi Direct", directOnlyPeer!.Value.Name); // wider but not mutual → demoted
    }

    /// <summary>
    /// A transport the peer lacks is still offered — reachability over tidiness.
    /// </summary>
    /// <remarks>
    /// If the only link that formed is one the peer "should not" prefer, it still carries: a link that
    /// is all there is beats no link. The negotiated preference orders the radios, it never deletes one.
    /// </remarks>
    [Fact]
    public void A_transport_the_peer_lacks_is_still_offered()
    {
        var order = RadioChoice.Order(
            [TBle()],
            carrying: null,
            Carries(TransportCapability.WifiAware));

        Assert.Equal(["BLE"], order.Select(r => r.Name));
    }

    /// <summary>
    /// Moving UP onto a mutual transport is not held back by hysteresis.
    /// </summary>
    /// <remarks>
    /// Hysteresis stops speed noise bouncing the traffic between two comparable radios. Climbing off a
    /// transport the peer cannot hear onto one it can is not speed noise — it is the difference between
    /// delivered and not — so it happens at once, even though BLE here measures far wider than the
    /// Wi-Fi Direct link taking over.
    /// </remarks>
    [Fact]
    public void Upgrading_onto_a_mutual_transport_ignores_hysteresis()
    {
        var best = RadioChoice.Best(
            [TBle(measured: 10_000_000), TDirect(measured: 1_000)],
            carrying: "BLE",
            Carries(TransportCapability.WifiDirect));

        Assert.Equal("Wi-Fi Direct", best!.Value.Name);
    }

    /// <summary>
    /// Within the mutual group, hysteresis still keeps a near-tie from thrashing.
    /// </summary>
    [Fact]
    public void Within_the_mutual_group_a_near_tie_holds()
    {
        RadioSpeed[] radios =
        [
            TDirect(measured: 30_000_000),
            TAware(measured: 31_000_000),
        ];
        var peer = Carries(TransportCapability.WifiDirect, TransportCapability.WifiAware);

        // Carrying Wi-Fi Direct, a barely-wider Aware does not steal it…
        Assert.Equal("Wi-Fi Direct",
            RadioChoice.Best(radios, "Wi-Fi Direct", peer)!.Value.Name);

        // …but a clearly wider one does.
        RadioSpeed[] clearlyWider = [TDirect(measured: 30_000_000), TAware(measured: 90_000_000)];
        Assert.Equal("Wi-Fi Aware",
            RadioChoice.Best(clearlyWider, "Wi-Fi Direct", peer)!.Value.Name);
    }

    /// <summary>The mutual leader leads and the rest follow it, never dropped.</summary>
    [Fact]
    public void Mutual_first_then_the_rest()
    {
        var order = RadioChoice.Order(
            [TBle(measured: 5_000_000), TDirect(measured: 1_000), Lora()],
            carrying: null,
            Carries(TransportCapability.WifiDirect));

        Assert.Equal("Wi-Fi Direct", order[0].Name);              // mutual leads despite being slowest
        Assert.Contains(order, r => r.Name == "BLE");             // fallbacks remain
        Assert.Contains(order, r => r.Name == "LoRa");
    }
}
