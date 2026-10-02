// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNetNodeService;   // ServicePermission, PermissionPage

namespace AetherNetService;

/// <summary>
/// The permissions the phone keeps for AetherNetService — the ones a person allows or refuses on the phone's own pages
/// — and whether each is allowed right now. A connected app shows this list in its own settings, because
/// AetherNetService has no screen to show it on.
/// </summary>
/// <remarks>
/// <para>
/// Named as the phone names them, so the words in the app are the words on the phone's page. Which radio permissions
/// there are follows the Android version, the same rule the radios follow (RadioPermissions): "Nearby devices" from 13;
/// on 12 Bluetooth moved there but Wi-Fi still asks Location; before 12 it is all Location. Notifications from 13.
/// </para>
/// <para>
/// Two more keep it running. A phone short of memory stops even a foreground service: the P30 stopped this one four
/// times in five minutes (2026-10-02), and once no app was bound to it nothing started it again. "Battery" lifts the
/// phone's battery limits; on Huawei phones "App launch" is what lets the phone start it again — and the phone does not
/// say how that one is set, so it is reported as not known.
/// </para>
/// </remarks>
internal static class ServicePermissions
{
    public static IReadOnlyList<ServicePermission> Now()
    {
        var list = new List<ServicePermission>(4);

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            list.Add(new("Nearby devices", Granted(global::Android.Manifest.Permission.NearbyWifiDevices) && Bluetooth,
                "find phones near you, over Wi-Fi and Bluetooth"));
            list.Add(new("Notifications", Granted(global::Android.Manifest.Permission.PostNotifications),
                "show that it is keeping you reachable"));
        }
        else if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            list.Add(new("Nearby devices", Bluetooth, "find phones near you over Bluetooth"));
            list.Add(new("Location", Granted(global::Android.Manifest.Permission.AccessFineLocation),
                "find phones near you over Wi-Fi"));
        }
        else
        {
            list.Add(new("Location", Granted(global::Android.Manifest.Permission.AccessFineLocation),
                "find phones near you, over Wi-Fi and Bluetooth"));
        }

        list.Add(new("Battery", Unrestricted, "keep running in the background, free of the phone's battery limits")
        {
            Page = PermissionPage.Battery,
        });

        if (Huawei)
        {
            list.Add(new("App launch", false, "start again after the phone stops it")
            {
                Page = PermissionPage.AppLaunch,
                Known = false,
            });
        }

        return list;
    }

    // Scan and connect: what the radios check (RadioPermissions.Bluetooth). Advertising rides the same grant.
    private static bool Bluetooth =>
        Granted(global::Android.Manifest.Permission.BluetoothScan) && Granted(global::Android.Manifest.Permission.BluetoothConnect);

    /// <summary>Free of battery optimisation, and not restricted in the background by the person.</summary>
    private static bool Unrestricted
    {
        get
        {
            var context = global::Android.App.Application.Context;
            var exempt = context.GetSystemService(global::Android.Content.Context.PowerService) is global::Android.OS.PowerManager power
                && power.IsIgnoringBatteryOptimizations(context.PackageName);
            var restricted = OperatingSystem.IsAndroidVersionAtLeast(28)
                && context.GetSystemService(global::Android.Content.Context.ActivityService) is global::Android.App.ActivityManager am
                && am.IsBackgroundRestricted;
            return exempt && !restricted;
        }
    }

    /// <summary>Huawei, and Honor phones built on the same system — where App launch decides what may start again.</summary>
    private static bool Huawei =>
        string.Equals(global::Android.OS.Build.Manufacturer, "HUAWEI", StringComparison.OrdinalIgnoreCase)
        || string.Equals(global::Android.OS.Build.Manufacturer, "HONOR", StringComparison.OrdinalIgnoreCase);

    private static bool Granted(string permission) =>
        global::Android.App.Application.Context.CheckSelfPermission(permission) == global::Android.Content.PM.Permission.Granted;
}
#endif
