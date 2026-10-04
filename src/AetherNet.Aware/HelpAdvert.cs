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
        _ => throw new ArgumentOutOfRangeException(nameof(form)),
    };

    /// <summary>Whether this form fits a standard advert, which every phone can send and hear.</summary>
    public static bool FitsLegacyAdvert(HelpAdvertForm form) => Size(form) <= LegacyAdvertBytes;

    /// <summary>
    /// The advert bytes for a help message. <paramref name="registeredId"/> is the registered 16-bit service ID and is
    /// needed only by <see cref="HelpAdvertForm.Registered16"/>; without one that form throws, because an advert under
    /// an ID nobody assigned us could collide with another product's.
    /// </summary>
    public static byte[] Build(HelpAdvertForm form, ReadOnlySpan<byte> message, ushort? registeredId = null)
    {
        if (message.Length != HelpCodec.Length)
        {
            throw new ArgumentException($"A help message is {HelpCodec.Length} bytes.", nameof(message));
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
                // A 128-bit UUID goes out least-significant byte first.
                var be = ServiceUuid.ToByteArray(bigEndian: true);
                for (var i = 0; i < 16; i++)
                {
                    advert[5 + i] = be[15 - i];
                }
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
        var want16 = registeredId is { } id ? id.ToString("X4", System.Globalization.CultureInfo.InvariantCulture) : null;
        foreach (var record in facts.ServiceData)
        {
            var uuid = Strings.HexOnly(record.Uuid);
            if (uuid != want128 && (want16 is null || uuid != want16))
            {
                continue;
            }
            if (Strings.HexToBytes(record.DataHex) is { Length: HelpCodec.Length } message)
            {
                return message;
            }
        }
        return null;
    }
}
