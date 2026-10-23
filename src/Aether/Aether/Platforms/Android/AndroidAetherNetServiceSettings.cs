// SPDX-License-Identifier: MIT
#if ANDROID
using Android.Content;
using AetherNetNodeService.Client;

namespace AetherNetNodeService.Android;

/// <summary>
/// The Android <see cref="IAetherNetServiceSettings"/>: the phone's own prompts for AetherNetService's radios, put up
/// over this app by AetherNetService's see-through AskActivity, and Android's own "App info" page for AetherNetService,
/// where Permissions is one tap away. Both are the phone's screens, not ours, and nothing is granted without the person
/// tapping Allow on them.
/// </summary>
public sealed class AndroidAetherNetServiceSettings : IAetherNetServiceSettings
{
    /// <summary>AetherNetService's see-through screen that puts up the phone's prompts, and what it is told.</summary>
    private const string AskActivity = "com.bhengubv.aethernetservice.AskActivity";
    private const string RadioExtra = "radio";
    private const int AskRequest = 0x4153;

    private readonly Context _context;
    private readonly string _servicePackage;
    private readonly Func<global::Android.App.Activity?>? _screen;

    /// <param name="context">An application context.</param>
    /// <param name="servicePackage">AetherNetService's package name.</param>
    /// <param name="screen">The screen the person is on, which the phone's prompts come up over.</param>
    public AndroidAetherNetServiceSettings(Context context, string servicePackage, Func<global::Android.App.Activity?>? screen = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _servicePackage = string.IsNullOrWhiteSpace(servicePackage)
            ? throw new ArgumentException("AetherNetService's package name is needed.", nameof(servicePackage))
            : servicePackage;
        _screen = screen;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Started for a result from the screen the person is on, so the prompt comes up over it, they never leave it, and
    /// the phone tells AetherNetService which app asked — it answers only one signed like itself. An AetherNetService
    /// from before 1.6 has no such screen: then this is false and the caller opens App info.
    /// </remarks>
    public bool Ask(string radio)
    {
        if (_screen?.Invoke() is not { } screen) return false;
        try
        {
            var intent = new Intent().SetComponent(new ComponentName(_servicePackage, AskActivity));
            intent.PutExtra(RadioExtra, radio);
            screen.StartActivityForResult(intent, AskRequest);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;   // an AetherNetService from before it could ask
        }
        catch (Java.Lang.SecurityException)
        {
            return false;
        }
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

    // Huawei's own page first, where the phone lets another app open it. On EMUI 10 it does not — the page needs a
    // signature|privileged permission (P30, 2026-10-03) — so then the phone's Battery page, which links to it.
    private bool OpenAppLaunch()
    {
        foreach (var component in AppLaunchPages)
        {
            if (Start(new Intent().SetComponent(component))) return true;
        }
        return Start(new Intent(Intent.ActionPowerUsageSummary));
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
