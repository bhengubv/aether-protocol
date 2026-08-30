// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Sample.Shared.Services;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Hands the choice to MAUI, which is the one lever everything else already listens to.
/// </summary>
/// <remarks>
/// Setting <c>UserAppTheme</c> makes <c>RequestedTheme</c> return it and raises
/// <c>RequestedThemeChanged</c> — so <c>MainPage</c> repaints the ground behind the WebView without
/// knowing this exists, and anything added later that asks MAUI for the theme gets the answer the
/// person actually chose rather than the one the phone is set to.
/// </remarks>
public sealed class AndroidAppTheme : IAppTheme
{
    /// <summary>Where the choice is mirrored for code that runs before the database is open.</summary>
    public const string Key = "appearance.theme";

    public void Apply(string theme)
    {
        // Mirrored into Preferences as well as the database. The ground behind the WebView is painted
        // before Blazor exists, let alone SQLite, and opening the database on that path is the stall
        // the warm-up screen was built to remove.
        Microsoft.Maui.Storage.Preferences.Set(Key, theme);

        if (Application.Current is not { } app) return;

        app.UserAppTheme = theme switch
        {
            "light" => AppTheme.Light,
            "dark" => AppTheme.Dark,
            _ => AppTheme.Unspecified,   // follow the phone
        };
    }
}
#endif
