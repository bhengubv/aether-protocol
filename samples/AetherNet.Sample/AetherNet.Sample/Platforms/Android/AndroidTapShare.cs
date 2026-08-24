// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Sample.Shared.Services;
using Android.Nfc;
using Android.Nfc.CardEmulators;
using AndroidApp = Android.App.Application;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Android's answer to <see cref="ITapShare"/>: arm and disarm <see cref="TouchMyBlood"/>.
///
/// <para>
/// Thin on purpose. The tap itself is the emulated tag in <see cref="TouchMyBlood"/>, which Android
/// constructs and drives on its own NFC thread; all this does is decide whether the hardware is there
/// and tell that service what the next tap should carry.
/// </para>
/// </summary>
public sealed class AndroidTapShare : ITapShare
{
    private readonly NfcAdapter? _nfc;
    private readonly bool _hasEmulation;

    public AndroidTapShare()
    {
        _nfc = NfcAdapter.GetDefaultAdapter(AndroidApp.Context);

        // Two different things, and a phone can have the first without the second. Plenty of handsets
        // read tags perfectly and cannot pretend to be one, and on those Touch My Blood cannot work in
        // this direction however healthy the NFC chip looks.
        _hasEmulation = AndroidApp.Context.PackageManager?
            .HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureNfcHostCardEmulation) == true;

        TouchMyBlood.Tapped += () => Tapped?.Invoke();
    }

    /// <inheritdoc />
    public bool IsSupported => _nfc is { IsEnabled: true } && _hasEmulation;

    /// <inheritdoc />
    public string? UnavailableReason =>
        _nfc is null ? "this phone has no NFC"
        : !_hasEmulation ? "this phone can read NFC but cannot be read by another phone"
        : !_nfc.IsEnabled ? "NFC is switched off"
        : null;

    /// <inheritdoc />
    /// <remarks>Switched off is a tap away. Absent hardware is not, and saying otherwise is a lie.</remarks>
    public bool IsFixable => _nfc is { IsEnabled: false } && _hasEmulation;

    /// <inheritdoc />
    public bool IsArmed => TouchMyBlood.IsArmed;

    /// <inheritdoc />
    public void Arm(string invite, string aetherTag)
    {
        if (!IsSupported) return;
        TouchMyBlood.Offer(invite, aetherTag);
        Prefer(true);
        Capture(true);
    }

    /// <inheritdoc />
    public void Disarm()
    {
        TouchMyBlood.Offer(null, null);
        Prefer(false);
        Capture(false);
    }

    /// <summary>
    /// While this screen is open, nothing else on this phone gets a tag it reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A phone does two jobs on a tap and this only used to do one of them.</b> Claiming the
    /// preferred service covers being READ — a peer taps us and gets our card. It does nothing about
    /// this handset READING, which it does by default, and whatever it reads is handed to whichever
    /// app registered the broadest NFC filter.
    /// </para>
    /// <para>
    /// Measured on a P30 during Touch My Blood: touching another phone opened <b>WeChat</b>. It
    /// registers <c>android.nfc.action.TECH_DISCOVERED</c>, which matches on the tag technology rather
    /// than its contents and therefore catches essentially anything, and nothing at all was registered
    /// for the narrower <c>NDEF_DISCOVERED</c>. So the tap was read by this phone, dispatched by the
    /// platform, and swallowed by a chat app.
    /// </para>
    /// <para>
    /// Foreground dispatch routes anything read to this activity instead, where it is ignored. Null
    /// filters and null tech lists mean "everything", which is exactly the intent: for as long as
    /// somebody is handing over the app, no other application on this phone gets a look in.
    /// </para>
    /// </remarks>
    private void Capture(bool capture)
    {
        if (_nfc is null) return;
        if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is not { } activity) return;

        // The NFC foreground APIs are activity-lifecycle bound and must be called on the thread that
        // owns the activity. This is reached from a Blazor event handler, which is not that thread.
        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (!capture) { _nfc.DisableForegroundDispatch(activity); return; }

                var back = new global::Android.Content.Intent(activity, activity.GetType())
                    .AddFlags(global::Android.Content.ActivityFlags.SingleTop);

                // The platform fills the tag into this intent, so on Android 12 and later it has to be
                // declared mutable — an immutable one is rejected outright and the capture silently
                // never happens.
                var flags = global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.S
                    ? global::Android.App.PendingIntentFlags.Mutable
                    : 0;

                var pending = global::Android.App.PendingIntent.GetActivity(activity, 0, back, flags);
                _nfc.EnableForegroundDispatch(activity, pending, null, null);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Info("AetherTMB", "could not capture the tap: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// While this screen is open, this app answers the tap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The NDEF Tag Application id is not ours — it belongs to the NFC Forum, and every app doing
    /// tap-to-share claims it. Measured on a handset here: X registers the identical AID to share a
    /// profile by holding phones together. With two services claiming one id Android has to pick, and
    /// what it picks by default is not necessarily the app the person is looking at.
    /// </para>
    /// <para>
    /// This says: while the Touch My Blood screen is in front of somebody, the tap is ours. It is
    /// released the moment that screen goes away, so nothing is taken from any other app for longer
    /// than a person is actively handing over the app.
    /// </para>
    /// </remarks>
    private void Prefer(bool preferred)
    {
        try
        {
            if (_nfc is null) return;
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is not { } activity) return;

            var emulation = CardEmulation.GetInstance(_nfc);
            if (emulation is null) return;

            var service = new global::Android.Content.ComponentName(
                activity, "com.bhengubv.aethernet.TouchMyBlood");

            if (preferred) emulation.SetPreferredService(activity, service);
            else emulation.UnsetPreferredService(activity);
        }
        catch (Exception ex)
        {
            // Not fatal: without it the tap still works whenever nothing else is claiming the id.
            global::Android.Util.Log.Info("AetherTMB", "could not claim the tap: " + ex.Message);
        }
    }

    /// <inheritdoc />
    public event Action? Tapped;
}
#endif
