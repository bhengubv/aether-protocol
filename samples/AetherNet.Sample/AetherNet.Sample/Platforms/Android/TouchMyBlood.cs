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
/// every tap-to-pay card in the world runs in — and what is emulated here is an NFC Forum Type 4 Tag:
/// a capability container, an NDEF file, and the handful of commands a reader uses to walk them. The
/// taker's phone is not being tricked. It asks a tag the questions the specification says to ask, and
/// gets the answers the specification says to give.
/// </para>
///
/// <para>
/// What it reads is an address on the giver's own handset. The bytes of the app then travel over
/// Wi-Fi, which is what the NFC Forum's own Connection Handover is for — the tap introduces, the fast
/// carrier delivers. That division is not a workaround; it is the design.
/// </para>
/// </summary>
[Service(Exported = true, Permission = "android.permission.BIND_NFC_SERVICE",
    Name = "com.bhengubv.aethernet.TouchMyBlood")]
[IntentFilter(["android.nfc.cardemulation.action.HOST_APDU_SERVICE"])]
[MetaData("android.nfc.cardemulation.host_apdu_service", Resource = "@xml/apduservice")]
public sealed class TouchMyBlood : HostApduService
{
    /// <summary>
    /// What the next tap will hand over, or null when nothing is being offered.
    /// </summary>
    /// <remarks>
    /// Static because Android builds this service itself and there is nowhere to hand it a dependency.
    /// It is one string, written by the screen the person is looking at and read on the NFC thread,
    /// so it is marked volatile and never anything more complicated than a reference swap.
    /// </remarks>
    private static volatile string? _offer;

    /// <summary>This phone's AetherTag, so a tap between two people who both have Aether is a contact.</summary>
    private static volatile string? _tag;

    /// <summary>Raised when a reader has actually taken the message — the moment a tap "landed".</summary>
    public static event Action? Tapped;

    /// <summary>Arm the tap. Call with null to disarm it.</summary>
    public static void Offer(string? invite, string? aetherTag)
    {
        _offer = invite;
        _tag = aetherTag;
    }

    /// <summary>Whether a tap would currently hand anything over.</summary>
    public static bool IsArmed => _offer is not null;

    // ── ISO 7816-4, as much of it as a Type 4 Tag needs ──────────────────────

    private static readonly byte[] Ok = [0x90, 0x00];
    private static readonly byte[] FileNotFound = [0x6A, 0x82];
    private static readonly byte[] WrongParameters = [0x6A, 0x86];
    private static readonly byte[] NotSupported = [0x6D, 0x00];

    /// <summary>
    /// The NDEF Tag Application, NFC Forum Type 4 Tag Operation §5.4.2. A reader selects this by name
    /// to say "I would like to talk to you as an NDEF tag".
    /// </summary>
    private static readonly byte[] NdefApplication = [0xD2, 0x76, 0x00, 0x00, 0x85, 0x01, 0x01];

    private static readonly byte[] CapabilityContainerId = [0xE1, 0x03];
    private static readonly byte[] NdefFileId = [0xE1, 0x04];

    /// <summary>
    /// The Capability Container: a fixed fifteen bytes telling the reader where the NDEF file is and
    /// how much of it may be read at a time.
    /// </summary>
    /// <remarks>
    /// Every value here is from the specification rather than chosen. Write access is <c>0xFF</c> —
    /// denied — because this tag is something to be read and never something a passing reader gets to
    /// change.
    /// </remarks>
    private static readonly byte[] CapabilityContainer =
    [
        0x00, 0x0F,             // CCLEN — this structure is 15 bytes
        0x20,                   // mapping version 2.0
        0x00, 0x3B,             // MLe — most data we will return in one response
        0x00, 0x34,             // MLc — most data we will accept in one command
        0x04, 0x06,             // NDEF File Control TLV: type 4, length 6
        0xE1, 0x04,             // the NDEF file's id
        0x7F, 0xFF,             // the largest NDEF file we will ever present
        0x00,                   // read access granted
        0xFF,                   // write access denied
    ];

