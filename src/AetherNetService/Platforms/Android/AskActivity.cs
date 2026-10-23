// SPDX-License-Identifier: MIT
using Android.App;
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.Nfc;
using Android.OS;
using AndroidSettings = Android.Provider.Settings;
using AndroidUri = Android.Net.Uri;
using Log = Android.Util.Log;

namespace AetherNetService;

/// <summary>
/// The phone's own prompt, put up over the app the person is using, for one of the radios, for Aware or Quiet help, or
/// for one of AetherNetService's permissions as an app's settings list them (<see cref="RadioAsk"/>): Android's
/// "Allow?" for a permission still missing, its "turn on Bluetooth?", or its panel for NFC, Wi-Fi or the internet.
/// Nothing of ours is drawn — the screen is see-through, and it closes as soon as the phone's prompt does.
/// </summary>
/// <remarks>
/// <para>
/// It has to be AetherNetService's own: the phone keeps a permission per app, and puts up its prompt only for the app
/// whose screen asks. With no screen at all, Allow on Aether's welcome did nothing (1.4, 1.5), and then opened
/// AetherNetService's App info for the person to find the switch in (1.6 as first built, 2026-10-23).
/// </para>
/// <para>
/// Only an app signed like this one is answered — Aether, which starts this for a result so the phone says who asked.
/// Two things still leave the app, because the phone has no prompt for them: a permission the person told it never to
/// ask about again (it would answer no by itself, so its App info page is opened, the one place left), and Location
/// switched off (no prompt without Google's services, so its page is opened). Location "all the time" is asked for
/// like any other, and from Android 11 the phone answers by taking the person to its own page for that one choice.
/// </para>
/// </remarks>
[Activity(
    Name = "com.bhengubv.aethernetservice.AskActivity",
    Exported = true,
    ExcludeFromRecents = true,
    Theme = "@android:style/Theme.Translucent.NoTitleBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize
        | ConfigChanges.ScreenLayout | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.UiMode
        | ConfigChanges.Density)]
public sealed class AskActivity : Activity
{
    /// <summary>
    /// What to ask for: a radio, by the name AetherNetService gives it ("BLE", "Wi-Fi Direct", "NFC", "Internet"),
    /// "Aware", "Quiet help", or a permission by the name the phone gives it ("Location", "Nearby devices"). Called
    /// "radio" because radios came first, and an Aether that asks for one still says it so.
    /// </summary>
    public const string RadioExtra = "radio";

    private const int Prompt = 1;
    private const string Tag = "AetherNetService";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Made again while the phone's prompt was up (the person turned the phone): the answer still comes here.
        if (savedInstanceState is not null) return;

        var caller = CallingPackage;
        if (caller is null || PackageManager!.CheckSignatures(caller, PackageName!) != SignatureResult.Match)
        {
            Log.Warn(Tag, $"ask: not answered — {caller ?? "an app that did not say who it is"} is not signed like AetherNetService");
            Finish();
            return;
        }

        var radio = Intent?.GetStringExtra(RadioExtra);
        if (!AskPermission(radio) && !AskSwitch(radio))
        {
            Log.Info(Tag, $"ask: {radio} — nothing on this phone to ask for");
            Finish();
        }
    }

    // ── A permission ─────────────────────────────────────────────────────────────

    private bool AskPermission(string? radio)
    {
        var missing = RadioAsk.Missing(radio, (int)Build.VERSION.SdkInt, p => CheckSelfPermission(p) == Permission.Granted);
        if (missing.Length == 0) return false;

        var asked = GetSharedPreferences("ask", FileCreationMode.Private)!;
        var askedBefore = missing.Any(p => asked.GetBoolean(p, false));
        if (!RadioAsk.PhoneWillAsk(askedBefore, missing.Any(ShouldShowRequestPermissionRationale)))
        {
            Log.Info(Tag, $"ask: {radio} — the phone will not ask about {string.Join(", ", missing)} again; its App info instead");
            return Open(new Intent(AndroidSettings.ActionApplicationDetailsSettings, AndroidUri.FromParts("package", PackageName, null)));
        }

        var edit = asked.Edit()!;
        foreach (var permission in missing) edit.PutBoolean(permission, true);
        edit.Apply();

        Log.Info(Tag, $"ask: {radio} — the phone's prompt for {string.Join(", ", missing)}");
        RequestPermissions(missing, Prompt);
        return true;
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        Log.Info(Tag, $"ask: answered {string.Join(", ", permissions.Zip(grantResults, (p, g) => $"{p} {g}"))}");
        Finish();
    }

    // ── A switch ─────────────────────────────────────────────────────────────────

    private bool AskSwitch(string? radio)
    {
        var phone = new PhoneNow((int)Build.VERSION.SdkInt, BluetoothOn, LocationOn, WifiOn, NfcOn, HasNetwork);
        if (RadioAsk.SwitchFor(radio, phone) is not { } which) return false;

        Log.Info(Tag, $"ask: {radio} — the phone's {which} switch ({phone})");
        return which switch
        {
            // Its own "turn on Bluetooth?"; its page where a phone will not put that up.
            PhoneSwitch.Bluetooth => Open(new Intent(BluetoothAdapter.ActionRequestEnable))
                || Open(new Intent(AndroidSettings.ActionBluetoothSettings)),
            PhoneSwitch.Location => Open(new Intent(AndroidSettings.ActionLocationSourceSettings)),
            PhoneSwitch.Wifi => Panel("android.settings.panel.action.WIFI")
                || Open(new Intent(AndroidSettings.ActionWifiSettings)),
            PhoneSwitch.Nfc => Panel("android.settings.panel.action.NFC")
                || Open(new Intent(AndroidSettings.ActionNfcSettings)),
            PhoneSwitch.Internet => Panel("android.settings.panel.action.INTERNET_CONNECTIVITY")
                || Open(new Intent(AndroidSettings.ActionWirelessSettings)),
            _ => false,
        };
    }

    /// <summary>One of the phone's panels (Settings.Panel), which sit over the app; Android 10 and later.</summary>
    private bool Panel(string action) => OperatingSystem.IsAndroidVersionAtLeast(29) && Open(new Intent(action));

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        Finish();
    }

    /// <summary>Started for a result, so this closes the moment the person is back from it.</summary>
    private bool Open(Intent intent)
    {
        try
        {
            StartActivityForResult(intent, Prompt);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            return false;   // not on this phone — the caller tries the next
        }
        catch (Java.Lang.SecurityException)
        {
            return false;   // there, but not ours to open
        }
    }

    // ── The phone's switches, read as the radios read them ───────────────────────

    private bool BluetoothOn => (GetSystemService(BluetoothService) as BluetoothManager)?.Adapter?.IsEnabled == true;

    private bool LocationOn => GetSystemService(LocationService) is global::Android.Locations.LocationManager m
        && (m.IsProviderEnabled(global::Android.Locations.LocationManager.GpsProvider)
            || m.IsProviderEnabled(global::Android.Locations.LocationManager.NetworkProvider));

    private bool WifiOn => (GetSystemService(WifiService) as global::Android.Net.Wifi.WifiManager)?.IsWifiEnabled == true;

    private bool NfcOn => NfcAdapter.GetDefaultAdapter(this)?.IsEnabled == true;

    private bool HasNetwork => GetSystemService(ConnectivityService) is ConnectivityManager c
        && c.GetNetworkCapabilities(c.ActiveNetwork)?.HasCapability(NetCapability.Internet) == true;
}
