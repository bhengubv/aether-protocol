// SPDX-License-Identifier: MIT

namespace AetherNet.Aware;

/// <summary>Which Bluetooth container carries a help message. Both are built here; the person's phone uses what it can.</summary>
public enum HelpAdvertForm
{
    /// <summary>
    /// Under a registered 16-bit Bluetooth service ID. The whole advert fits the standard 31 bytes, so every phone
    /// can send it and every phone can hear it — but the ID has to be registered with the Bluetooth SIG, which costs
    /// a fee. Until there is one, <see cref="HelpAdvert.Build"/> refuses this form and the app says why.
    /// </summary>
    Registered16,

    /// <summary>
    /// Under AetherNet's own 128-bit service ID. Free and ours, but the advert runs past the standard 31 bytes, so it
    /// needs Bluetooth 5 extended adverts at both ends.
    /// </summary>
    AetherNet128,

    /// <summary>
    /// The same 128-bit ID, sent in two halves: ten bytes in the advert and the other thirteen in its scan response,
    /// under a second ID. A standard advert holds 31 bytes and so does its scan response, and a phone that asks — any
    /// phone, the asking is what a normal scan does — is handed both at once, as one reading. So this needs no
    /// registered ID and no Bluetooth 5: it is the widest reach there is, and what a phone sends unless it has
    /// something better.
    /// </summary>
    AetherNet128Pair,
}

/// <summary>
/// The help message in a Bluetooth advert. The 23 bytes are the same either way (<see cref="HelpCodec"/>); only the
/// wrapper differs, so a phone can send whichever its radio supports and a guardian reads both.
/// </summary>
public static class HelpAdvert
{
    /// <summary>What a standard (legacy) advert holds.</summary>
    public const int LegacyAdvertBytes = 31;

    /// <summary>AetherNet's service ID for a help message — the "aether" base, like the mesh's own GATT ids.</summary>
    public static readonly Guid ServiceUuid = new("61657468-6572-0010-0000-000000000000");

    /// <summary>
    /// The second ID, for the rest of a message sent in two halves. A different ID because a reading holds one lot of
    /// data per ID, so the two halves would otherwise overwrite each other.
    /// </summary>
    public static readonly Guid ServiceUuidRest = new("61657468-6572-0010-0001-000000000000");

    /// <summary>How much of the message goes in the advert when it is sent in two halves. The rest follows.</summary>
    public const int FirstHalf = 10;

    /// <summary>How much follows in the scan response.</summary>
    public const int SecondHalf = HelpCodec.Length - FirstHalf;

    private const byte FlagsAd = 0x01;
    private const byte ServiceData16Ad = 0x16;
    private const byte ServiceData128Ad = 0x21;

    /// <summary>LE General Discoverable, BR/EDR not supported — what a listening-only advert says of itself.</summary>
    private const byte Flags = 0x06;

    /// <summary>How many advert bytes this form needs, flags included.</summary>
    public static int Size(HelpAdvertForm form) => form switch
    {
        HelpAdvertForm.Registered16 => 3 + 1 + 1 + 2 + HelpCodec.Length,
        HelpAdvertForm.AetherNet128 => 3 + 1 + 1 + 16 + HelpCodec.Length,
        HelpAdvertForm.AetherNet128Pair => 3 + 1 + 1 + 16 + FirstHalf,
        _ => throw new ArgumentOutOfRangeException(nameof(form)),
    };

    /// <summary>
    /// How many bytes follow in the scan response. Zero for a form that sends everything in the advert; the scan
    /// response has its own 31 bytes and no flags, which is what makes room for the rest.
    /// </summary>
    public static int ScanResponseSize(HelpAdvertForm form) => form switch
    {
        HelpAdvertForm.Registered16 or HelpAdvertForm.AetherNet128 => 0,
        HelpAdvertForm.AetherNet128Pair => 1 + 1 + 16 + SecondHalf,
        _ => throw new ArgumentOutOfRangeException(nameof(form)),
    };

    /// <summary>
    /// The two halves a radio actually sends for <see cref="HelpAdvertForm.AetherNet128Pair"/>: the advert, and the
    /// scan response it hands to whoever asks. Every platform sends these as the one pair, and a listener is given
    /// them as one reading.
    /// </summary>
    public static (byte[] Advert, byte[] ScanResponse) BuildPair(ReadOnlySpan<byte> message)
    {
        Check(message);
        var advert = new byte[Size(HelpAdvertForm.AetherNet128Pair)];
        advert[0] = 0x02;
        advert[1] = FlagsAd;
        advert[2] = Flags;
        advert[3] = (byte)(1 + 16 + FirstHalf);
        advert[4] = ServiceData128Ad;
        Little(ServiceUuid, advert.AsSpan(5));
        message[..FirstHalf].CopyTo(advert.AsSpan(21));

        var rest = new byte[ScanResponseSize(HelpAdvertForm.AetherNet128Pair)];
        rest[0] = (byte)(1 + 16 + SecondHalf);
        rest[1] = ServiceData128Ad;
        Little(ServiceUuidRest, rest.AsSpan(2));
        message[FirstHalf..].CopyTo(rest.AsSpan(18));
        return (advert, rest);
    }

