// SPDX-License-Identifier: MIT

using System.Text;
using AetherNet.Rendezvous;
using AetherNet.Sample.Shared.Services;
using AetherNet.Transport.Wifi;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Two phones already on the same Wi-Fi, using it.
///
/// <para>
/// Wi-Fi Direct builds a network out of nothing, which is right in a field and slow and fragile in a
/// kitchen where both handsets are three metres from the same access point. Two phones sat on one
/// network for an afternoon unable to reach each other while a perfectly good link went unused.
/// </para>
///
/// <para>
/// These run over real sockets on the machine's own network rather than against a fake, because what
/// is being claimed is that two processes find each other and move bytes — a stand-in for the network
/// would prove only that the stand-in works.
/// </para>
/// </summary>
public class WifiTransportTests
{
    private const string Merlin = "7RB9G-97RTG";

    private const string P30 = "Y6TK9-EW9KK";

    /// <summary>How long to give two sockets on the same machine before calling it a failure.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static async Task<bool> UntilAsync(Func<bool> done)
    {
        var giveUp = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < giveUp)
        {
            if (done()) return true;
            await Task.Delay(100);
        }

        return done();
    }

    // ── Finding each other ────────────────────────────────────────────────────

    /// <summary>
    /// Two nodes that were only ever given each other's tags find each other and link.
    /// </summary>
    /// <remarks>
    /// Nothing is scanned and nothing is asked of the network. Both worked out where to meet from the
    /// two tags before either touched a socket, so the only devices that could possibly answer are the
    /// two that were handed both.
    /// </remarks>
    [Fact]
    public async Task Two_nodes_on_one_network_find_each_other()
    {
        var meet = Meeting.With(Merlin, P30)!.Value;

        using var host = new WifiTransportService(Merlin);
        using var joiner = new WifiTransportService(P30);

        if (!host.IsAvailable) return;   // no network on this machine; nothing to prove

        var linked = new List<string>();
        host.PeerLinked += p => { lock (linked) linked.Add(p); };
        joiner.PeerLinked += p => { lock (linked) linked.Add(p); };

        await host.MeetAsync(meet.Rendezvous, iStart: true);
        await joiner.MeetAsync(meet.Rendezvous, iStart: false);

        Assert.True(
            await UntilAsync(() => host.IsConnected(P30) && joiner.IsConnected(Merlin)),
            "two nodes on the same network did not find each other");
    }

    /// <summary>And then carry bytes, both ways.</summary>
    [Fact]
    public async Task They_carry_bytes_both_ways()
    {
        var meet = Meeting.With(Merlin, P30)!.Value;

        using var host = new WifiTransportService(Merlin);
        using var joiner = new WifiTransportService(P30);

        if (!host.IsAvailable) return;

        var heard = new List<string>();
        void Note(string from, byte[] data)
        {
            lock (heard) heard.Add($"{from}:{Encoding.UTF8.GetString(data)}");
        }

        host.DataReceived += Note;
        joiner.DataReceived += Note;

        await host.MeetAsync(meet.Rendezvous, iStart: true);
        await joiner.MeetAsync(meet.Rendezvous, iStart: false);

        Assert.True(await UntilAsync(() => host.IsConnected(P30) && joiner.IsConnected(Merlin)));

        Assert.True(await host.SendAsync(P30, "from merlin"u8.ToArray()));
        Assert.True(await joiner.SendAsync(Merlin, "from the p30"u8.ToArray()));

        Assert.True(
            await UntilAsync(() =>
            {
                lock (heard) return heard.Count >= 2;
            }),
            "bytes did not cross");

        lock (heard)
        {
            Assert.Contains($"{Merlin}:from merlin", heard);
            Assert.Contains($"{P30}:from the p30", heard);
        }
    }

    // ── Who it will not meet ──────────────────────────────────────────────────

    /// <summary>
    /// A node that was never given both tags cannot compute the meeting, so it hears nothing for it.
    /// </summary>
    /// <remarks>
    /// The rule the whole design rests on: you meet people whose tag you were handed, and nobody else.
    /// A stranger on the same access point is on the same wire and still has nowhere to knock.
    /// </remarks>
    [Fact]
    public async Task A_stranger_on_the_same_network_finds_nothing()
    {
        var ours = Meeting.With(Merlin, P30)!.Value;
        var theirs = Meeting.With("KXJB7-MN2P4", "Q3WRT-88ZZA")!.Value;

        using var host = new WifiTransportService(Merlin);
        using var stranger = new WifiTransportService("KXJB7-MN2P4");

        if (!host.IsAvailable) return;

        await host.MeetAsync(ours.Rendezvous, iStart: true);
        await stranger.MeetAsync(theirs.Rendezvous, iStart: false);

        await Task.Delay(3000);

        Assert.False(stranger.IsConnected(Merlin), "a stranger linked to a meeting it was not part of");
        Assert.False(host.IsConnected("KXJB7-MN2P4"));
    }

    // ── What it says about itself ─────────────────────────────────────────────

    /// <summary>
    /// It reports itself below Wi-Fi Direct.
    /// </summary>
    /// <remarks>
    /// Both are fast enough for anything here. When a pair has both, the one with no third party in it
    /// should carry the traffic — so the numbers have to order that way rather than being flattering.
    /// </remarks>
    [Fact]
    public void It_ranks_below_wifi_direct()
    {
        using var wifi = new WifiTransportService(Merlin);

        Assert.Equal("Wi-Fi", wifi.Name);
        Assert.True(wifi.MaxBandwidthBps < 250_000_000, "it claimed to beat Wi-Fi Direct");
        Assert.True(wifi.MaxBandwidthBps > 11_000, "it claimed to be no better than BLE");
    }

    /// <summary>
    /// Being asked to keep the same meeting again costs nothing.
    /// </summary>
    /// <remarks>
    /// The radio bring-up repeats on purpose — there is no moment either phone can point to and say
    /// "the other one is ready now" — so this is asked over and over. Without a guard it opened a
    /// fresh socket and dialled again each time, and a perfectly healthy link re-handshook on a loop.
    /// Measured on the bench as an endless run of "connected to 192.168.0.203 / linked with …".
    /// </remarks>
    [Fact]
    public async Task Being_asked_again_does_not_start_over()
    {
        var meet = Meeting.With(Merlin, P30)!.Value;

        using var host = new WifiTransportService(Merlin);
        using var joiner = new WifiTransportService(P30);

        if (!host.IsAvailable) return;

        var links = 0;
        joiner.PeerLinked += _ => Interlocked.Increment(ref links);

        await host.MeetAsync(meet.Rendezvous, iStart: true);
        await joiner.MeetAsync(meet.Rendezvous, iStart: false);

        Assert.True(await UntilAsync(() => joiner.IsConnected(Merlin)));

        // What the bring-up does every few seconds, for as long as the app is running.
        for (var again = 0; again < 5; again++)
        {
            await host.MeetAsync(meet.Rendezvous, iStart: true);
            await joiner.MeetAsync(meet.Rendezvous, iStart: false);
        }

        await Task.Delay(2000);

        Assert.Equal(1, links);
        Assert.True(joiner.IsConnected(Merlin), "the link did not survive being asked again");
    }

    [Fact]
    public async Task Sending_to_somebody_who_is_not_there_says_so()
    {
        using var alone = new WifiTransportService(Merlin);

        Assert.False(await alone.SendAsync(P30, "hello"u8.ToArray()));
        Assert.False(alone.IsConnected(P30));
    }
}
