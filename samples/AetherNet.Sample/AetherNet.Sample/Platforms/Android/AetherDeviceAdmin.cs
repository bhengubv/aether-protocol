// SPDX-License-Identifier: MIT
#if ANDROID
using Android.App.Admin;
using Android.Content;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// The slot provisioning needs filled, holding nothing.
///
/// <para>
/// Android will not install an app by itself — join a network, fetch a package, verify its hash and
/// install it, with nobody reading an address — unless that package contains something to hand
/// authority to. This is that something. It exists because the door will not open without it.
/// </para>
///
/// <para>
/// <b>It overrides nothing on purpose.</b> A device admin can wipe a phone, force a lock, expire
/// passwords, disable the camera and read a great deal about the device. Every one of those is a
/// method left unimplemented here and a policy left out of <c>device_admin.xml</c>, so the honest
/// answer to "what can this thing do to my phone" is nothing, and it is checkable rather than
/// promised.
/// </para>
///
/// <para>
/// The authority that <i>is</i> taken is decided in <see cref="ProvisioningModeActivity"/>, which
/// takes the smallest one the system will settle for and walks away from the install rather than
/// accept ownership of somebody's handset.
/// </para>
/// </summary>
[global::Android.Content.BroadcastReceiver(
    Name = "com.bhengubv.aethernet.AetherDeviceAdmin",
    Permission = "android.permission.BIND_DEVICE_ADMIN",
    Exported = true)]
[global::Android.App.MetaData("android.app.device_admin", Resource = "@xml/device_admin")]
[global::Android.App.IntentFilter(["android.app.action.DEVICE_ADMIN_ENABLED"])]
public sealed class AetherDeviceAdmin : DeviceAdminReceiver
{
    /// <summary>Where the platform is told to find this, when a tap has to name it.</summary>
    public const string Component = "com.bhengubv.aethernet/com.bhengubv.aethernet.AetherDeviceAdmin";

    public override void OnEnabled(Context context, Intent intent) =>
        global::Android.Util.Log.Info("AetherProv", "admin enabled — holding no policies");

    public override void OnDisabled(Context context, Intent intent) =>
        global::Android.Util.Log.Info("AetherProv", "admin disabled");

    /// <summary>
    /// A work profile has been created and this app owns it — and nothing outside it.
    /// </summary>
    public override void OnProfileProvisioningComplete(Context context, Intent intent) =>
        global::Android.Util.Log.Info("AetherProv", "● provisioning complete — Aether is installed");
}
#endif
