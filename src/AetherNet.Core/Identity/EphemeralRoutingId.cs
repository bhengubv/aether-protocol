// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AetherNet.Identity;

/// <summary>
/// Ephemeral Routing Id (ERID) — a rotating, key-derived wire address that is designed to
/// replace the stable, phone-derived UHID on the public wire.
///
/// <h3>The problem it solves</h3>
/// A node's UHID is <c>SHA-256(phone : deviceId : publicKey)</c> — stable for the life of the
/// install and carried in cleartext (<c>SourceUhid</c>/<c>DestinationUhid</c>) on every single
/// packet. A passive observer who never breaks any encryption can therefore (a) follow any node
/// indefinitely across time and place, and (b) — because the value is phone-derived — attempt to
/// confirm a suspected phone number by recomputing the hash. That is a surveillance and targeting
/// primitive, independent of the fact that message <em>contents</em> are end-to-end encrypted.
///
/// <h3>The design</h3>
/// The wire address becomes <c>ERID(epoch) = base32( HMAC-SHA256(routingKey, epoch) )[0..length]</c>:
/// <list type="bullet">
///   <item><description><b>routingKey is SECRET</b> — derived from the node's identity secret via
///     <see cref="DeriveRoutingKey"/>. It is NEVER derived from the public key: if it were, anyone
///     could recompute the whole schedule and unlinkability would be lost.</description></item>
///   <item><description><b>epoch = floor(unixSeconds / EpochSeconds)</b> — a 15-minute window by
///     default, matching the BLE-address / presence ephemeral-id rotation used elsewhere in the
///     stack. The ID changes every window.</description></item>
///   <item><description>To an outside observer two ERIDs from the same node in different epochs are
///     cryptographically uncorrelated — no cross-time linkage, no phone recovery.</description></item>
///   <item><description>A peer that needs to address the node learns its <c>routingKey</c> (or a
///     window of upcoming ERIDs) <em>inside</em> the established Signal session, so an existing
///     relationship resolves the rotating address while outsiders cannot.</description></item>
///   <item><description>Long-term identity (<see cref="AetherNetTag"/>) and reputation continuity
///     key off the identity revealed in-session — never off the rotating wire ERID.</description></item>
/// </list>
///
/// <h3>Migration</h3>
/// This is the protocol-level primitive only. Routing / DTN / reputation integration is intended to
/// ride <em>alongside</em> the existing UHID behind a negotiated capability until a two-node delivery
/// test proves the rotating path delivers; only then does the stable UHID leave the wire. Adding this
/// type changes nothing on its own.
/// </summary>
public static class EphemeralRoutingId
{
    // Same Crockford base-32 alphabet as AetherNetTag (no I/L/O/U — visually unambiguous).
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Default rotation window: 15 minutes, expressed in seconds.</summary>
    public const int DefaultEpochSeconds = 900;

    /// <summary>Default ERID length in base-32 characters (16 chars × 5 bits = 80 bits of entropy).</summary>
    public const int DefaultLength = 16;

    // Domain-separation label so a routing key can never collide with another key derived from
    // the same identity secret for a different purpose.
    private static readonly byte[] RoutingKeyInfo = Encoding.ASCII.GetBytes("aether-erid-routing-key-v1");

    /// <summary>
    /// Derives the 32-byte SECRET routing key from a node's identity secret (e.g. its Ed25519
    /// private-key bytes). Domain-separated via HKDF-SHA256. MUST be fed a secret — never a public
    /// value, or the rotation schedule becomes computable by anyone.
    /// </summary>
    public static byte[] DeriveRoutingKey(byte[] identitySecret)
    {
        ArgumentNullException.ThrowIfNull(identitySecret);
        if (identitySecret.Length == 0)
            throw new ArgumentException("identitySecret cannot be empty.", nameof(identitySecret));

        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: identitySecret,
            outputLength: 32,
            salt: null,
            info: RoutingKeyInfo);
    }

    /// <summary>The epoch (rotation-window index) that contains the given Unix time.</summary>
    public static long EpochFor(long unixSeconds, int epochSeconds = DefaultEpochSeconds)
    {
        if (epochSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(epochSeconds), "epochSeconds must be positive.");
        if (unixSeconds < 0) unixSeconds = 0;
        return unixSeconds / epochSeconds;
    }

    /// <summary>Derives the ERID for the epoch that contains <paramref name="unixSeconds"/>.</summary>
    public static string Derive(byte[] routingKey, long unixSeconds,
                                int epochSeconds = DefaultEpochSeconds, int length = DefaultLength)
        => DeriveForEpoch(routingKey, EpochFor(unixSeconds, epochSeconds), length);

    /// <summary>
    /// Derives the ERID for an explicit epoch number. The epoch is encoded big-endian so every
    /// language port produces byte-identical input to the HMAC.
    /// </summary>
    public static string DeriveForEpoch(byte[] routingKey, long epoch, int length = DefaultLength)
    {
        ArgumentNullException.ThrowIfNull(routingKey);
        if (routingKey.Length == 0)
            throw new ArgumentException("routingKey cannot be empty.", nameof(routingKey));
        if (length is < 1 or > 51)
            throw new ArgumentOutOfRangeException(nameof(length), "length must be 1..51 (SHA-256 is 256 bits = 51 base-32 chars).");

        Span<byte> epochBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(epochBytes, epoch);

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(routingKey, epochBytes, mac);

        return Base32(mac, length);
    }

    // Encode the first (length × 5) bits of data as Crockford base-32, most-significant bit first.
    private static string Base32(ReadOnlySpan<byte> data, int length)
    {
        Span<char> chars = stackalloc char[length];
        int bitPos = 0;
        for (int i = 0; i < length; i++)
        {
            int byteIndex = bitPos >> 3;
            int bitOffset = bitPos & 7;
            int hi = data[byteIndex];
            int lo = (byteIndex + 1 < data.Length) ? data[byteIndex + 1] : 0;
            int window = (hi << 8) | lo;                 // 16-bit window
            int val = (window >> (11 - bitOffset)) & 0x1F; // top 5 bits after the consumed offset
            chars[i] = Alphabet[val];
            bitPos += 5;
        }
        return new string(chars);
    }
}
