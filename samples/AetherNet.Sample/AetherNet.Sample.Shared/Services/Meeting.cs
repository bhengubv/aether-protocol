// SPDX-License-Identifier: MIT

using System.Security.Cryptography;
using System.Text;

namespace AetherNet.Sample.Shared.Services;

/// <summary>
/// Two phones agreeing where to meet, before either radio has done anything.
///
/// <para>
/// <b>Why this is not a radio's business.</b> Every radio had invented its own answer to the same
/// three questions — who starts, how do we find each other, which channel do we agree on. Wi-Fi
/// Direct compared AetherTags and derived a group from a public key. BLE picked its roles from what
/// the silicon could do and never looked at a tag at all. LoRa would have been a third answer. Three
/// answers to one question, none of them shared, and the only tag-aware one bolted to a single radio
/// — so a phone with no Wi-Fi Direct could not meet anybody, and the whole thing collapsed when that
/// one radio could not finish.
/// </para>
///
/// <para>
/// So the question is answered once, here, above every radio. What comes out is small enough for any
/// of them to carry: who you are meeting, a value you both derived independently, and which of you
/// starts. What a radio does with it is the radio's business — Wi-Fi Direct makes it a group name,
/// LoRa an address inside a shared channel, BLE a service id, NFC ignores it because the tap is the
/// meeting.
/// </para>
///
/// <para>
/// <b>Derived from the two tags, and nothing else.</b> Both phones hold both tags the moment somebody
/// has been added — typed, scanned or tapped — so both can work this out before they have ever been
/// in the same room. It is not a secret and is not doing the work of one: anyone holding both tags
/// can compute it, and holding both tags means having been given both. Everything that crosses the
/// link is sealed above it, so meeting somewhere guessable buys an intruder the ability to carry
/// other people's ciphertext and nothing else — the same trust boundary as adding a contact.
/// </para>
///
/// <para>
/// This is only how strangers meet the first time. It carries one thing — the public key, which is
/// checked against the tag it claims to belong to — and after that the pair go back to deriving from
/// that key, which nobody can compute without having been given it.
/// </para>
/// </summary>
/// <param name="PeerTag">Whose tag this meeting is with.</param>
/// <param name="Rendezvous">
///   Where to meet: letters and digits, the same on both phones, long enough for a radio to take as
///   much as it can use and ignore the rest.
/// </param>
/// <param name="IStart">
///   Whether this phone is the one that opens — creates the group, transmits first, whatever opening
///   means on the radio in question. Decided by ordering the two tags, so both work out the same
///   answer without a word passing between them.
/// </param>
public readonly record struct Meeting(string PeerTag, string Rendezvous, bool IStart)
{
    /// <summary>Ties this derivation to this purpose, so the same tags used elsewhere yield nothing here.</summary>
    private const string Info = "aether-meeting-v1";

    /// <summary>
    /// Crockford's alphabet: no I, L, O or U, so it cannot be misread down a phone line and cannot
    /// accidentally spell anything.
    /// </summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>How many characters a rendezvous carries.</summary>
    /// <remarks>
    /// Longer than the widest radio needs, so a radio takes a prefix and none of them has to agree
    /// with the others about length. Wi-Fi Direct uses nine; LoRa will use far fewer.
    /// </remarks>
    public const int Length = 25;

    /// <summary>
    /// Work out where two phones meet, from their tags alone.
    /// </summary>
    /// <returns>The meeting, or null when either tag is missing or they are the same phone.</returns>
    public static Meeting? With(string? myTag, string? theirTag)
    {
        if (string.IsNullOrWhiteSpace(myTag) || string.IsNullOrWhiteSpace(theirTag)) return null;
        if (string.Equals(myTag, theirTag, StringComparison.OrdinalIgnoreCase)) return null;

        // Ordered, so the two phones feed the derivation the same bytes in the same order. Fed in the
        // order each phone happens to hold them, they would land on two different rendezvous and each
        // would sit waiting at a place the other had never heard of.
        var (first, second) = string.CompareOrdinal(myTag, theirTag) < 0
            ? (myTag, theirTag)
            : (theirTag, myTag);

        Span<byte> derived = stackalloc byte[16];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(first + "\n" + second),
            derived,
            salt: ReadOnlySpan<byte>.Empty,
            info: Encoding.UTF8.GetBytes(Info));

        return new Meeting(theirTag, Encode(derived)[..Length], GroupRole.HostsTheGroup(myTag, theirTag));
    }

    /// <summary>As much of the rendezvous as a radio can use, from the front.</summary>
    /// <remarks>
    /// A radio takes what fits rather than being handed a length it has to argue with — a Wi-Fi Direct
    /// network name has room for nine characters after its mandatory prefix, and a LoRa address has
    /// room for a handful of bits.
    /// </remarks>
    public string Where(int characters) =>
        characters <= 0 ? "" :
        characters >= Rendezvous.Length ? Rendezvous : Rendezvous[..characters];

    /// <summary>
    /// The meeting as a UUID, for a radio that finds people by advertising one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bluetooth is one of those: a peripheral advertises a service id and a central scans for it, and
    /// the scan filter is the whole of the matching. Advertising one fixed id for the whole app means
    /// every phone running it answers every other — which is discovery, and discovery is the thing
    /// that must not happen. Advertising the meeting means only the person whose tag you were handed
    /// can be seen, and only by them.
    /// </para>
    /// <para>
    /// Version and variant bits are set, so this is a well-formed random-looking UUID rather than
    /// something a Bluetooth stack might refuse.
    /// </para>
    /// </remarks>
    public Guid Uuid()
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Info + "-uuid\n" + Rendezvous))[..16];

        // Byte 7, not byte 6. .NET stores the first three fields of a Guid little-endian, so the
        // octet RFC 4122 calls "position 6" — the one carrying the version — is index 7 here. Setting
        // index 6 puts the version somewhere harmless and leaves the real one whatever the hash said,
        // which is a UUID that mostly works and occasionally is refused.
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x40);   // version 4
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // variant 1

        return new Guid(bytes);
    }

    /// <summary>
    /// The meeting as a small number, for a radio whose address space is tiny.
    /// </summary>
    /// <param name="bits">How many bits that radio has to spare, 1 to 32.</param>
    /// <remarks>
    /// LoRa is the case: a handful of frequencies and a short address inside them, nothing like the
    /// room a network name has. So a radio takes what it can hold — and accepts that two pairs can
    /// collide in a small space, which costs a dropped frame and never a wrong link, because what
    /// arrives is still checked against a key.
    /// </remarks>
    public uint Address(int bits)
    {
        if (bits is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(bits));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Info + "-addr\n" + Rendezvous));
        var whole = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash);

        return bits == 32 ? whole : whole & ((1u << bits) - 1);
    }

    /// <summary>Bytes as Crockford base32, five bits at a time.</summary>
    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length * 8 / 5];
        var bit = 0;

        for (var i = 0; i < chars.Length; i++)
        {
            var value = 0;
            for (var b = 0; b < 5; b++, bit++)
            {
                var source = bytes[bit / 8];
                var taken = (source >> (7 - (bit % 8))) & 1;
                value = (value << 1) | taken;
            }

            chars[i] = Alphabet[value];
        }

        return new string(chars);
    }
}
