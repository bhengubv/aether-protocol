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
                Say($"armed — a tap now hands over the network {ssid} ({_offer.Length}B)");
                return;
            }
            catch (ArgumentException ex)
            {
                global::Android.Util.Log.Info("AetherTMB", "bad network credentials: " + ex.Message);
            }
        }

        if (string.IsNullOrWhiteSpace(aetherTag))
        {
            _offer = null;
            Say("disarmed — a tap now hands over nothing");
            return;
        }

        _offer = Ndef.Tag(aetherTag);
        Say($"armed — a tap now hands over the identity {aetherTag} ({_offer.Length}B)");
    }

    /// <summary>
    /// Arm the tap with a message assembled elsewhere.
    /// </summary>
    /// <remarks>
    /// The provisioning tap comes through here. It is built and tested on the neutral side because it
    /// is the one message this app sends that a person never sees any part of — if a byte of it is
    /// wrong, the only symptom is a phone that does nothing at all when touched.
    /// </remarks>
    public static void Offer(byte[]? message, string what)
    {
        if (message is not { Length: > 0 })
        {
            _offer = null;
            Say("disarmed — a tap now hands over nothing");
            return;
        }

        _offer = message;
        Say($"armed — a tap now hands over {what} ({message.Length}B)");
    }

    /// <summary>
    /// Trace to logcat under one tag.
    /// </summary>
    /// <remarks>
    /// Every line this class used to print was an error. So a tap that WORKED logged nothing, and
    /// silence read exactly like a tap that never happened — which is the one failure this feature
    /// has, and the one thing a log has to be able to tell you apart.
    /// </remarks>
    private static void Say(string message) => global::Android.Util.Log.Info("AetherTMB", message);

    /// <summary>Whether a tap would currently hand anything over.</summary>
    public static bool IsArmed => _offer is not null;

    /// <summary>
    /// Exactly what is currently on offer, so the other radio can carry the identical message.
    /// </summary>
    /// <remarks>
    /// Shared rather than rebuilt: two radios presenting slightly different bytes for the same tap is
    /// a bug that would only ever appear on one of them, and only sometimes.
    /// </remarks>
    public static byte[]? Armed => _offer;

    private readonly Type4Tag _tag = new();
    private bool _first = true;

    public TouchMyBlood() => _tag.Read += () =>
    {
        Say("● a reader took the whole message — the tap landed");
        Tapped?.Invoke();
    };

    public override byte[]? ProcessCommandApdu(byte[]? commandApdu, Bundle? extras)
    {
        // The first command of a tap. Worth one line: it is the only proof that another phone's radio
        // reached this one at all, and everything after it is either the conversation working or the
        // conversation failing — both of which are silent from the outside.
        if (_first) { _first = false; Say("a phone is reading us"); }

        // Read afresh for each command rather than captured once: the tag freezes the message itself
        // when the reader selects the application, which is the point at which it must stop changing.
        _tag.Offer = _offer;
        var response = _tag.Process(commandApdu);

        // The whole walk, one line per command.
        //
        // Measured: a reader engaged this tag with a 627-byte message on it, stayed for six seconds,
        // and never completed — while the same tag carrying 106 bytes completes in 199 ms. From the
        // outside those two are identical: a phone touched, and nothing happened. The only thing that
        // separates them is which command the reader stopped on, and that is invisible without this.
        if (commandApdu is { Length: >= 4 })
        {
            var status = response is { Length: >= 2 }
                ? $"{response[^2]:X2}{response[^1]:X2}"
                : "----";

            global::Android.Util.Log.Info("AetherTMB",
                $"  ins={commandApdu[1]:X2} p1p2={commandApdu[2]:X2}{commandApdu[3]:X2} " +
                $"le={(commandApdu.Length > 4 ? commandApdu[^1] : 0)} " +
                $"→ {(response?.Length ?? 0)}B sw={status}");
        }

        return response;
    }

    public override void OnDeactivated(DeactivationReason reason)
    {
        Say($"the phones came apart ({reason})");
        _first = true;
        _tag.Deactivated();
    }
}
#endif
