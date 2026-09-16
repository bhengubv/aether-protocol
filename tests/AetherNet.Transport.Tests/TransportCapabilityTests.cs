// SPDX-License-Identifier: MIT

using AetherNet.Transport.Services;
using Xunit;

namespace AetherNet.Transport.Tests;

/// <summary>
/// The names transports go by when two phones tell each other what they can carry. Both the side that
/// advertises a tag and the side that ranks against it read them from here, so the one thing that must
/// hold is that they agree — a tag written one way here and matched another way there negotiates nothing.
/// </summary>
public class TransportCapabilityTests
{
    [Theory]
    [InlineData("Wi-Fi Direct", "transport:wifi-direct")]
    [InlineData("Wi-Fi Aware", "transport:wifi-aware")]
    [InlineData("BLE", "transport:ble")]
    [InlineData("Bluetooth", "transport:ble")]
    [InlineData("Wi-Fi", "transport:wifi")]
    [InlineData("Internet", "transport:internet")]
    [InlineData("Mobile data", "transport:mobile-data")]
    [InlineData("LoRa", "transport:lora")]
    public void Known_radios_map_to_their_tag(string name, string tag)
        => Assert.Equal(tag, TransportCapability.TagFor(name));

    /// <summary>
    /// A radio that carries nothing, or one nobody can name, has no tag — a transport no peer can match.
    /// </summary>
    [Fact]
    public void An_unknown_or_carryless_radio_has_no_tag()
    {
        Assert.Null(TransportCapability.TagFor("NFC"));       // a way to meet, not a way to carry
        Assert.Null(TransportCapability.TagFor("NearLink")); // no portable form on Android
        Assert.Null(TransportCapability.TagFor("Telepathy"));
        Assert.Null(TransportCapability.TagFor(null));
    }

    /// <summary>
    /// Transport tags are pickable back out of a negotiated set; protocol-feature tags are left alone.
    /// </summary>
    [Fact]
    public void Transport_tags_are_recognised_and_feature_tags_are_not()
    {
        Assert.True(TransportCapability.IsTransport(TransportCapability.WifiDirect));
        Assert.True(TransportCapability.IsTransport("transport:anything"));

        Assert.False(TransportCapability.IsTransport("signal-x3dh")); // a protocol feature, not a transport
        Assert.False(TransportCapability.IsTransport("voice"));
        Assert.False(TransportCapability.IsTransport(null));
    }

    /// <summary>Every named constant is itself a transport tag — no typo slips the prefix.</summary>
    [Fact]
    public void Every_named_tag_carries_the_prefix()
    {
        string[] all =
        [
            TransportCapability.WifiDirect, TransportCapability.WifiAware, TransportCapability.Ble,
            TransportCapability.Wifi, TransportCapability.Internet, TransportCapability.MobileData,
            TransportCapability.Lora,
        ];

        foreach (var tag in all) Assert.True(TransportCapability.IsTransport(tag));
    }
}
