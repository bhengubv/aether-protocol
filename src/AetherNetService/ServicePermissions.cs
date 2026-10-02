// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNetNodeService;   // ServicePermission

namespace AetherNetService;

/// <summary>
/// The permissions the phone keeps for AetherNetService — the ones a person allows or refuses on its page in the
/// phone's settings — and whether each is allowed right now. A connected app shows this list in its own settings,
/// because AetherNetService has no screen to show it on.
/// </summary>
/// <remarks>
/// Named as the phone names them, so the words in the app are the words on the phone's page. Which ones there are
/// follows the Android version, the same rule the radios follow (RadioPermissions): "Nearby devices" from 13; on 12
/// Bluetooth moved there but Wi-Fi still asks Location; before 12 it is all Location. Notifications from 13.
/// </remarks>
internal static class ServicePermissions
{
    public static IReadOnlyList<ServicePermission> Now()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return
            [
                new("Nearby devices", Granted(global::Android.Manifest.Permission.NearbyWifiDevices) && Bluetooth,
                    "find phones near you, over Wi-Fi and Bluetooth"),
                new("Notifications", Granted(global::Android.Manifest.Permission.PostNotifications),
                    "show that it is keeping you reachable"),
            ];
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            return
            [
                new("Nearby devices", Bluetooth, "find phones near you over Bluetooth"),
                new("Location", Granted(global::Android.Manifest.Permission.AccessFineLocation),
                    "find phones near you over Wi-Fi"),
            ];
        }

        return
        [
            new("Location", Granted(global::Android.Manifest.Permission.AccessFineLocation),
                "find phones near you, over Wi-Fi and Bluetooth"),
        ];
    }

    // Scan and connect: what the radios check (RadioPermissions.Bluetooth). Advertising rides the same grant.
    private static bool Bluetooth =>
        Granted(global::Android.Manifest.Permission.BluetoothScan) && Granted(global::Android.Manifest.Permission.BluetoothConnect);

    private static bool Granted(string permission) =>
        global::Android.App.Application.Context.CheckSelfPermission(permission) == global::Android.Content.PM.Permission.Granted;
}
#endif
