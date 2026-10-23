// SPDX-License-Identifier: MIT

namespace AetherNetService;

/// <summary>
/// What the phone is asked for one of the radios when somebody taps its button in an app: the permission the radio is
/// missing, or the phone's switch it is waiting on. Worked out here, with no phone in it, so it is tested off one.
/// </summary>
/// <remarks>
/// <para>
/// The permissions are the ones the radios check before they touch their stack (RadioPermissions, in
/// AetherNet.Transport.Android): from Android 12 Bluetooth needs "Nearby devices" — scan, connect and advertise, one
/// prompt — and from 13 Wi-Fi does too; before that, both need Location.
/// </para>
/// <para>
/// The switches are the ones the radios say they are waiting on. Bluetooth, NFC, Wi-Fi and the internet each have a
/// prompt or a panel the phone puts up over the app (the panels from Android 10). Location has none on a phone without
/// Google's services, so its page is the one place it is switched on.
/// </para>
/// </remarks>
internal static class RadioAsk
{
    // The phone's names for them (Android.Manifest.Permission), spelt out so this file needs no phone.
    private const string FineLocation = "android.permission.ACCESS_FINE_LOCATION";
    private const string BluetoothScan = "android.permission.BLUETOOTH_SCAN";
    private const string BluetoothConnect = "android.permission.BLUETOOTH_CONNECT";
    private const string BluetoothAdvertise = "android.permission.BLUETOOTH_ADVERTISE";
    private const string NearbyWifiDevices = "android.permission.NEARBY_WIFI_DEVICES";

    /// <summary>Every permission <paramref name="radio"/> needs on Android <paramref name="sdk"/>, allowed yet or not.</summary>
    public static string[] Permissions(string? radio, int sdk) => radio switch
    {
        "BLE" => sdk >= 31 ? [BluetoothScan, BluetoothConnect, BluetoothAdvertise] : [FineLocation],
        "Wi-Fi Direct" or "Wi-Fi Aware" => sdk >= 33 ? [NearbyWifiDevices] : [FineLocation],
        _ => [],
    };

    /// <summary>The phone's switch <paramref name="radio"/> is waiting on, or null when no switch on this phone helps.</summary>
    public static PhoneSwitch? SwitchFor(string? radio, PhoneNow phone) => radio switch
    {
        "BLE" when !phone.BluetoothOn => PhoneSwitch.Bluetooth,

        // Below Android 12 a Bluetooth scan finds nobody with Location off, and below 13 a Wi-Fi Direct one.
        "BLE" when phone.Sdk < 31 && !phone.LocationOn => PhoneSwitch.Location,
        "Wi-Fi Direct" when phone.Sdk < 33 && !phone.LocationOn => PhoneSwitch.Location,

        // "Turn Wi-Fi and location on" — one at a time, Wi-Fi first.
        "Wi-Fi Aware" when !phone.WifiOn => PhoneSwitch.Wifi,
        "Wi-Fi Aware" when !phone.LocationOn => PhoneSwitch.Location,

        "Wi-Fi" when !phone.WifiOn => PhoneSwitch.Wifi,
        "NFC" when !phone.NfcOn => PhoneSwitch.Nfc,

        // With a network, the internet waits on somebody in the Circle to relay — which no switch here changes.
        "Internet" when !phone.HasNetwork => PhoneSwitch.Internet,

        _ => null,
    };

    /// <summary>
    /// Whether the phone will still put up its prompt for a permission. It always does the first time; after a no, it
    /// does while it says it may ask again (shouldShowRequestPermissionRationale). After "don't ask again" — or a second
    /// no, from Android 11 — it answers no by itself and shows nothing at all.
    /// </summary>
    public static bool PhoneWillAsk(bool askedBefore, bool mayAskAgain) => !askedBefore || mayAskAgain;
}

/// <summary>Where the phone's switches stand right now, read the way the radios read them.</summary>
internal readonly record struct PhoneNow(int Sdk, bool BluetoothOn, bool LocationOn, bool WifiOn, bool NfcOn, bool HasNetwork);

/// <summary>A switch the phone keeps, which a radio can be waiting on.</summary>
internal enum PhoneSwitch
{
    Bluetooth,
    Location,
    Wifi,
    Nfc,

    /// <summary>Wi-Fi and mobile data.</summary>
    Internet,
}
