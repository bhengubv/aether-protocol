// SPDX-License-Identifier: MIT

using AetherNetNodeService.Host.Tests.Fakes;
using AetherNetService;
using Xunit;

namespace AetherNetNodeService.Host.Tests;

/// <summary>
/// The words over a radio's button say what the button does. A permission is asked for before a switch, so while one
/// is missing it is what AetherNetService says of the radio, whatever else is off. With Bluetooth off and the permission
/// not yet allowed, the welcome read "Bluetooth is switched off" over an Allow that asked for the permission (P30,
/// 2026-10-24).
/// </summary>
public class PermissionIsSaidBeforeTheSwitchTests
{
    private static AetherNetNodeService.RadioStatus Shown(RadioInfo radio)
    {
        var mesh = new FakeRadioMesh("ME") { Radios = [radio] };
        return Assert.Single(new MeshNodeLinkSource(mesh, () => []).Current.Radios);
    }

    [Fact]
    public void With_Bluetooth_off_and_its_permission_missing_the_permission_is_said()
    {
        var ble = Shown(new RadioInfo("BLE", false, "Bluetooth is switched off", Fixable: true, NeedsPermission: true));

        Assert.True(ble.NeedsPermission);
        Assert.Equal("needs permission to find phones nearby", ble.Reason);
    }

    /// <summary>The same words as with Bluetooth on, and as Wi-Fi Direct and Wi-Fi Aware say them.</summary>
    [Fact]
    public void With_only_the_permission_missing_the_words_are_the_same()
    {
        var ble = Shown(new RadioInfo("BLE", false, "needs permission to find phones nearby", Fixable: true, NeedsPermission: true));

        Assert.Equal("needs permission to find phones nearby", ble.Reason);
    }

    [Fact]
    public void Once_the_permission_is_allowed_the_switch_is_said()
    {
        var ble = Shown(new RadioInfo("BLE", false, "Bluetooth is switched off", Fixable: true));

        Assert.False(ble.NeedsPermission);
        Assert.True(ble.Fixable);
        Assert.Equal("Bluetooth is switched off", ble.Reason);
    }

    [Fact]
    public void A_radio_that_cannot_be_put_right_keeps_its_own_words()
    {
        var wifiAware = Shown(new RadioInfo("Wi-Fi Aware", false, "this phone does not have Wi-Fi Aware"));

        Assert.False(wifiAware.Fixable);
        Assert.Equal("this phone does not have Wi-Fi Aware", wifiAware.Reason);
    }

    [Fact]
    public void A_radio_that_works_says_nothing()
    {
        var ble = Shown(new RadioInfo("BLE", true));

        Assert.True(ble.Available);
        Assert.Null(ble.Reason);
    }
}