    /// <summary>A 128-bit UUID goes out least-significant byte first.</summary>
    private static void Little(Guid uuid, Span<byte> to)
    {
        var be = uuid.ToByteArray(bigEndian: true);
        for (var i = 0; i < 16; i++)
        {
            to[i] = be[15 - i];
        }
    }

    private static void Check(ReadOnlySpan<byte> message)
    {
        if (message.Length != HelpCodec.Length)
        {
            throw new ArgumentException($"A help message is {HelpCodec.Length} bytes.", "message");
        }
    }

    /// <summary>Whether this form fits a standard advert, which every phone can send and hear.</summary>
    public static bool FitsLegacyAdvert(HelpAdvertForm form) => Size(form) <= LegacyAdvertBytes;

    /// <summary>
    /// What a listening phone ends up holding for a help message, which for every form but one is the advert itself.
    /// For <see cref="HelpAdvertForm.AetherNet128Pair"/> it is the advert followed by its scan response, because that
    /// is what a scan hands over — one reading, both halves. <see cref="BuildPair"/> gives the halves separately, for
    /// a radio that has to send them. <paramref name="registeredId"/> is the registered 16-bit service ID and is
    /// needed only by <see cref="HelpAdvertForm.Registered16"/>; without one that form throws, because an advert under
    /// an ID nobody assigned us could collide with another product's.
    /// </summary>
    public static byte[] Build(HelpAdvertForm form, ReadOnlySpan<byte> message, ushort? registeredId = null)
    {
        Check(message);
        if (form == HelpAdvertForm.AetherNet128Pair)
        {
            var (first, rest) = BuildPair(message);
            return [.. first, .. rest];
        }
        var advert = new byte[Size(form)];
        advert[0] = 0x02;
        advert[1] = FlagsAd;
        advert[2] = Flags;
        switch (form)
        {
            case HelpAdvertForm.Registered16:
                if (registeredId is not { } id)
                {
                    throw new InvalidOperationException(
                        "A standard advert needs a registered 16-bit Bluetooth service ID. Until one is registered, send as AetherNet128.");
                }
                advert[3] = (byte)(1 + 2 + HelpCodec.Length);
                advert[4] = ServiceData16Ad;
                advert[5] = (byte)(id & 0xFF);
                advert[6] = (byte)(id >> 8);
                message.CopyTo(advert.AsSpan(7));
                break;
            case HelpAdvertForm.AetherNet128:
                advert[3] = (byte)(1 + 16 + HelpCodec.Length);
                advert[4] = ServiceData128Ad;
                Little(ServiceUuid, advert.AsSpan(5));
                message.CopyTo(advert.AsSpan(21));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(form));
        }
        return advert;
    }

    /// <summary>The help message inside an advert, in either container. Null when there is none.</summary>
    public static byte[]? TryFind(ReadOnlySpan<byte> advert, ushort? registeredId = null) =>
        TryFind(new RadioFacts { ServiceData = BleAdParser.Parse(advert.ToArray()).ServiceData }, registeredId);

    /// <summary>
    /// The help message among the service data the radios already read, in either container. Null when none of it is
    /// a help message — which is every other advert in the air.
    /// </summary>
    public static byte[]? TryFind(RadioFacts facts, ushort? registeredId = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var want128 = Strings.HexOnly(ServiceUuid.ToString());
        var wantRest = Strings.HexOnly(ServiceUuidRest.ToString());
        var want16 = registeredId is { } id ? id.ToString("X4", System.Globalization.CultureInfo.InvariantCulture) : null;
        byte[]? first = null;
        byte[]? rest = null;
        foreach (var record in facts.ServiceData)
        {
            var uuid = Strings.HexOnly(record.Uuid);
            var bytes = Strings.HexToBytes(record.DataHex);
            if (bytes is null)
            {
                continue;
            }
            if (uuid == wantRest)
            {
                if (bytes.Length == SecondHalf)
                {
                    rest = bytes;
                }
                continue;
            }
            if (uuid != want128 && (want16 is null || uuid != want16))
            {
                continue;
            }
            switch (bytes.Length)
            {
                // Everything in the one advert.
                case HelpCodec.Length:
                    return bytes;

                // The first half of a message sent in two; the rest is under the other ID.
                case FirstHalf:
                    first = bytes;
                    break;
            }
        }

        // Both halves or nothing: half a message cannot be opened, and a guardian is told nothing rather than wrongly.
        return first is not null && rest is not null ? [.. first, .. rest] : null;
    }
}
