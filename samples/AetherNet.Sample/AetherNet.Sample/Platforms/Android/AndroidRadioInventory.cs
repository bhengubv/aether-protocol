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
///
/// <para>
/// This list must match what <c>AndroidRadioMesh</c> registers. It drifted: Wi-Fi was added as a
/// transport and never appeared here, so the launch screen read "3 of 6 radios" while the radio
/// actually carrying the traffic in front of the person was not on the list at all.
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
        var wifi = Has("android.hardware.wifi");
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
                Carries: wifiDirect),

            // The obvious one, and the one that was missing from this list entirely. Two phones on the
            // same network — a house, a café, a hotspot — can already reach each other, and leaving it
            // out meant the screen did not name the radio that was doing the work.
            new("Wi-Fi", wifi,
                wifi
                    ? "carries over a network you are both already on, at whatever that network can do"
                    : "no Wi-Fi radio in this device",
                Carries: wifi),

            new("Mobile data", telephony,
                telephony
                    ? "reaches people who are nowhere near you, through a phone in your Circle"
                    : "no cellular radio in this device",
                Carries: telephony),

            // Back in the mesh, so the old line here — "too slow to carry a call, so nothing is sent
            // over it" — is no longer true. It was taken out for carrying traffic Wi-Fi Direct should
            // have had, which was a routing fault, not a Bluetooth fault, and the routing is fixed.
            new("Bluetooth", bluetooth,
                bluetooth
                    ? "reaches when there is no Wi-Fi at all — slow, so it carries only when it is the best there is"
                    : "no Bluetooth radio in this device",
                Carries: bluetooth),

            new("NFC", nfc,
                nfc ? "adds someone by touching two phones together" : "no NFC in this device"),

            // An open Wi-Fi Alliance standard with a standard Android API, and no phone tested here
            // has the hardware. Worth listing precisely because it is the one to look for next.
            new("Wi-Fi Aware", aware,
                aware
                    ? "finds people and carries traffic without forming a group at all"
                    : "not in this phone — it is the radio worth having on your next one",
                Carries: aware),

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
