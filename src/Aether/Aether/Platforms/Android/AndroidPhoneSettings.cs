// SPDX-License-Identifier: MIT
#if ANDROID
using Aether.Shared.Services;
using Android.Content;
using AndroidApp = Android.App.Application;
using AndroidSettings = Android.Provider.Settings;

namespace Aether.Platforms.Android;

/// <summary>
/// Android's <see cref="IPhoneSettings"/>: the system's own pages for Bluetooth, Location, NFC and the networks, the
/// same pages AetherNetService opened for itself before it lost its screen. Each falls back to the phone's Settings
/// where a phone has no such page. Nothing is switched from here: the person does that, on the phone's page.
/// </summary>
public sealed class AndroidPhoneSettings : IPhoneSettings
{
    private long _backAt;

    public AndroidPhoneSettings()
    {
        Microsoft.Maui.ApplicationModel.Platform.ActivityStateChanged += (_, e) =>
        {
            if (e.State is not Microsoft.Maui.ApplicationModel.ActivityState.Resumed) return;

            // Resumed arrives twice for one return to the screen (see AndroidTapShare); asking again once is enough.
            var now = Environment.TickCount64;
            if (now - _backAt < 500) return;
            _backAt = now;
            Back?.Invoke();
        };
    }

    /// <inheritdoc />
    public event Action? Back;

    /// <inheritdoc />
    public bool Open(PhoneSetting setting) => setting switch
    {
        PhoneSetting.Bluetooth => Start(AndroidSettings.ActionBluetoothSettings),
        PhoneSetting.Location => Start(AndroidSettings.ActionLocationSourceSettings),
        PhoneSetting.Nfc => Start(AndroidSettings.ActionNfcSettings),
        PhoneSetting.Internet => Start(AndroidSettings.ActionWirelessSettings),
        _ => false,
    } || Start(AndroidSettings.ActionSettings);

    private static bool Start(string action)
    {
        try
        {
            var intent = new Intent(action);
            intent.AddFlags(ActivityFlags.NewTask);
            AndroidApp.Context.StartActivity(intent);
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
