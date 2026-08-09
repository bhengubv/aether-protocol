// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Android.Locations;
using Android.Nfc;
using AetherNet.Sample.Shared.Services;
using AndroidX.Core.Content;
using AndroidApp = Android.App.Application;
using AndroidUri = Android.Net.Uri;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Real radio readiness on Android. Each check asks the platform what is actually true — permission
/// held AND the radio switched on AND the capability present — because every one of those has bitten
/// us on a real phone:
///
/// <list type="bullet">
/// <item>Android 12+ needs <c>BLUETOOTH_ADVERTISE</c>/<c>CONNECT</c>/<c>SCAN</c> at runtime; without
/// them the GATT server fails <b>silently</b> and the app looks fine while being invisible.</item>
/// <item>Wi-Fi Direct discovery needs <c>NEARBY_WIFI_DEVICES</c> (or fine location ≤ API 32) <b>and</b>
/// system Location Services actually switched on — not just the permission.</item>
/// <item>The P30 Lite cannot BLE-advertise at all, so it can find others but can never be found. That
/// is <see cref="RadioState.Partial"/>, not a failure, and the wizard must say which way it works.</item>
/// </list>
/// </summary>
public sealed class AndroidRadioSetup : IRadioSetup
{
    public const string WifiDirect = "Wi-Fi Direct";
    public const string Ble = "BLE";
    public const string Nfc = "NFC";
    public const string Internet = "Internet";

    public bool IsPhone => true;

    public Task<IReadOnlyList<RadioStatus>> CheckAsync() =>
        Task.FromResult<IReadOnlyList<RadioStatus>>(new[]
        {
            CheckBle(),
            CheckWifiDirect(),
            CheckInternet(),
            CheckNfc(),
        });

    public async Task<RadioStatus> RequestAsync(string radioName)
    {
        switch (radioName)
        {
            case Ble:
                await RequestPermissionsAsync(BluetoothPermissions()).ConfigureAwait(false);
                if (BluetoothAdapter.DefaultAdapter is { IsEnabled: false })
                    OpenSettings(global::Android.Provider.Settings.ActionBluetoothSettings);
                return CheckBle();

            case WifiDirect:
                await RequestPermissionsAsync(WifiDirectPermissions()).ConfigureAwait(false);
                if (!LocationServicesOn())
                    OpenSettings(global::Android.Provider.Settings.ActionLocationSourceSettings);
                return CheckWifiDirect();

            case Nfc:
                if (NfcAdapter.GetDefaultAdapter(AndroidApp.Context) is { IsEnabled: false })
                    OpenSettings(global::Android.Provider.Settings.ActionNfcSettings);
                return CheckNfc();

            case Internet:
                OpenSettings(global::Android.Provider.Settings.ActionWirelessSettings);
                return CheckInternet();

            default:
                return new RadioStatus(radioName, RadioState.Unsupported, "Unknown radio.", null, Required: false);
        }
    }

    // ── Per-radio truth ─────────────────────────────────────────────────────────

    private static RadioStatus CheckBle()
    {
        var context = AndroidApp.Context;
        if (!context.PackageManager!.HasSystemFeature(PackageManager.FeatureBluetoothLe))
            return new(Ble, RadioState.Unsupported, "This phone has no Bluetooth LE.", null, Required: false);

        var missing = BluetoothPermissions().Where(p => !Granted(p)).ToArray();
        if (missing.Length > 0)
            return new(Ble, RadioState.NeedsPermission,
                "Aether needs Bluetooth to find phones next to you.", "Allow Bluetooth", Required: true);

        var adapter = BluetoothAdapter.DefaultAdapter;
        if (adapter is null || !adapter.IsEnabled)
            return new(Ble, RadioState.NeedsSystemToggle, "Bluetooth is switched off.", "Turn on Bluetooth", Required: true);

        // The asymmetry that decides how two phones find each other: a phone that cannot advertise can
        // only ever be the one doing the looking.
        return adapter.IsMultipleAdvertisementSupported
            ? new(Ble, RadioState.Ready, "Ready — this phone can find others and be found.", null, Required: true)
            : new(Ble, RadioState.Partial, "Ready — this phone can find others, but can't be found. Tap Connect on the other phone.", null, Required: true);
    }

