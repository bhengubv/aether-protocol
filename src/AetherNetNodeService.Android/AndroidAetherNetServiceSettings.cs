// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Content;
using AetherNetNodeService.Client;

namespace AetherNetNodeService.Android;

/// <summary>
/// The Android <see cref="IAetherNetServiceSettings"/>: opens Android's own "App info" page for AetherNetService,
/// where Permissions is one tap away. It is the phone's screen, not one of ours — AetherNetService still has no UI,
/// and nothing is granted without the person tapping Allow there.
/// </summary>
public sealed class AndroidAetherNetServiceSettings : IAetherNetServiceSettings
{
    private readonly Context _context;
    private readonly string _servicePackage;

    /// <param name="context">An application context.</param>
    /// <param name="servicePackage">AetherNetService's package name.</param>
    public AndroidAetherNetServiceSettings(Context context, string servicePackage)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _servicePackage = string.IsNullOrWhiteSpace(servicePackage)
            ? throw new ArgumentException("AetherNetService's package name is needed.", nameof(servicePackage))
            : servicePackage;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The radios ask for "Nearby devices" from Android 13 on. Android 12 moved Bluetooth there but still asks Location
    /// for Wi-Fi, so it takes both; before 12, everything is Location.
    /// </remarks>
    public string PermissionName =>
        OperatingSystem.IsAndroidVersionAtLeast(33) ? "Nearby devices"
        : OperatingSystem.IsAndroidVersionAtLeast(31) ? "Nearby devices and Location"
        : "Location";

    /// <inheritdoc />
    public bool Open() => Start(new Intent(
        global::Android.Provider.Settings.ActionApplicationDetailsSettings,
        global::Android.Net.Uri.FromParts("package", _servicePackage, null)));

    /// <inheritdoc />
    /// <remarks>
    /// The battery prompt is the phone's own "let it always run in the background?" for AetherNetService: the phone
    /// checks only that AetherNetService declares it may ask, not which app raises it. App launch is Huawei's page, at
    /// the names it has had across EMUI versions. Each falls back to the next thing that opens, ending at App info.
    /// </remarks>
    public bool Open(PermissionPage page) => page switch
    {
        PermissionPage.Battery =>
            Start(new Intent(
                global::Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                global::Android.Net.Uri.FromParts("package", _servicePackage, null)))
            || Start(new Intent(global::Android.Provider.Settings.ActionIgnoreBatteryOptimizationSettings))
            || Open(),
        PermissionPage.AppLaunch => OpenAppLaunch() || Open(),
        _ => Open(),
    };

    private bool OpenAppLaunch()
    {
        foreach (var component in AppLaunchPages)
        {
            if (Start(new Intent().SetComponent(component))) return true;
        }
        return false;
    }

    /// <summary>Huawei's App launch page, newest name first (EMUI 9 and later, then 8, then older).</summary>
    private static readonly ComponentName[] AppLaunchPages =
    [
        new("com.huawei.systemmanager", "com.huawei.systemmanager.startupmgr.ui.StartupNormalAppListActivity"),
        new("com.huawei.systemmanager", "com.huawei.systemmanager.appcontrol.activity.StartupAppControlActivity"),
        new("com.huawei.systemmanager", "com.huawei.systemmanager.optimize.process.ProtectActivity"),
    ];

    private bool Start(Intent intent)
    {
        try
        {
            intent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(intent);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;   // this phone has no such page — the caller tries the next
        }
        catch (Java.Lang.SecurityException)
        {
            return false;   // there, but not ours to open
        }
    }
}
#endif
