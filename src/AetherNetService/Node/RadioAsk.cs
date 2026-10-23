// SPDX-License-Identifier: MIT

namespace AetherNetService;

/// <summary>
/// What the phone is asked for when somebody taps a button in an app: for one of the radios, for Aware or Quiet help,
/// or for one of AetherNetService's permissions as an app's settings list them — the permission still missing, or the
/// phone's switch it is waiting on. Worked out here, with no phone in it, so it is tested off one.
/// </summary>
/// <remarks>
/// <para>
/// The permissions are the ones the radios check before they touch their stack (RadioPermissions, in
/// AetherNet.Transport.Android): from Android 12 Bluetooth needs "Nearby devices" — scan, connect and advertise, one
/// prompt — and from 13 Wi-Fi does too; before that, both need Location. Location is always asked for precise and
/// approximate together: from Android 12 the phone ignores a request for precise alone, and shows nothing at all.
/// </para>
/// <para>
/// The switches are the ones the radios say they are waiting on. Bluetooth, NFC, Wi-Fi and the internet each have a
/// prompt or a panel the phone puts up over the app (the panels from Android 10). Location has none on a phone without
/// Google's services, so its page is the one place it is switched on.
/// </para>
/// </remarks>
internal static class RadioAsk
{
    // What an app asks for besides a radio: the two features with a "Put it right", and Settings' rows, by the name
    // the phone gives each permission (ServicePermissions lists them under these names).
    public const string Aware = "Aware";
    public const string QuietHelp = "Quiet help";
    public const string NearbyDevices = "Nearby devices";
    public const string Location = "Location";
    public const string LocationAllTheTime = "Location all the time";
    public const string Notifications = "Notifications";

    // The phone's names for them (Android.Manifest.Permission), spelt out so this file needs no phone.
    private const string FineLocation = "android.permission.ACCESS_FINE_LOCATION";
    private const string CoarseLocation = "android.permission.ACCESS_COARSE_LOCATION";
    private const string BackgroundLocation = "android.permission.ACCESS_BACKGROUND_LOCATION";
    private const string BluetoothScan = "android.permission.BLUETOOTH_SCAN";
    private const string BluetoothConnect = "android.permission.BLUETOOTH_CONNECT";
    private const string BluetoothAdvertise = "android.permission.BLUETOOTH_ADVERTISE";
    private const string NearbyWifiDevices = "android.permission.NEARBY_WIFI_DEVICES";
    private const string PostNotifications = "android.permission.POST_NOTIFICATIONS";

    /// <summary>Precise and approximate, the pair the phone asks about in one prompt.</summary>
    private static string[] WhereYouAre => [FineLocation, CoarseLocation];

    /// <summary>Every permission <paramref name="what"/> needs on Android <paramref name="sdk"/>, allowed yet or not.</summary>
    public static string[] Permissions(string? what, int sdk) => what switch
    {
        "BLE" => sdk >= 31 ? [BluetoothScan, BluetoothConnect, BluetoothAdvertise] : WhereYouAre,
        "Wi-Fi Direct" or "Wi-Fi Aware" => sdk >= 33 ? [NearbyWifiDevices] : WhereYouAre,

        // Aware listens over Bluetooth and Wi-Fi and notes where it heard things, which needs location on every
        // version; its own advert is what Quiet help's nearby half rides on, and that half needs only the advert.
        Aware => sdk >= 31 ? [BluetoothScan, BluetoothAdvertise, .. WhereYouAre] : WhereYouAre,
        QuietHelp => sdk >= 31 ? [BluetoothAdvertise] : WhereYouAre,

        NearbyDevices => sdk >= 33 ? [NearbyWifiDevices, BluetoothScan, BluetoothConnect, BluetoothAdvertise]
            : sdk >= 31 ? [BluetoothScan, BluetoothConnect, BluetoothAdvertise]
            : [],
        Location => WhereYouAre,
        LocationAllTheTime => sdk >= 29 ? [BackgroundLocation] : [],
        Notifications => sdk >= 33 ? [PostNotifications] : [],
        _ => [],
    };

    /// <summary>
    /// What to ask the phone for now: the permissions <paramref name="what"/> needs that are not allowed yet, as the
    /// phone will take them in one prompt.
    /// </summary>
    /// <remarks>
    /// Precise location goes with approximate even when approximate is allowed already: from Android 12 that is how
    /// the phone is asked to make an approximate location precise. And "all the time" is a second step on a location
    /// already allowed — the phone refuses it alone — so without one, the location itself is asked for first.
    /// </remarks>
    public static string[] Missing(string? what, int sdk, Func<string, bool> granted)
    {
        var all = Permissions(what, sdk);
        var missing = all.Where(p => !granted(p)).ToList();
        if (missing.Contains(FineLocation) && all.Contains(CoarseLocation) && !missing.Contains(CoarseLocation))
            missing.Add(CoarseLocation);

        if (missing.Contains(BackgroundLocation) && !granted(FineLocation))
            return WhereYouAre;

        return [.. missing];
    }

    /// <summary>The phone's switch <paramref name="what"/> is waiting on, or null when no switch on this phone helps.</summary>
    public static PhoneSwitch? SwitchFor(string? what, PhoneNow phone) => what switch
    {
        "BLE" or Aware or QuietHelp when !phone.BluetoothOn => PhoneSwitch.Bluetooth,

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
