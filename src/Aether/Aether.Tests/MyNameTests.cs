// SPDX-License-Identifier: MIT

extern alias service;

using MyName = service::AetherNet.Browser.MyName;
using Aether.Shared.Services;
using Xunit;

namespace Aether.Tests;

/// <summary>
/// The one string a stranger reads before deciding to trust anything.
///
/// <para>
/// Touch My Blood was reshaped around the fact that a raw IP address reads as a scam to somebody being
/// handed an app. The address went — and <c>KXJB7-MN2P4</c> took its place on the network name and on
/// the card, which is the same problem wearing different clothes. Nobody can tell one random string
/// from another.
/// </para>
///
/// <para>
/// So this has to survive a Wi-Fi picker on an unfamiliar handset, fit inside a network name Android
/// has already spent seven characters of, and still be recognisably a person.
/// </para>
/// </summary>
public class MyNameTests
{
    /// <summary>And what is left really does fit.</summary>
    [Fact]
    public void The_cut_name_fits_inside_a_wifi_network_name()
    {
        var network = GuestGroup.For(MyName.Clean(new string('W', 60)));

        Assert.True(network.NetworkName.Length <= GuestGroup.LongestName,
            $"'{network.NetworkName}' is {network.NetworkName.Length} characters");
    }

    /// <summary>
    /// The name reaches the network a stranger actually sees.
    /// </summary>
    /// <remarks>
    /// The end-to-end assertion, because this is the whole point: what appears in somebody's Wi-Fi
    /// list is a person, not a code.
    /// </remarks>
    [Fact]
    public void The_network_a_stranger_sees_carries_the_name()
    {
        var network = GuestGroup.For(MyName.OrTag("Thabang", "KXJB7-MN2P4"));

        Assert.Contains("Thabang", network.NetworkName, StringComparison.Ordinal);
        Assert.DoesNotContain("KXJB7", network.NetworkName, StringComparison.Ordinal);
    }
}