    private enum Selected { None, CapabilityContainer, Ndef }

    private Selected _selected = Selected.None;

    /// <summary>
    /// The message this tap is presenting, frozen for the length of one tap.
    /// </summary>
    /// <remarks>
    /// Captured when the reader selects the application rather than read afresh on every command. A
    /// reader fetches a message across several READ BINARYs, and if the offer changed between two of
    /// them the taker would assemble half of one address and half of another — a URL that resolves to
    /// nothing, arriving as a browser error nobody could explain.
    /// </remarks>
    private byte[] _file = [];

    public override byte[]? ProcessCommandApdu(byte[]? commandApdu, Bundle? extras)
    {
        if (commandApdu is null || commandApdu.Length < 4) return NotSupported;

        var ins = commandApdu[1];

        return ins switch
        {
            0xA4 => Select(commandApdu),
            0xB0 => ReadBinary(commandApdu),
            _ => NotSupported,
        };
    }

    private byte[] Select(byte[] apdu)
    {
        var p1 = apdu[2];
        var p2 = apdu[3];

        // SELECT by name — the reader asking for the NDEF application.
        if (p1 == 0x04 && p2 == 0x00)
        {
            if (!DataOf(apdu, out var aid) || !aid.AsSpan().SequenceEqual(NdefApplication))
                return FileNotFound;

            // Freeze what this tap will present.
            var invite = _offer;
            if (invite is null) return FileNotFound;

            var message = Ndef.UriAndTag(invite, _tag ?? string.Empty);

            // A Type 4 Tag's NDEF file is the message with its length in front of it, big-endian.
            _file = new byte[2 + message.Length];
            _file[0] = (byte)(message.Length >> 8);
            _file[1] = (byte)(message.Length & 0xFF);
            message.CopyTo(_file, 2);

            _selected = Selected.None;
            return Ok;
        }

        // SELECT by file id — the capability container, or the NDEF file itself.
        if (p1 == 0x00 && p2 == 0x0C)
        {
            if (!DataOf(apdu, out var id) || id.Length != 2) return WrongParameters;

            if (id.AsSpan().SequenceEqual(CapabilityContainerId)) { _selected = Selected.CapabilityContainer; return Ok; }
            if (id.AsSpan().SequenceEqual(NdefFileId)) { _selected = Selected.Ndef; return Ok; }

            return FileNotFound;
        }

        return WrongParameters;
    }

    private byte[] ReadBinary(byte[] apdu)
    {
        var source = _selected switch
        {
            Selected.CapabilityContainer => CapabilityContainer,
            Selected.Ndef => _file,
            _ => null,
        };

        if (source is null || source.Length == 0) return FileNotFound;

        var offset = (apdu[2] << 8) | apdu[3];
        var wanted = apdu.Length > 4 ? apdu[^1] : 0;
        if (wanted == 0) wanted = 256;

        if (offset >= source.Length) return WrongParameters;

        var take = Math.Min(wanted, source.Length - offset);
        var response = new byte[take + 2];
        Array.Copy(source, offset, response, 0, take);
        response[take] = Ok[0];
        response[take + 1] = Ok[1];

        // The reader has what it came for once it has read to the end of the message.
        if (_selected == Selected.Ndef && offset + take >= source.Length)
            Tapped?.Invoke();

        return response;
    }

    /// <summary>Pull the data field out of a command, honouring its length byte rather than the array's.</summary>
    private static bool DataOf(byte[] apdu, out byte[] data)
    {
        data = [];
        if (apdu.Length < 5) return false;

        var length = apdu[4];
        if (length == 0 || apdu.Length < 5 + length) return false;

        data = new byte[length];
        Array.Copy(apdu, 5, data, 0, length);
        return true;
    }

    /// <summary>
    /// The phones came apart. Nothing to clean up — the next tap starts from the application select.
    /// </summary>
    public override void OnDeactivated(DeactivationReason reason)
    {
        _selected = Selected.None;
        _file = [];
    }
}
#endif
