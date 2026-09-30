// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Content;

namespace AetherNet.Transport.Android;

/// <summary>
/// Whether Android lets this app look for phones nearby over each kind of radio — asked before a radio touches its
/// stack, never learned from the stack refusing.
/// </summary>
/// <remarks>
/// <para>
/// AetherNetService has no screen, so it can never put up Android's "Allow?" prompt: these are granted once, by the
/// person, in Settings → Apps → AetherNetService → Permissions. Until then a radio reports what it needs — the same
/// words on every radio — and stays off.
/// </para>
/// <para>
/// Asking the stack and catching its refusal is not the same thing. Bluetooth refused a GATT server with a
/// SecurityException most of the time and, now and then on a freshly started service, crashed the whole process
/// with a segmentation fault instead (Pixel, Android 16, 2026-09-30) — taking every other radio and every held
/// message with it.
/// </para>
/// </remarks>
internal static class RadioPermissions
{
    /// <summary>What a radio says while the permission is missing.</summary>
    public const string Missing = "needs permission to find phones nearby";

    /// <summary>
    /// Finding phones over Wi-Fi — Direct or Aware. Android 13 added "Nearby devices" precisely so that finding a
    /// phone next to you stops meaning "may track where you are"; before it, the only way to ask was fine location.
    /// </summary>
    public static bool NearbyWifi => Granted(
        OperatingSystem.IsAndroidVersionAtLeast(33)
            ? global::Android.Manifest.Permission.NearbyWifiDevices
            : global::Android.Manifest.Permission.AccessFineLocation);

    /// <summary>
    /// Finding and connecting to phones over Bluetooth. Android 12 split this out as "Nearby devices" (scan and
    /// connect; advertising rides the same grant); before it, a scan needed fine location and returned nothing
    /// without it.
    /// </summary>
    public static bool Bluetooth => OperatingSystem.IsAndroidVersionAtLeast(31)
        ? Granted(global::Android.Manifest.Permission.BluetoothScan) && Granted(global::Android.Manifest.Permission.BluetoothConnect)
        : Granted(global::Android.Manifest.Permission.AccessFineLocation);

    /// <summary>
    /// Whether Location is switched on. On older Android, scanning for phones returns nothing at all while it is
    /// off — not an error, just silence, which is the hardest kind of failure to find.
    /// </summary>
    public static bool LocationServicesOn =>
        global::Android.App.Application.Context.GetSystemService(Context.LocationService)
            is global::Android.Locations.LocationManager m &&
        (m.IsProviderEnabled(global::Android.Locations.LocationManager.GpsProvider) ||
         m.IsProviderEnabled(global::Android.Locations.LocationManager.NetworkProvider));

    private static bool Granted(string permission) =>
        AndroidX.Core.Content.ContextCompat.CheckSelfPermission(global::Android.App.Application.Context, permission)
        == global::Android.Content.PM.Permission.Granted;
}
#endif
