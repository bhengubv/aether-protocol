// SPDX-License-Identifier: MIT

package aethernet.identity

/**
 * Derives a libp2p **PeerID** from a node's Ed25519 public key — the bridge between an AetherNet
 * identity and the global libp2p relay / DHT used by the decentralised relay layer.
 *
 * Because AetherNet and libp2p both key identity off the same Ed25519 public key, the PeerID is a
 * *pure, deterministic* function of that key — no lookup table, no network. A node can compute its
 * own PeerID (to announce on the libp2p DHT) and any peer's PeerID (to dial it) from the public key
 * alone.
 *
 * ## Encoding (must be byte-identical across every SDK language)
 *  1. **protobuf PublicKey** = `08 01` (field 1 Type = Ed25519) `12 20` (field 2 Data, length 32)
 *     followed by the 32-byte key — 36 bytes total.
 *  2. **identity multihash** = `00` (identity hash code) `24` (length 36) followed by the protobuf —
 *     38 bytes. libp2p uses the identity multihash for keys whose serialized form is <= 42 bytes,
 *     which Ed25519 always is.
 *  3. **PeerID string** = base58btc (Bitcoin alphabet) of the 38-byte multihash, WITHOUT a multibase
 *     prefix. Always renders as `12D3Koo…` for Ed25519.
 *
 * Verified byte-for-byte against real `js-libp2p` output; see `fixtures/peerid/`.
 *
 * NOTE: Kotlin bytes are signed; every byte is masked with `and 0xFF` before it is treated as an
 * unsigned value (matching [AetherNetTag]).
 */
object PeerId {

    /** Bitcoin base58 alphabet (no 0, O, I, l). */
    private const val BASE58_ALPHABET = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"

    /**
     * identity-multihash(code 0x00, len 0x24 = 36) || protobuf PublicKey
     * (type Ed25519: 0x08 0x01; data len 32: 0x12 0x20).
     */
    private val ED25519_PREFIX = byteArrayOf(0x00, 0x24, 0x08, 0x01, 0x12, 0x20)

    /** Length in bytes of a raw Ed25519 public key. */
    const val ED25519_PUBLIC_KEY_LENGTH = 32

    /**
     * Returns the libp2p PeerID string (e.g. `12D3Koo…`) for a 32-byte Ed25519 public key.
     *
     * @throws IllegalArgumentException if [publicKey] is not exactly 32 bytes.
     */
    fun fromEd25519PublicKey(publicKey: ByteArray): String {
        require(publicKey.size == ED25519_PUBLIC_KEY_LENGTH) {
            "Ed25519 public key must be $ED25519_PUBLIC_KEY_LENGTH bytes, got ${publicKey.size}."
        }

        val multihash = ByteArray(ED25519_PREFIX.size + ED25519_PUBLIC_KEY_LENGTH)
        System.arraycopy(ED25519_PREFIX, 0, multihash, 0, ED25519_PREFIX.size)
        System.arraycopy(publicKey, 0, multihash, ED25519_PREFIX.size, ED25519_PUBLIC_KEY_LENGTH)
        return base58Encode(multihash)
    }

    /**
     * Standard base58 (bitcoinj algorithm) — counts leading zero bytes and reproduces them as
     * leading '1's, then converts the big-endian base-256 number to base-58 by repeated divmod.
     */
    private fun base58Encode(input: ByteArray): String {
        if (input.isEmpty()) return ""

        var zeros = 0
        while (zeros < input.size && input[zeros].toInt() and 0xFF == 0) zeros++

        val buffer = input.copyOf() // divmod mutates in place
        val encoded = CharArray(input.size * 2) // safe upper bound
        var outputStart = encoded.size

        var inputStart = zeros
        while (inputStart < buffer.size) {
            encoded[--outputStart] = BASE58_ALPHABET[divmod58(buffer, inputStart)]
            if (buffer[inputStart].toInt() and 0xFF == 0) inputStart++ // a digit fully consumed
        }
        // Drop extra leading '1's the loop may have produced.
        while (outputStart < encoded.size && encoded[outputStart] == BASE58_ALPHABET[0]) outputStart++
        // Re-add one '1' per leading zero byte of the input.
        while (zeros > 0) {
            encoded[--outputStart] = BASE58_ALPHABET[0]
            zeros--
        }

        return String(encoded, outputStart, encoded.size - outputStart)
    }

    /**
     * Divides the big-endian base-256 number in `number[firstDigit until size]` by 58, in place,
     * returning the remainder. Every byte is masked with `and 0xFF` so the signed Kotlin byte is
     * treated as an unsigned 0–255 value.
     */
    private fun divmod58(number: ByteArray, firstDigit: Int): Int {
        var remainder = 0
        for (i in firstDigit until number.size) {
            val digit = number[i].toInt() and 0xFF
            val temp = remainder * 256 + digit
            number[i] = (temp / 58).toByte()
            remainder = temp % 58
        }
        return remainder
    }
}
