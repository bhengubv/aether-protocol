// SPDX-License-Identifier: MIT
#if ANDROID
using AetherNet.Sample.Shared.Services;
using Android.Nfc.CardEmulators;
using Android.OS;

namespace AetherNet.Sample.Platforms.Android;

/// <summary>
/// Touch My Blood on NFC-F — the empty radio.
///
/// <para>
/// The tag in <see cref="TouchMyBlood"/> lives on NFC-A, which is where every bank card, every sticker
/// and every tap-to-share app already is. Measured on a stock Redmi: a tap there gets answered by
/// <c>com.twitter.android/…ProfileTagApduService</c>, because X registers the same NDEF application
/// identifier we do, and two phones held together race for who reads whom. We lost that race all
/// evening.
/// </para>
///
/// <para>
/// <b>NFC-F has nobody on it.</b> It is the radio Suica runs on, and outside Japan an ordinary handset
/// emulates nothing there. Different addressing — a system code and an eight-byte identity rather than
/// application identifiers — so the contest does not exist rather than being won.
/// </para>
///
/// <para>
/// Two things differ from the other service and both are in our favour. This one is foreground-only:
/// the platform will not route to it unless an activity has switched it on, so a phone in a pocket
/// cannot quietly answer taps. And the tag format states its message length in three bytes instead of
/// two, which is where the 64 KB figure came from — it was one format's field width, not a property of
/// NFC.
/// </para>
///
/// <para>
/// The conversation itself lives in <see cref="Type3Tag"/> on the neutral side, so a reader's whole
/// exchange can be played against it in a test rather than guessed at with two handsets.
/// </para>
/// </summary>
[global::Android.App.Service(
    Exported = true,
    Permission = "android.permission.BIND_NFC_SERVICE",
    Name = "com.bhengubv.aethernet.TouchMyBloodF")]
[global::Android.App.IntentFilter(["android.nfc.cardemulation.action.HOST_NFCF_SERVICE"])]
[global::Android.App.MetaData("android.nfc.cardemulation.host_nfcf_service",
    Resource = "@xml/nfcfservice")]
public sealed class TouchMyBloodF : HostNfcFService
{
    /// <summary>The identity this tag answers to, matching the manifest filter.</summary>
    /// <remarks>
    /// Fixed rather than random so the value in the descriptor and the value on the wire cannot drift
    /// apart. They must agree or the platform routes nothing to us and the tap is silent.
    /// </remarks>
    public static readonly byte[] Nfcid2 = [0x02, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static volatile byte[]? _offer;

    /// <summary>Raised when a reader has taken the whole message.</summary>
    public static event Action? Tapped;

    /// <summary>Arm the tap on this radio. Call with nothing to disarm.</summary>
    public static void Offer(byte[]? message, string what)
    {
        _offer = message is { Length: > 0 } ? message : null;

        Say(_offer is null
            ? "F: disarmed"
            : $"F: armed on NFC-F — {what} ({_offer.Length}B)");
    }

    /// <summary>Whether a tap on this radio would hand anything over.</summary>
    public static bool IsArmed => _offer is not null;

    private static void Say(string message) => global::Android.Util.Log.Info("AetherTMB", message);

    private readonly Type3Tag _tag = new() { Id = Nfcid2 };
    private bool _first = true;

    public TouchMyBloodF() => _tag.Read += () =>
    {
        Say("F: ● a reader took the whole message — the tap landed");
        Tapped?.Invoke();
    };

    public override byte[]? ProcessNfcFPacket(byte[]? commandPacket, Bundle? extras)
    {
        if (_first) { _first = false; Say("F: a phone is reading us on NFC-F"); }

        _tag.Offer = _offer;
        var reply = _tag.Process(commandPacket);

        if (commandPacket is { Length: >= 2 })
        {
            Say($"  F cmd={commandPacket[1]:X2} len={commandPacket.Length} → " +
                (reply is null ? "ignored" : $"{reply.Length}B"));
        }

        return reply;
    }

    public override void OnDeactivated(DeactivationReasonF reason)
    {
        Say($"F: the phones came apart ({reason})");
        _first = true;
        _tag.Deactivated();
    }
}
#endif
