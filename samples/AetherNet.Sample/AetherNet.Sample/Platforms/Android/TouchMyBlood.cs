// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Sample.Shared.Services;
using Android.App;
using Android.Content;
using Android.Nfc.CardEmulators;
using Android.OS;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Touch My Blood — the giver's phone, being an NFC tag.
///
/// <para>
/// The friend on the other side has nothing installed. No Aether, no account, nothing of ours running
/// at all. So the tap cannot be a conversation between two copies of this app — there is only one
/// copy. What their handset does have is the NFC spec, and every Android phone made in the last
/// decade will read a tag and offer to open what is on it.
/// </para>
///
/// <para>
/// So this phone becomes the tag. Card emulation is one of NFC's three standard modes — the same one
/// every tap-to-pay card in the world runs in — and what is emulated here is an NFC Forum Type 4 Tag.
/// The taker's phone is not being tricked: it asks a tag the questions the specification says to ask,
/// and gets the answers the specification says to give.
/// </para>
///
/// <para>
/// What it reads is an address on the giver's own handset. The bytes of the app then travel over
/// Wi-Fi, which is what the NFC Forum's own Connection Handover is for — the tap introduces, the fast
/// carrier delivers. That division is not a workaround; it is the design.
/// </para>
///
/// <para>
/// The conversation itself lives in <see cref="Type4Tag"/>, on the platform-neutral side, so a
/// reader's entire walk can be played against it in a test instead of guessed at with two handsets.
/// All that is left here is what Android has to own: the service, and the two static handles the
/// screen uses to arm it.
/// </para>
/// </summary>
[Service(Exported = true, Permission = "android.permission.BIND_NFC_SERVICE",
    Name = "com.bhengubv.aethernet.TouchMyBlood")]
[IntentFilter(["android.nfc.cardemulation.action.HOST_APDU_SERVICE"])]
[MetaData("android.nfc.cardemulation.host_apdu_service", Resource = "@xml/apduservice")]
public sealed class TouchMyBlood : HostApduService
{
    /// <summary>
    /// What the next tap hands over, or null when nothing is offered.
    /// </summary>
    /// <remarks>
    /// Static because Android builds this service itself and there is nowhere to hand it a dependency.
    /// It is one byte array, written by the screen the person is looking at and read on the NFC
    /// thread, so it is only ever a reference swap.
    /// </remarks>
    private static volatile byte[]? _offer;

    /// <summary>Raised when a reader has taken the message — the moment a tap landed.</summary>
    public static event Action? Tapped;

    /// <summary>
    /// Arm the tap. Call with nothing to disarm it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two different taps, decided by what is being offered. When a network is being handed over the
    /// tap carries <b>Wi-Fi credentials</b> — a format stock Android already acts on, the same one
    /// that makes a phone offer to join a printer. Their handset joins a network with a person's name
    /// on it, notices there is no internet behind it, and raises its own sign-in sheet. Nobody ever
    /// reads an address.
    /// </para>
    /// <para>
    /// With no network to give, it carries this phone's identity instead, and a tap between two people
    /// who both have Aether hands over what is on screen.
    /// </para>
    /// </remarks>
    public static void Offer(string? aetherTag, string? ssid = null, string? passphrase = null)
    {
        if (!string.IsNullOrWhiteSpace(ssid) && !string.IsNullOrWhiteSpace(passphrase))
        {
            try
            {
                _offer = WifiHandover.Message(ssid, passphrase);
                return;
            }
            catch (ArgumentException ex)
            {
                global::Android.Util.Log.Info("AetherTMB", "bad network credentials: " + ex.Message);
            }
        }

        _offer = string.IsNullOrWhiteSpace(aetherTag) ? null : Ndef.Tag(aetherTag);
    }

    /// <summary>Whether a tap would currently hand anything over.</summary>
    public static bool IsArmed => _offer is not null;

    private readonly Type4Tag _tag = new();

    public TouchMyBlood() => _tag.Read += () => Tapped?.Invoke();

    public override byte[]? ProcessCommandApdu(byte[]? commandApdu, Bundle? extras)
    {
        // Read afresh for each command rather than captured once: the tag freezes the message itself
        // when the reader selects the application, which is the point at which it must stop changing.
        _tag.Offer = _offer;
        return _tag.Process(commandApdu);
    }

    public override void OnDeactivated(DeactivationReason reason) => _tag.Deactivated();
}
#endif
