// SPDX-License-Identifier: MIT

package aethernet.identity

import java.security.MessageDigest

/**
 * A human-readable, shareable identity address derived from a node's 32-byte Ed25519 public key.
 *
 * Algorithm:
 *   SHA-256(publicKey) → first 50 bits → Crockford base-32 → "XXXXX-XXXXX"
 *
 * The Crockford base-32 alphabet ("0123456789ABCDEFGHJKMNPQRSTVWXYZ") omits I, L, O, U
 * to reduce visual ambiguity.
 *
 * The 50-bit value is extracted from hash bytes 0–6:
 *   bits = (b0 << 42) | (b1 << 34) | (b2 << 26) | (b3 << 18) | (b4 << 10) | (b5 << 2)
 *        | ((b6 >>> 6) & 0x3)
 * where each byte is treated as unsigned (0–255).
 *
 * Example output: "KXJB7-MN2P4"
 */
data class AetherNetTag(val value: String) {

    val isValid: Boolean get() = value.isNotEmpty()

    override fun toString(): String = value

    companion object {

        private const val ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
        private val VALID_PATTERN = Regex("^[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$")

        /**
         * Derives an AetherNetTag from a 32-byte Ed25519 public key.
         */
        fun fromPublicKey(publicKey: ByteArray): AetherNetTag {
            require(publicKey.size == 32) {
                "Public key must be exactly 32 bytes, got ${publicKey.size}"
            }

            val digest = MessageDigest.getInstance("SHA-256")
            val hash = digest.digest(publicKey)

            // Extract 50 bits from bytes 0–6 treating every byte as unsigned.
            // Each byte must be masked with 0xFF before shifting to prevent sign extension.
            val bits: Long =
                (hash[0].toInt().and(0xFF).toLong() shl 42) or
                (hash[1].toInt().and(0xFF).toLong() shl 34) or
                (hash[2].toInt().and(0xFF).toLong() shl 26) or
                (hash[3].toInt().and(0xFF).toLong() shl 18) or
                (hash[4].toInt().and(0xFF).toLong() shl 10) or
                (hash[5].toInt().and(0xFF).toLong() shl  2) or
                (hash[6].toInt().ushr(6).and(0x3).toLong())

            // Decode into 10 Crockford base-32 characters (5 bits each, MSB first).
            val chars = CharArray(10)
            for (i in 0..9) {
                val shift = (9 - i) * 5
                val index = ((bits ushr shift) and 0x1FL).toInt()
                chars[i] = ALPHABET[index]
            }

            val tag = "${String(chars, 0, 5)}-${String(chars, 5, 5)}"
            return AetherNetTag(tag)
        }

        /**
         * Parses a tag string in any supported format:
         *   - "XXXXX-XXXXX"  (canonical with separator)
         *   - "XXXXXXXXXX"   (10 chars, no separator)
         *   - lowercase accepted; canonicalized to uppercase
         *
         * @throws IllegalArgumentException if the string is not a valid AetherNetTag.
         */
        fun parse(tag: String): AetherNetTag {
            val upper = tag.uppercase()
            val canonical = when (upper.length) {
                11 -> upper  // already "XXXXX-XXXXX"
                10 -> "${upper.substring(0, 5)}-${upper.substring(5)}"  // insert dash
                else -> throw IllegalArgumentException(
                    "Invalid AetherNetTag format: expected 10 or 11 characters (with optional '-'), got \"$tag\""
                )
            }
            if (!VALID_PATTERN.matches(canonical)) {
                throw IllegalArgumentException(
                    "Invalid AetherNetTag: contains characters outside the Crockford base-32 alphabet: \"$tag\""
                )
            }
            return AetherNetTag(canonical)
        }

        /**
         * Attempts to parse a tag string; returns null if the string is not valid.
         */
        fun tryParse(tag: String): AetherNetTag? = try {
            parse(tag)
        } catch (_: IllegalArgumentException) {
            null
        }

        /**
         * Returns true if [tag] is a valid AetherNetTag string that matches the given public key.
         */
        fun verify(tag: String, publicKey: ByteArray): Boolean {
            val parsed = tryParse(tag) ?: return false
            return try {
                fromPublicKey(publicKey) == parsed
            } catch (_: IllegalArgumentException) {
                false
            }
        }
    }
}
