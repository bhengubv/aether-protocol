// SPDX-License-Identifier: MIT

using AetherNetService;
using Xunit;

namespace Aether.Tests;

/// <summary>
/// What AetherNetService's see-through AskActivity asks the phone for when a button is tapped in an app — for a radio,
/// for Aware or Quiet help, or for one of the permissions Settings lists: the permission still missing, or the switch
/// it is waiting on — on the Android versions where those differ.
/// </summary>
public sealed class RadioAskTests
{
    private const int Android9 = 28;
    private const int Android10 = 29;
    private const int Android12 = 31;
    private const int Android13 = 33;

    private const string Fine = "android.permission.ACCESS_FINE_LOCATION";
    private const string Coarse = "android.permission.ACCESS_COARSE_LOCATION";
    private const string Background = "android.permission.ACCESS_BACKGROUND_LOCATION";
    private const string Scan = "android.permission.BLUETOOTH_SCAN";
    private const string Connect = "android.permission.BLUETOOTH_CONNECT";
    private const string Advertise = "android.permission.BLUETOOTH_ADVERTISE";
    private const string NearbyWifi = "android.permission.NEARBY_WIFI_DEVICES";
    private const string PostNotifications = "android.permission.POST_NOTIFICATIONS";

    /// <summary>A phone with every switch on.</summary>
    private static PhoneNow AllOn(int sdk) =>
        new(sdk, BluetoothOn: true, LocationOn: true, WifiOn: true, NfcOn: true, HasNetwork: true);

    private static Func<string, bool> Allowed(params string[] granted) => p => granted.Contains(p);

    [Fact]
    public void Bluetooth_asks_for_nearby_devices_from_Android_12_and_for_location_before()
    {
        Assert.Equal([Scan, Connect, Advertise], RadioAsk.Permissions("BLE", Android12));
        Assert.Equal([Fine, Coarse], RadioAsk.Permissions("BLE", Android10));
    }

    [Theory]
    [InlineData("Wi-Fi Direct")]
    [InlineData("Wi-Fi Aware")]
    public void Wifi_asks_for_nearby_devices_from_Android_13_and_for_location_before(string radio)
    {
        Assert.Equal([NearbyWifi], RadioAsk.Permissions(radio, Android13));
        Assert.Equal([Fine, Coarse], RadioAsk.Permissions(radio, Android12));
        Assert.Equal([Fine, Coarse], RadioAsk.Permissions(radio, Android10));
    }

    /// <summary>
    /// From Android 12 the phone answers a request for precise location alone with nothing at all — no prompt, just a
    /// no — so precise goes with approximate whenever location is asked for, by a radio or by anything else.
    /// </summary>
    [Theory]
    [InlineData("BLE", Android10)]
    [InlineData("Wi-Fi Direct", Android12)]
    [InlineData("Aware", Android13)]
    [InlineData("Quiet help", Android10)]
    [InlineData("Location", Android13)]
    public void Precise_location_is_never_asked_for_without_approximate(string what, int sdk)
    {
        var asked = RadioAsk.Permissions(what, sdk);

        Assert.Contains(Fine, asked);
        Assert.Contains(Coarse, asked);
    }

    [Theory]
    [InlineData("NFC")]
    [InlineData("Internet")]
    [InlineData("Wi-Fi")]
    [InlineData("LoRa")]
    [InlineData(null)]
    public void A_radio_with_no_permission_of_its_own_asks_for_none(string? radio) =>
        Assert.Empty(RadioAsk.Permissions(radio, Android13));

    // ── Aware and Quiet help ────────────────────────────────────────────────────

    [Fact]
    public void Aware_asks_to_listen_and_advertise_and_to_know_where_it_heard_things()
    {
        Assert.Equal([Scan, Advertise, Fine, Coarse], RadioAsk.Permissions(RadioAsk.Aware, Android12));
        Assert.Equal([Fine, Coarse], RadioAsk.Permissions(RadioAsk.Aware, Android10));
    }

