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
    private readonly bool _hasNfcF;
    private long _claimedAt;

    public AndroidTapShare()
    {
        _nfc = NfcAdapter.GetDefaultAdapter(AndroidApp.Context);

        // Two different things, and a phone can have the first without the second. Plenty of handsets
        // read tags perfectly and cannot pretend to be one, and on those Touch My Blood cannot work in
        // this direction however healthy the NFC chip looks.
        _hasEmulation = AndroidApp.Context.PackageManager?
            .HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureNfcHostCardEmulation) == true;

        // The second radio. Measured on the P30: android.hardware.nfc.hcef is present, so this handset
        // can be a Type 3 tag as well as a Type 4 one — and nothing else on it is competing there.
        _hasNfcF = AndroidApp.Context.PackageManager?
            .HasSystemFeature("android.hardware.nfc.hcef") == true;

        TouchMyBlood.Tapped += () => Tapped?.Invoke();

        // Both NFC claims are bound to an activity being RESUMED, and Android drops them on its own
        // when it pauses. Measured: the screen stayed open and armed, this class never released
        // anything, and the dump still read "Current preferred foreground service: null" — so a
        // notification shade, a lock, or a glance at another app was enough to hand the tap back.
        //
        // That is not a cosmetic loss. Another app on this handset claims the same identifier —
        // "Share your X profile by holding phones together" — so an unclaimed tap does not fail
        // quietly, it goes to them.
        Microsoft.Maui.ApplicationModel.Platform.ActivityStateChanged += (_, e) =>
        {
            if (e.State is not Microsoft.Maui.ApplicationModel.ActivityState.Resumed) return;
            if (!TouchMyBlood.IsArmed) return;

            // Resumed arrives twice for one return to the screen, and each claim reconfigures the NFC
            // controller. Doing that twice in a millisecond is churn on the one radio a tap depends
            // on, so the second is dropped.
            var now = Environment.TickCount64;
            if (now - _claimedAt < 500) return;
            _claimedAt = now;

            global::Android.Util.Log.Info("AetherTMB", "back in front — claiming the tap again");
            Prefer(true);
            Capture(false);
        };
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
    public void Arm(string aetherTag, string? ssid = null, string? passphrase = null)
    {
        if (!CanArm()) return;

        TouchMyBlood.Offer(aetherTag, ssid, passphrase);
        AlsoOnNfcF(TouchMyBlood.Armed, "the same thing");
        Prefer(true);
        Capture(false);
    }

    /// <inheritdoc />
    public void ArmRaw(byte[] message, string what)
    {
        if (!CanArm()) return;

        TouchMyBlood.Offer(message, what);
        AlsoOnNfcF(message, what);
        Prefer(true);
        Capture(false);
    }

    /// <summary>
    /// Put the same message on the other radio.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a fallback — a second lane. A reader polls NFC-A and NFC-F in turn, and on NFC-A it may
    /// find X's profile service instead of us, because that app claims the same identifier we do.
    /// On NFC-F there is nobody else on an ordinary handset outside Japan.
    /// </para>
    /// <para>
    /// Offering both costs one array reference and removes a race we lost repeatedly.
    /// </para>
    /// </remarks>
    private void AlsoOnNfcF(byte[]? message, string what)
    {
        if (!_hasNfcF || _nfc is null) return;

        TouchMyBloodF.Offer(message, what);

        if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is not { } activity) return;

        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                // Unlike the other radio, this one answers ONLY while an activity has switched it on.
                // A phone in a pocket cannot quietly serve taps, which is the right default.
                var f = global::Android.Nfc.CardEmulators.NfcFCardEmulation.GetInstance(_nfc);
                var component = new global::Android.Content.ComponentName(
                    activity, "com.bhengubv.aethernet.TouchMyBloodF");

                if (message is null) { f?.DisableService(activity); return; }

                var on = f?.EnableService(activity, component) == true;
                global::Android.Util.Log.Info("AetherTMB",
                    on ? "F: this phone is now also a Type 3 tag" : "F: could not switch on the NFC-F tag");
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Info("AetherTMB", "F: could not switch on the NFC-F tag: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// Whether this phone can be read at all, said out loud either way.
    /// </summary>
    /// <remarks>
    /// This used to return in silence, and the screen asks <see cref="IsSupported"/> separately — so a
    /// phone that could not arm still showed "touch the phones back to back" while offering nothing.
    /// Two taps were spent on that: the giver's log was empty, the taker read an empty NDEF, and
    /// neither end said why.
    /// </remarks>
    private bool CanArm()
    {
        if (IsSupported) return true;

        global::Android.Util.Log.Info("AetherTMB",
            $"⚠ NOT armed — {UnavailableReason ?? "NFC is unavailable"} " +
            $"(adapter={(_nfc is null ? "absent" : _nfc.IsEnabled ? "on" : "off")}, " +
            $"emulation={_hasEmulation})");

        return false;
    }

    /// <inheritdoc />
    public void Disarm()
    {
        TouchMyBlood.Offer(null);
        AlsoOnNfcF(null, "");
        Prefer(false);
        Capture(false);
    }

    /// <summary>
    /// Whether this phone should also be READING while it is offering to be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It should not, and finding that out cost most of an evening.</b> Two phones held together
    /// each take turns emitting a reader field and listening for one. Whichever emits when the other
    /// is listening wins, and the loser answers as a card. With foreground dispatch held the whole
    /// time, this phone emits continuously and therefore wins every single round — so the phone we
    /// are trying to hand something to spends the entire tap being read by us instead of reading.
    /// </para>
    /// <para>
    /// Measured from the taker's own NFC service, tap after tap: <c>RF FIELD DEACTIVATED … (cur:1)</c>
    /// — it saw our reader field and was in card mode for all of it — while its card emulation bound
    /// to <c>com.twitter.android/…ProfileTagApduService</c>, which claims the same identifier we do.
    /// So a tap that looked like nothing happening was in fact this phone reading X's profile off
    /// theirs.
    /// </para>
    /// <para>
    /// Dispatch is what this was for: without it, whatever this phone reads goes to whichever app
    /// registered the broadest filter, and on a P30 that was WeChat. That problem disappears along
    /// with the reading — a phone that is not looking has nothing to hand to anybody.
    /// </para>
    /// </remarks>
    private void Capture(bool capture)
    {
        if (_nfc is null) return;
        if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is not { } activity)
        {
            global::Android.Util.Log.Info("AetherTMB", "⚠ no activity — cannot capture the tap");
            return;
        }

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
                global::Android.Util.Log.Info("AetherTMB", "captured the tap — nothing else on this phone gets a tag now");
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
            if (Microsoft.Maui.ApplicationModel.Platform.CurrentActivity is not { } activity)
            {
                // Both NFC claims are bound to an activity. Without one they cannot be made, and the
                // tap quietly goes to whichever app registered the broadest filter.
                global::Android.Util.Log.Info("AetherTMB", "⚠ no activity — cannot claim the tap");
                return;
            }

            var emulation = CardEmulation.GetInstance(_nfc);
            if (emulation is null) return;

            var service = new global::Android.Content.ComponentName(
                activity, "com.bhengubv.aethernet.TouchMyBlood");

            if (preferred) emulation.SetPreferredService(activity, service);
            else emulation.UnsetPreferredService(activity);

            global::Android.Util.Log.Info("AetherTMB",
                preferred ? "claimed the tap — a reader touching us gets Aether" : "released the tap");
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
