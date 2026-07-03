// SPDX-License-Identifier: MIT

package aethernet.security

import java.security.MessageDigest
import java.security.SecureRandom

/**
 * Panic-wipe: the identity-erasure core of an AetherNet node's duress defence.
 * A duress PIN (or panic button) irreversibly destroys the node's key material,
 * so a seized device reveals nothing and looks like a fresh install.
 *
 * This object is the protocol-level core — deterministic and portable across
 * every AetherNet SDK:
 *  - [duressPinHash] / [verifyDuressPin] — recognise the duress PIN (SHA-256,
 *    constant-time compare); the PIN itself is never stored.
 *  - [secureErase] — best-effort in-memory erase of key material (overwrite with
 *    random, then zero).
 *  - [IDENTITY_KEY_NAMES] + [preKeyName] / [signedPreKeyName] — the canonical set
 *    of key-store entries a wipe must destroy.
 *
 * Destroying the hosting app's local database, platform keychain entries and any
 * decoy store is the app's job — it owns that storage. This object gives the app
 * the crypto trigger, the secure-erase primitive, and the manifest of what to
 * remove, so every app wipes the same identity material the same way.
 *
 * The hash and name operations are byte-identical across every AetherNet SDK
 * (verified against fixtures/panicwipe/vectors.json).
 */
object PanicWipe {
    /** Number of one-time / signed pre-key slots a wipe sweeps (0..N-1). */
    const val MAX_PRE_KEYS = 200

    /**
     * The key-store entry names that together constitute an AetherNet identity —
     * everything a panic-wipe must destroy, besides the numbered pre-keys.
     */
    val IDENTITY_KEY_NAMES: List<String> = listOf(
        "aether_identity_pub",
        "aether_identity_priv",
        "aether_identity_generated",
        "aether_device_salt",
        "aether_drk",
        "aether_ble_rotation_key",
        "aether_ble_irk",
    )

    /** Key-store name of the i-th one-time pre-key. */
    fun preKeyName(index: Int): String = "prekey_$index"

    /** Key-store name of the i-th signed pre-key. */
    fun signedPreKeyName(index: Int): String = "signed_prekey_$index"

    /**
     * The duress-PIN hash: SHA-256 of the UTF-8 PIN. Stored at setup and compared
     * on unlock — the PIN is only ever kept as this hash.
     */
    fun duressPinHash(pin: String): ByteArray =
        MessageDigest.getInstance("SHA-256").digest(pin.toByteArray(Charsets.UTF_8))

    /**
     * Constant-time check of whether [pin] matches a stored [duressPinHash] — i.e.
     * whether unlocking should trigger a wipe. Returns false for a hash that is
     * not 32 bytes.
     */
    fun verifyDuressPin(pin: String, storedHash: ByteArray): Boolean {
        if (storedHash.size != 32) return false
        return MessageDigest.isEqual(duressPinHash(pin), storedHash)
    }

    /**
     * Best-effort secure erase of in-memory key material: overwrite with random
     * bytes, then zero. Call on every buffer holding a secret before releasing it.
     * Defence in depth — the runtime or OS may still hold copies, but this removes
     * the obvious one and leaves no plaintext secret in the buffer.
     */
    fun secureErase(buffer: ByteArray) {
        if (buffer.isEmpty()) return
        SecureRandom().nextBytes(buffer)
        buffer.fill(0)
    }
}
