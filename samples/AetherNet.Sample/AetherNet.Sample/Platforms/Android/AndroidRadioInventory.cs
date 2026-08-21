// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Sample.Shared.Services;
using Android.Content;
using Android.Content.PM;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Asks Android what radios this phone actually contains.
///
/// <para>
/// Every answer comes from the package manager's own feature list — the same list the Play Store uses
/// to decide whether an app can run on a device. Nothing here is inferred from a model name or a
/// version number, so a phone that says it has Wi-Fi Aware has it, and one that does not, does not.
/// </para>
/// </summary>
public sealed class AndroidRadioInventory : IRadioInventory
{
    private static PackageManager? Packages => global::Android.App.Application.Context.PackageManager;

    private static bool Has(string feature)
    {
        try { return Packages?.HasSystemFeature(feature) == true; }
        catch { return false; }
    }

    /// <inheritdoc />
    public IReadOnlyList<RadioCapability> Survey()
    {
        var wifiDirect = Has(PackageManager.FeatureWifiDirect);
        var bluetooth = Has(PackageManager.FeatureBluetoothLe) || Has(PackageManager.FeatureBluetooth);
        var aware = Has("android.hardware.wifi.aware");
        var nfc = Has(PackageManager.FeatureNfc);
        var telephony = Has(PackageManager.FeatureTelephony);

        return
        [
            new("Wi-Fi Direct", wifiDirect,
                wifiDirect
                    ? "carries calls, video and files between phones with no network at all"
                    : "this phone cannot do device-to-device Wi-Fi",
                Carrying: wifiDirect),

            new("Mobile data", telephony,
                telephony
                    ? "reaches people who are nowhere near you, through a phone in your Circle"
                    : "no cellular radio in this device",
                Carrying: telephony),

            // Present on every phone here and deliberately not used. Measured at 11 kbps in one
            // direction, which cannot carry a call at any codec — and while it was registered it kept
            // taking traffic that Wi-Fi Direct could have carried properly.
            new("Bluetooth", bluetooth,
                bluetooth
                    ? "present, but 11 kbps one way — too slow to carry a call, so nothing is sent over it"
                    : "no Bluetooth radio in this device"),

            new("NFC", nfc,
                nfc ? "adds someone by touching two phones together" : "no NFC in this device"),

            // An open Wi-Fi Alliance standard with a standard Android API, and no phone tested here
            // has the hardware. Worth listing precisely because it is the one to look for next.
            new("Wi-Fi Aware", aware,
                aware
                    ? "finds people and carries traffic without forming a group at all"
                    : "not in this phone — it is the radio worth having on your next one",
                Carrying: aware),

            // Huawei silicon driven by HarmonyOS APIs. Android's stack cannot speak it at all, so on
            // an Android phone there is no way even to detect it.
            new("NearLink", false,
                "needs NearLink hardware and HarmonyOS — Android cannot reach it"),

            new("LoRa", false,
                "needs a USB module — kilometres of range at a few hundred bits per second"),
        ];
    }
}
#endif