    private static RadioStatus CheckWifiDirect()
    {
        var context = AndroidApp.Context;
        if (!context.PackageManager!.HasSystemFeature(PackageManager.FeatureWifiDirect))
            return new(WifiDirect, RadioState.Unsupported, "This phone has no Wi-Fi Direct.", null, Required: false);

        var missing = WifiDirectPermissions().Where(p => !Granted(p)).ToArray();
        if (missing.Length > 0)
            return new(WifiDirect, RadioState.NeedsPermission,
                "Wi-Fi Direct sends bigger things — photos, video — straight to a nearby phone.",
                "Allow nearby devices", Required: false);

        // Below API 33 the discovery stack silently returns nothing unless Location is actually on.
        if (global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.Tiramisu && !LocationServicesOn())
            return new(WifiDirect, RadioState.NeedsSystemToggle,
                "Android needs Location switched on to discover nearby phones over Wi-Fi.",
                "Turn on Location", Required: false);

        return new(WifiDirect, RadioState.Ready, "Ready — used for photos, video and big files.", null, Required: false);
    }

    private static RadioStatus CheckInternet()
    {
        // Internet is one more AetherNet transport, and a phone that has it can carry traffic for
        // phones that don't. Never required: the whole point is that Aether works without it.
        var connectivity = Connectivity.Current.NetworkAccess;
        return connectivity == NetworkAccess.Internet
            ? new(Internet, RadioState.Ready, "Connected — you can also reach people further away, and share your connection.", null, Required: false)
            : new(Internet, RadioState.NeedsSystemToggle, "No internet. Aether still works with the phones around you.", "Open network settings", Required: false);
    }

    private static RadioStatus CheckNfc()
    {
        var adapter = NfcAdapter.GetDefaultAdapter(AndroidApp.Context);
        if (adapter is null)
            return new(Nfc, RadioState.Unsupported, "This phone has no NFC.", null, Required: false);
        return adapter.IsEnabled
            ? new(Nfc, RadioState.Ready, "Ready — tap two phones together to swap tags.", null, Required: false)
            : new(Nfc, RadioState.NeedsSystemToggle, "NFC is switched off.", "Turn on NFC", Required: false);
    }

    // ── Platform plumbing ───────────────────────────────────────────────────────

    private static string[] BluetoothPermissions() =>
        global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.S
            ? new[]
            {
                global::Android.Manifest.Permission.BluetoothScan,
                global::Android.Manifest.Permission.BluetoothConnect,
                global::Android.Manifest.Permission.BluetoothAdvertise,
            }
            : new[]
            {
                global::Android.Manifest.Permission.Bluetooth,
                global::Android.Manifest.Permission.BluetoothAdmin,
                global::Android.Manifest.Permission.AccessFineLocation,
            };

    private static string[] WifiDirectPermissions() =>
        global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.Tiramisu
            ? new[] { global::Android.Manifest.Permission.NearbyWifiDevices }
            : new[] { global::Android.Manifest.Permission.AccessFineLocation };

    private static bool Granted(string permission) =>
        ContextCompat.CheckSelfPermission(AndroidApp.Context, permission) == Permission.Granted;

    private static async Task RequestPermissionsAsync(string[] permissions)
    {
        var missing = permissions.Where(p => !Granted(p)).ToArray();
        if (missing.Length == 0) return;

        if (global::Android.App.Application.Context is not null &&
            Platform.CurrentActivity is { } activity)
        {
            AndroidX.Core.App.ActivityCompat.RequestPermissions(activity, missing, 9701);
            // The dialog is modal to the user but not to us; give it room to be answered before the
            // caller re-checks. The wizard re-checks on resume anyway, so this is a nudge, not a wait.
            await Task.Delay(400).ConfigureAwait(false);
        }
    }

    private static bool LocationServicesOn()
    {
        if (AndroidApp.Context.GetSystemService(Context.LocationService) is not LocationManager manager)
            return false;
        return manager.IsProviderEnabled(LocationManager.GpsProvider)
            || manager.IsProviderEnabled(LocationManager.NetworkProvider);
    }

    private static void OpenSettings(string action)
    {
        var intent = new Intent(action);
        intent.AddFlags(ActivityFlags.NewTask);
        AndroidApp.Context.StartActivity(intent);
    }
}
#endif
