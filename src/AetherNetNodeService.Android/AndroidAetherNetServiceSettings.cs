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
    public bool Open()
    {
        try
        {
            var intent = new Intent(
                global::Android.Provider.Settings.ActionApplicationDetailsSettings,
                global::Android.Net.Uri.FromParts("package", _servicePackage, null));
            intent.AddFlags(ActivityFlags.NewTask);
            _context.StartActivity(intent);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;   // a settings app with no App info page — nothing to open
        }
    }
}
#endif
