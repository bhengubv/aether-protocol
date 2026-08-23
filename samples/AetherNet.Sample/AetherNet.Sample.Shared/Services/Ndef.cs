// SPDX-License-Identifier: MIT

using System.Text;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// The bytes that cross on a tap.
///
/// <para>
/// NDEF is the NFC Forum's message format — the thing every NFC tag on earth holds and every phone
/// already knows how to read. A phone with no app installed will act on one of these, which is the
/// entire reason a tap can hand somebody software they have never heard of: their handset is not
/// running our code, it is running the NFC spec.
/// </para>
///
/// <para>
/// Encoded here rather than through the platform's own <c>NdefMessage</c> class for one reason: this
/// is a wire format, it has to be byte-exact, and a wire format that can only be checked by holding
/// two phones together is a wire format nobody checks. Every byte below is in the spec and every byte
/// below is tested.
/// </para>
/// </summary>
public static class Ndef
{
    // Record header bits, NFC Forum NDEF 1.0 §3.2.
    private const byte MessageBegin = 0x80;
    private const byte MessageEnd = 0x40;
    private const byte ShortRecord = 0x10;
    private const byte TnfWellKnown = 0x01;
    private const byte TnfExternal = 0x04;

    /// <summary>Record type 'U' — a URI, NFC Forum RTD-URI.</summary>
    private const byte TypeUri = 0x55;

    /// <summary>
    /// The abbreviations the URI record uses so common schemes cost one byte instead of eight.
    /// </summary>
    /// <remarks>
    /// Ordered longest-first when matched, so <c>https://www.</c> wins over <c>https://</c>. Getting
    /// that backwards produces a URI that still parses and points somewhere subtly wrong.
    /// </remarks>
    private static readonly (byte Code, string Prefix)[] Prefixes =
    [
        (0x01, "http://www."),
        (0x02, "https://www."),
        (0x03, "http://"),
        (0x04, "https://"),
        (0x05, "tel:"),
        (0x06, "mailto:"),
    ];

    /// <summary>
    /// One URI record, as a whole NDEF message.
    /// </summary>
    /// <remarks>
    /// This is what makes a stock phone open something on a tap. No app, no pairing, no account — the
    /// handset reads a URI record off what it believes is a tag and offers it to the person, exactly
    /// as it would for a poster or a bus stop.
    /// </remarks>
    public static byte[] Uri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            throw new ArgumentException("A URI record needs a URI.", nameof(uri));

        // Longest match wins: "https://www.x" must be one abbreviation, not "https://" followed by a
        // literal "www.x". Both encode to something that parses; only one round-trips.
        byte code = 0x00;
        var matched = 0;

        foreach (var (c, prefix) in Prefixes)
        {
            if (prefix.Length <= matched) continue;
            if (!uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            code = c;
            matched = prefix.Length;
        }

        var body = Encoding.UTF8.GetBytes(uri[matched..]);
        var payload = new byte[1 + body.Length];
        payload[0] = code;
        body.CopyTo(payload, 1);

        return Record(MessageBegin | MessageEnd | ShortRecord | TnfWellKnown, [TypeUri], payload);
    }

    /// <summary>
    /// A URI record and an external record carrying the giver's AetherTag, in one message.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A phone with no app reads the first record and opens the address; the second is data it does
    /// not recognise and quietly ignores, which is what the spec says an unknown record type must do.
    /// A phone that DOES have Aether reads both, so tapping a mate who is already on the network adds
    /// them instead of offering them software they are already running.
    /// </para>
    /// <para>
    /// One tap, two meanings, decided by what the phone on the other side already has.
    /// </para>
    /// </remarks>
    public static byte[] UriAndTag(string uri, string aetherTag)
    {
        if (string.IsNullOrWhiteSpace(aetherTag)) return Uri(uri);

        var first = Uri(uri);
        // Clear the "message end" bit on the first record — it is no longer the last one.
        first[0] &= unchecked((byte)~MessageEnd);

        var tag = Record(
            MessageEnd | ShortRecord | TnfExternal,
            Encoding.ASCII.GetBytes(TagRecordType),
            Encoding.ASCII.GetBytes(aetherTag));

        var message = new byte[first.Length + tag.Length];
        first.CopyTo(message, 0);
        tag.CopyTo(message, first.Length);
        return message;
    }

    /// <summary>
    /// The external record type carrying an AetherTag.
    /// </summary>
    /// <remarks>
    /// External types are the spec's namespace for "mine" — a domain you control, a colon, a name.
    /// Using one means a phone that has never heard of Aether skips it without complaint instead of
    /// showing somebody a screen full of nonsense.
    /// </remarks>
    public const string TagRecordType = "bhengubv.com:aethertag";

    private static byte[] Record(int header, byte[] type, byte[] payload)
    {
        if (payload.Length > 255)
            throw new ArgumentException("Short records carry at most 255 bytes.", nameof(payload));

        var record = new byte[3 + type.Length + payload.Length];
        record[0] = (byte)header;
        record[1] = (byte)type.Length;
        record[2] = (byte)payload.Length;
        type.CopyTo(record, 3);
        payload.CopyTo(record, 3 + type.Length);
        return record;
    }

    /// <summary>
    /// Read a URI back out of a message. The counterpart to <see cref="Uri"/>, and the reason it can
    /// be trusted without two phones.
    /// </summary>
    public static string? ReadUri(byte[]? message)
    {
        if (message is null || message.Length < 4) return null;

        var offset = 0;
        while (offset + 3 <= message.Length)
        {
            var header = message[offset];
            var typeLength = message[offset + 1];

            // Only short records are produced here, and only short records are read.
            if ((header & ShortRecord) == 0) return null;

            var payloadLength = message[offset + 2];
            var typeAt = offset + 3;
            var payloadAt = typeAt + typeLength;
            if (payloadAt + payloadLength > message.Length) return null;

            if ((header & 0x07) == TnfWellKnown && typeLength == 1 && message[typeAt] == TypeUri &&
                payloadLength >= 1)
            {
                var code = message[payloadAt];
                var rest = Encoding.UTF8.GetString(message, payloadAt + 1, payloadLength - 1);
                foreach (var (c, prefix) in Prefixes)
                    if (c == code) return prefix + rest;
                return code == 0x00 ? rest : null;
            }

            if ((header & MessageEnd) != 0) return null;
            offset = payloadAt + payloadLength;
        }

        return null;
    }
}
