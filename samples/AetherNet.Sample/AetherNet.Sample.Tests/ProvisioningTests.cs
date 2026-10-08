// SPDX-License-Identifier: MIT

using AetherNet.Sample.Shared.Services;
using System.Text;
using Xunit;

namespace AetherNet.Sample.Tests;

/// <summary>
/// The tap that asks Android to install us itself.
///
/// <para>
/// Everything else we built ends with a person being asked to do something — read an address, trust a
/// page, accept a file — and a stranger is right to refuse all three. This one hands the operating
/// system a network, a fingerprint and a place, and it does the rest without rendering any of it.
/// </para>
///
/// <para>
/// Three things have to be exactly right or the tap is silently wasted, and none of them can be seen
/// by looking at a phone: the payload has to be properties Android can parse, the record has to
/// declare its own length honestly, and the fingerprint has to be the one shape this path reads.
/// </para>
/// </summary>
public class ProvisioningTests
{
    private const string Package = "com.bhengubv.aethernet";
    private const string Location = "http://192.168.49.1:40813/tmb/9b2993fde0092c4f/aether.apk";
    private const string Ssid = "DIRECT-Aether Y6TK9-EW9KK";
    private const string Passphrase = "8QK2M4TVXR7NPJ3W";

    /// <summary>
    /// The Wi-Fi handover record this sits beside is untouched by any of it.
    /// </summary>
    /// <remarks>
    /// That one is proven on silicon — a stock phone read it and joined the network — so the guard
    /// here is against a shared record writer quietly changing what the proven tap emits.
    /// </remarks>
    [Fact]
    public void The_proven_wifi_record_is_unchanged()
    {
        var wifi = WifiHandover.Message(Ssid, Passphrase);

        Assert.Equal(0x80 | 0x40 | 0x10 | 0x02, wifi[0]);
        Assert.Equal(WifiHandover.MimeType.Length, wifi[1]);
    }
}