    /// <summary>Quiet help's nearby half is a Bluetooth advert: from Android 12 that is all it asks for.</summary>
    [Fact]
    public void Quiet_help_asks_only_to_advertise_from_Android_12()
    {
        Assert.Equal([Advertise], RadioAsk.Permissions(RadioAsk.QuietHelp, Android13));
        Assert.Equal([Fine, Coarse], RadioAsk.Permissions(RadioAsk.QuietHelp, Android10));
    }

    [Theory]
    [InlineData("Aware")]
    [InlineData("Quiet help")]
    public void Aware_and_Quiet_help_ask_for_Bluetooth_switched_on(string what)
    {
        Assert.Equal(PhoneSwitch.Bluetooth, RadioAsk.SwitchFor(what, AllOn(Android13) with { BluetoothOn = false }));
        Assert.Null(RadioAsk.SwitchFor(what, AllOn(Android13)));
    }

    // ── Settings' lines, by the name the phone gives each permission ─────────────

    [Fact]
    public void Nearby_devices_is_Bluetooth_from_Android_12_and_Wifi_as_well_from_13()
    {
        Assert.Equal([NearbyWifi, Scan, Connect, Advertise], RadioAsk.Permissions(RadioAsk.NearbyDevices, Android13));
        Assert.Equal([Scan, Connect, Advertise], RadioAsk.Permissions(RadioAsk.NearbyDevices, Android12));
        Assert.Empty(RadioAsk.Permissions(RadioAsk.NearbyDevices, Android10));
    }

    [Fact]
    public void Notifications_are_asked_for_from_Android_13()
    {
        Assert.Equal([PostNotifications], RadioAsk.Permissions(RadioAsk.Notifications, Android13));
        Assert.Empty(RadioAsk.Permissions(RadioAsk.Notifications, Android12));
    }

    [Fact]
    public void Location_all_the_time_is_asked_for_from_Android_10()
    {
        Assert.Equal([Background], RadioAsk.Permissions(RadioAsk.LocationAllTheTime, Android12));
        Assert.Empty(RadioAsk.Permissions(RadioAsk.LocationAllTheTime, Android9));
    }

    // ── What is asked for now ───────────────────────────────────────────────────

    [Fact]
    public void Only_what_is_not_allowed_yet_is_asked_for()
    {
        Assert.Equal([Advertise], RadioAsk.Missing(RadioAsk.Aware, Android12, Allowed(Scan, Fine, Coarse)));
        Assert.Empty(RadioAsk.Missing("BLE", Android12, Allowed(Scan, Connect, Advertise)));
    }

    /// <summary>Approximate allowed and precise not: from Android 12 the phone is asked for the pair again.</summary>
    [Fact]
    public void Approximate_alone_is_made_precise_by_asking_for_both_again()
    {
        var asked = RadioAsk.Missing(RadioAsk.Location, Android12, Allowed(Coarse));

        Assert.Equal([Fine, Coarse], asked);
    }

    /// <summary>The phone refuses "all the time" on its own: with no location allowed, location comes first.</summary>
    [Fact]
    public void All_the_time_waits_for_location_to_be_allowed_first()
    {
        Assert.Equal([Fine, Coarse], RadioAsk.Missing(RadioAsk.LocationAllTheTime, Android12, Allowed()));
        Assert.Equal([Background], RadioAsk.Missing(RadioAsk.LocationAllTheTime, Android12, Allowed(Fine, Coarse)));
        Assert.Empty(RadioAsk.Missing(RadioAsk.LocationAllTheTime, Android12, Allowed(Fine, Coarse, Background)));
    }

    // ── The switches ────────────────────────────────────────────────────────────

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
    [InlineData("Location")]
    public void With_every_switch_on_there_is_nothing_to_switch(string what) =>
        Assert.Null(RadioAsk.SwitchFor(what, AllOn(Android10)));

    [Theory]
    [InlineData(false, false, true)]   // never asked: the phone always puts its prompt up
    [InlineData(true, true, true)]     // said no once: the phone may ask again
    [InlineData(true, false, false)]   // told "don't ask again": the phone answers no by itself
    public void The_phone_asks_until_it_is_told_never_to(bool askedBefore, bool mayAskAgain, bool willAsk) =>
        Assert.Equal(willAsk, RadioAsk.PhoneWillAsk(askedBefore, mayAskAgain));
}
