// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// Arranging a Wi-Fi Direct group over Bluetooth instead of racing for one.
///
/// <para>
/// Two phones calling <c>connect()</c> at each other is a race, and losing it is not quiet: Android
/// falls back to an <b>"Invitation to connect"</b> dialog on the other handset that nobody is looking
/// at, and which takes window focus so the app looks wedged too. Watched on merlin 2026-08-17 — a
/// group that formed in seconds when the timing happened to work, and not at all in eight minutes
/// when it did not.
/// </para>
///
/// <para>
/// So one side creates the group outright and passes the key over the link that already works. These
/// cover the two things that decide whether that is sound: both phones must reach the <b>same</b>
/// answer about who hosts, and the key must never be readable by anyone else.
/// </para>
/// </summary>
public class WifiDirectBrokerTests
{
    private const string Lower = "KSQMM-T9G3E";
    private const string Higher = "QAVYZ-K8YFY";

    // ── Exactly one of them hosts ─────────────────────────────────────────────

    /// <summary>
    /// The property the whole scheme rests on. If both hosted there would be two groups and no
    /// meeting; if neither did there would be none. Each phone decides alone, so the rule has to give
    /// opposite answers to the two of them — every time, with no round trip to disagree over.
    /// </summary>
    [Theory]
    [InlineData(Lower, Higher)]
    [InlineData(Higher, Lower)]
    [InlineData("AAAAA-11111", "ZZZZZ-99999")]
    [InlineData("KXJB7-MN2P4", "DY5CF-84G9T")]
    public void Exactly_one_of_two_phones_hosts(string a, string b)
    {
        var aHosts = WifiDirectBroker.HostsTheGroup(a, b);
        var bHosts = WifiDirectBroker.HostsTheGroup(b, a);

        Assert.NotEqual(aHosts, bHosts);
    }

    [Fact]
    public void The_same_pair_always_reaches_the_same_answer()
    {
        Assert.Equal(
            WifiDirectBroker.HostsTheGroup(Lower, Higher),
            WifiDirectBroker.HostsTheGroup(Lower, Higher));
    }

    /// <summary>A phone cannot host a group with itself — that would be both sides waiting.</summary>
    [Fact]
    public void A_phone_does_not_host_against_its_own_tag() =>
        Assert.False(WifiDirectBroker.HostsTheGroup(Lower, Lower));

    // ── The credentials are a secret worth checking ───────────────────────────

    [Fact]
    public void Credentials_survive_being_written_and_read()
    {
        var original = new WifiDirectCredentials("DIRECT-Ab-Aether", "correcthorsebattery");

        var read = WifiDirectCredentials.Parse(original.ToJson());

        Assert.Equal(original, read);
    }

    /// <summary>
    /// These arrive from another device and decide which network this phone attaches itself to, so
    /// anything of the wrong shape is refused rather than attempted. Android names every P2P group
    /// <c>DIRECT-</c>something; a name of any other shape can only be a mistake or an attempt to steer
    /// this phone somewhere.
    /// </summary>
    [Theory]
    [InlineData("MyHomeWiFi", "correcthorsebattery")]          // not a P2P group at all
    [InlineData("", "correcthorsebattery")]
    [InlineData("DIRECT-", "correcthorsebattery")]             // the prefix and nothing else
    [InlineData("DIRECT-Ab-Aether", "short")]                  // below the WPA2 minimum
    [InlineData("DIRECT-Ab-Aether", "")]
    public void Credentials_of_the_wrong_shape_are_refused(string ssid, string passphrase) =>
        Assert.False(WifiDirectCredentials.IsUsable(new WifiDirectCredentials(ssid, passphrase)));

    [Fact]
    public void Nothing_is_not_usable_credentials() =>
        Assert.False(WifiDirectCredentials.IsUsable(null));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"ssid":"MyHomeWiFi","pass":"correcthorsebattery"}""")]
    public void Rubbish_on_the_wire_parses_to_nothing_rather_than_something_wrong(string json) =>
        Assert.Null(WifiDirectCredentials.Parse(json));

    [Fact]
    public void A_real_android_group_name_is_accepted() =>
        Assert.True(WifiDirectCredentials.IsUsable(
            new WifiDirectCredentials("DIRECT-tg-Android_1a2b", "vTk7QpLm93xZ")));
}
