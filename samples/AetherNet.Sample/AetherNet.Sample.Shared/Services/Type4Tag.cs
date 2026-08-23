// SPDX-License-Identifier: MIT

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The conversation a phone has with a tag, from the tag's side.
///
/// <para>
/// An NFC reader does not ask for "the message". It selects an application by name, selects a file by
/// id, reads a capability container to find out where the message lives and how much may be read at a
/// time, selects that file, and then reads it in pieces. Every one of those steps has a defined
/// command and a defined answer, and getting any of them wrong produces the same symptom: two phones
/// touch and nothing happens.
/// </para>
///
/// <para>
/// Which is exactly why it is here and not beside the Android service. This is the piece that decides
/// whether a tap works, it is pure byte-shuffling with no platform in it, and the alternative to
/// testing it is holding two handsets together and guessing. A reader's entire walk can be played
/// against this in a unit test.
/// </para>
///
/// <para>
/// NFC Forum Type 4 Tag Operation, over ISO 7816-4 APDUs.
/// </para>
/// </summary>
public sealed class Type4Tag
{
    // ── Status words ─────────────────────────────────────────────────────────
    public static readonly byte[] Ok = [0x90, 0x00];
    public static readonly byte[] FileNotFound = [0x6A, 0x82];
    public static readonly byte[] WrongParameters = [0x6A, 0x86];
    public static readonly byte[] NotSupported = [0x6D, 0x00];

    /// <summary>
    /// The NDEF Tag Application, Type 4 Tag Operation §5.4.2.
    /// </summary>
    /// <remarks>
    /// Not ours — it belongs to the NFC Forum, and every app doing tap-to-share claims it. That is
    /// what makes a reader which has never heard of this app willing to talk to it, and also why the
    /// platform has to be told we want the tap while our screen is open.
    /// </remarks>
    public static readonly byte[] NdefApplication = [0xD2, 0x76, 0x00, 0x00, 0x85, 0x01, 0x01];

    public static readonly byte[] CapabilityContainerId = [0xE1, 0x03];
    public static readonly byte[] NdefFileId = [0xE1, 0x04];

    /// <summary>
    /// A fixed fifteen bytes telling the reader where the NDEF file is and how much may be read.
    /// </summary>
    /// <remarks>
    /// Every value is from the specification rather than chosen. Write access is denied because this
    /// is something to be read, never something a passing reader gets to change.
    /// </remarks>
    public static readonly byte[] CapabilityContainer =
    [
        0x00, 0x0F,             // CCLEN — this structure is 15 bytes
        0x20,                   // mapping version 2.0
        0x00, 0x3B,             // MLe — most data returned in one response
        0x00, 0x34,             // MLc — most data accepted in one command
        0x04, 0x06,             // NDEF File Control TLV: type 4, length 6
        0xE1, 0x04,             // the NDEF file's id
        0x7F, 0xFF,             // the largest NDEF file ever presented
        0x00,                   // read access granted
        0xFF,                   // write access denied
    ];

    private enum File { None, CapabilityContainer, Ndef }

    private File _selected = File.None;
    private byte[] _file = [];

    /// <summary>
    /// What the next tap presents, or null when nothing is being offered.
    /// </summary>
    /// <remarks>
    /// Read once, when the reader selects the application. A reader fetches a message across several
    /// reads, and if this changed between two of them the taker would assemble half of one address
    /// and half of another — a URL resolving to nothing, arriving as a browser error nobody could
    /// explain.
    /// </remarks>
    public byte[]? Offer { get; set; }

    /// <summary>A reader has taken the whole message. The moment a tap landed.</summary>
    public event Action? Read;

    /// <summary>Answer one command from the reader.</summary>
    public byte[] Process(byte[]? apdu)
    {
        if (apdu is null || apdu.Length < 4) return NotSupported;

        return apdu[1] switch
        {
            0xA4 => Select(apdu),
            0xB0 => ReadBinary(apdu),
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

            if (Offer is not { Length: > 0 } message) return FileNotFound;

            // A Type 4 Tag's NDEF file is the message with its length in front of it, big-endian.
            _file = new byte[2 + message.Length];
            _file[0] = (byte)(message.Length >> 8);
            _file[1] = (byte)(message.Length & 0xFF);
            message.CopyTo(_file, 2);

            _selected = File.None;
            return Ok;
        }

        // SELECT by file id — the capability container, or the NDEF file itself.
        if (p1 == 0x00 && p2 == 0x0C)
        {
            if (!DataOf(apdu, out var id) || id.Length != 2) return WrongParameters;

            if (id.AsSpan().SequenceEqual(CapabilityContainerId)) { _selected = File.CapabilityContainer; return Ok; }

            // The NDEF file cannot be selected before the application is, or there is nothing behind
            // it — a reader that skipped the first step would otherwise read two zero bytes and
            // conclude the tag is empty.
            if (id.AsSpan().SequenceEqual(NdefFileId))
            {
                if (_file.Length == 0) return FileNotFound;
                _selected = File.Ndef;
                return Ok;
            }

            return FileNotFound;
        }

        return WrongParameters;
    }

    private byte[] ReadBinary(byte[] apdu)
    {
        var source = _selected switch
        {
            File.CapabilityContainer => CapabilityContainer,
            File.Ndef => _file,
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

        if (_selected == File.Ndef && offset + take >= source.Length)
            Read?.Invoke();

        return response;
    }

    /// <summary>The phones came apart. The next tap starts again from the application select.</summary>
    public void Deactivated()
    {
        _selected = File.None;
        _file = [];
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
}
