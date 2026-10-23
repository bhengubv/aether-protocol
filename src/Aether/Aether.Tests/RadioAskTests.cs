// SPDX-License-Identifier: MIT

using AetherNetService;
using Xunit;

namespace Aether.Tests;

/// <summary>
/// What AetherNetService's see-through AskActivity asks the phone for when a radio's button is tapped in an app: the
/// permission the radio checks before it touches its stack, or the switch it says it is waiting on — on the Android
/// versions where those differ.
/// </summary>
public sealed class RadioAskTests
{
    private const int Android10 = 29;
    private const int Android12 = 31;
    private const int Android13 = 33;

    /// <summary>A phone with every switch on.</summary>
    private static PhoneNow AllOn(int sdk) =>
        new(sdk, BluetoothOn: true, LocationOn: true, WifiOn: true, NfcOn: true, HasNetwork: true);

    [Fact]
    public void Bluetooth_asks_for_nearby_devices_from_Android_12_and_for_location_before()
    {
        Assert.Equal(
            ["android.permission.BLUETOOTH_SCAN", "android.permission.BLUETOOTH_CONNECT", "android.permission.BLUETOOTH_ADVERTISE"],
            RadioAsk.Permissions("BLE", Android12));
        Assert.Equal(["android.permission.ACCESS_FINE_LOCATION"], RadioAsk.Permissions("BLE", Android10));
    }

    [Theory]
    [InlineData("Wi-Fi Direct")]
    [InlineData("Wi-Fi Aware")]
    public void Wifi_asks_for_nearby_devices_from_Android_13_and_for_location_before(string radio)
    {
        Assert.Equal(["android.permission.NEARBY_WIFI_DEVICES"], RadioAsk.Permissions(radio, Android13));
        Assert.Equal(["android.permission.ACCESS_FINE_LOCATION"], RadioAsk.Permissions(radio, Android12));
        Assert.Equal(["android.permission.ACCESS_FINE_LOCATION"], RadioAsk.Permissions(radio, Android10));
    }

    [Theory]
    [InlineData("NFC")]
    [InlineData("Internet")]
    [InlineData("Wi-Fi")]
    [InlineData("LoRa")]
    [InlineData(null)]
    public void A_radio_with_no_permission_of_its_own_asks_for_none(string? radio) =>
        Assert.Empty(RadioAsk.Permissions(radio, Android13));

    [Fact]
    public void Bluetooth_switched_off_is_asked_for_first() =>
        Assert.Equal(PhoneSwitch.Bluetooth,
            RadioAsk.SwitchFor("BLE", AllOn(Android10) with { BluetoothOn = false, LocationOn = false }));

    [Fact]
    public void Location_off_holds_Bluetooth_back_only_below_Android_12()
    {
        Assert.Equal(PhoneSwitch.Location, RadioAsk.SwitchFor("BLE", AllOn(Android10) with { LocationOn = false }));
        Assert.Null(RadioAsk.SwitchFor("BLE", AllOn(Android12) with { LocationOn = false }));
    }

    [Fact]
    public void Location_off_holds_Wifi_Direct_back_only_below_Android_13()
    {
        Assert.Equal(PhoneSwitch.Location, RadioAsk.SwitchFor("Wi-Fi Direct", AllOn(Android12) with { LocationOn = false }));
        Assert.Null(RadioAsk.SwitchFor("Wi-Fi Direct", AllOn(Android13) with { LocationOn = false }));
    }

    [Fact]
    public void Wifi_Aware_asks_for_Wifi_then_for_location()
    {
        var off = AllOn(Android13) with { WifiOn = false, LocationOn = false };
        Assert.Equal(PhoneSwitch.Wifi, RadioAsk.SwitchFor("Wi-Fi Aware", off));
        Assert.Equal(PhoneSwitch.Location, RadioAsk.SwitchFor("Wi-Fi Aware", off with { WifiOn = true }));
    }

    [Fact]
    public void NFC_and_Wifi_switched_off_ask_for_their_own_switches()
    {
        Assert.Equal(PhoneSwitch.Nfc, RadioAsk.SwitchFor("NFC", AllOn(Android10) with { NfcOn = false }));
        Assert.Equal(PhoneSwitch.Wifi, RadioAsk.SwitchFor("Wi-Fi", AllOn(Android10) with { WifiOn = false }));
    }

    [Fact]
    public void No_internet_asks_for_the_networks_and_a_relay_is_no_switch_on_this_phone()
    {
        Assert.Equal(PhoneSwitch.Internet, RadioAsk.SwitchFor("Internet", AllOn(Android10) with { HasNetwork = false }));
        Assert.Null(RadioAsk.SwitchFor("Internet", AllOn(Android10)));   // a network, and nobody in the Circle relaying
    }

    [Theory]
    [InlineData("BLE")]
    [InlineData("Wi-Fi Direct")]
    [InlineData("Wi-Fi Aware")]
    [InlineData("Wi-Fi")]
    [InlineData("NFC")]
    [InlineData("LoRa")]
    public void With_every_switch_on_there_is_nothing_to_switch(string radio) =>
        Assert.Null(RadioAsk.SwitchFor(radio, AllOn(Android10)));

    [Theory]
    [InlineData(false, false, true)]   // never asked: the phone always puts its prompt up
    [InlineData(true, true, true)]     // said no once: the phone may ask again
    [InlineData(true, false, false)]   // told "don't ask again": the phone answers no by itself
    public void The_phone_asks_until_it_is_told_never_to(bool askedBefore, bool mayAskAgain, bool willAsk) =>
        Assert.Equal(willAsk, RadioAsk.PhoneWillAsk(askedBefore, mayAskAgain));
}
