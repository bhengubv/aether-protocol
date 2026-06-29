// SPDX-License-Identifier: MIT

using System;

namespace AetherNet.Identity;

/// <summary>
/// Derives a libp2p <b>PeerID</b> from a node's Ed25519 public key — the bridge between an
/// AetherNet identity and the global libp2p relay / DHT used by the decentralised relay layer.
///
/// <para>Because AetherNet and libp2p both key identity off the same Ed25519 public key, the PeerID
/// is a <em>pure, deterministic</em> function of that key — no lookup table, no network. A node can
/// compute its own PeerID (to announce on the libp2p DHT) and any peer's PeerID (to dial it) from
/// the public key alone.</para>
///
/// <h3>Encoding (must be byte-identical across every SDK language)</h3>
/// <list type="number">
///   <item><description><b>protobuf PublicKey</b> = <c>08 01</c> (field 1 Type = Ed25519) <c>12 20</c>
///     (field 2 Data, length 32) followed by the 32-byte key — 36 bytes total.</description></item>
///   <item><description><b>identity multihash</b> = <c>00</c> (identity hash code) <c>24</c> (length 36)
///     followed by the protobuf — 38 bytes. libp2p uses the identity multihash for keys whose
///     serialized form is ≤ 42 bytes, which Ed25519 always is.</description></item>
///   <item><description><b>PeerID string</b> = base58btc (Bitcoin alphabet) of the 38-byte multihash,
///     WITHOUT a multibase prefix. Always renders as <c>12D3Koo…</c> for Ed25519.</description></item>
/// </list>
///
/// Verified byte-for-byte against real <c>js-libp2p</c> output; see <c>fixtures/peerid/</c>.
/// </summary>
public static class PeerId
{
    // Bitcoin base58 alphabet (no 0, O, I, l).
    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    // identity-multihash(code 0x00, len 0x24=36) || protobuf PublicKey(type Ed25519: 0x08 0x01; data len 32: 0x12 0x20)
    private static readonly byte[] Ed25519Prefix = { 0x00, 0x24, 0x08, 0x01, 0x12, 0x20 };

    /// <summary>Length in bytes of a raw Ed25519 public key.</summary>
    public const int Ed25519PublicKeyLength = 32;

    /// <summary>
    /// Returns the libp2p PeerID string (e.g. <c>12D3Koo…</c>) for a 32-byte Ed25519 public key.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="publicKey"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="publicKey"/> is not exactly 32 bytes.</exception>
    public static string FromEd25519PublicKey(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (publicKey.Length != Ed25519PublicKeyLength)
            throw new ArgumentException($"Ed25519 public key must be {Ed25519PublicKeyLength} bytes, got {publicKey.Length}.", nameof(publicKey));

        var multihash = new byte[Ed25519Prefix.Length + Ed25519PublicKeyLength];
        Buffer.BlockCopy(Ed25519Prefix, 0, multihash, 0, Ed25519Prefix.Length);
        Buffer.BlockCopy(publicKey, 0, multihash, Ed25519Prefix.Length, Ed25519PublicKeyLength);
        return Base58Encode(multihash);
    }

    // Standard base58 (bitcoinj algorithm) — preserves leading zero bytes as leading '1's.
    private static string Base58Encode(byte[] input)
    {
        if (input.Length == 0) return string.Empty;

        int zeros = 0;
        while (zeros < input.Length && input[zeros] == 0) zeros++;

        var buffer = (byte[])input.Clone(); // divmod mutates in place
        var encoded = new char[input.Length * 2]; // safe upper bound
        int outputStart = encoded.Length;

        for (int inputStart = zeros; inputStart < buffer.Length;)
        {
            encoded[--outputStart] = Base58Alphabet[DivMod(buffer, inputStart, 256, 58)];
            if (buffer[inputStart] == 0) inputStart++; // a digit fully consumed
        }
        // Drop extra leading '1's the loop may have produced.
        while (outputStart < encoded.Length && encoded[outputStart] == Base58Alphabet[0]) outputStart++;
        // Re-add one '1' per leading zero byte of the input.
        for (; zeros > 0; zeros--) encoded[--outputStart] = Base58Alphabet[0];

        return new string(encoded, outputStart, encoded.Length - outputStart);
    }

    // Divides the big-endian base-256 number in number[firstDigit..] by 58, in place, returns the remainder.
    private static int DivMod(byte[] number, int firstDigit, int baseIn, int baseOut)
    {
        int remainder = 0;
        for (int i = firstDigit; i < number.Length; i++)
        {
            int digit = number[i] & 0xFF;
            int temp = remainder * baseIn + digit;
            number[i] = (byte)(temp / baseOut);
            remainder = temp % baseOut;
        }
        return remainder;
    }
}
