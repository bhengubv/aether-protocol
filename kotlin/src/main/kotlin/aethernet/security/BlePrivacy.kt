// SPDX-License-Identifier: MIT

package aethernet.security

import java.nio.ByteBuffer
import java.nio.ByteOrder
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * Bluetooth-LE tracking protection: a rotating Service UUID and IRK-based
 * Resolvable Private Addresses (RPA), so a mesh node is discoverable by its
 * peers without exposing a stable, trackable Bluetooth fingerprint on the air.
 *
 *  - The Service UUID rotates every 15 minutes, HMAC-SHA256-derived from a
 *    shared rotation key and the current time window. Every node in the same
 *    window derives the same UUID, so peers still find each other — but a
 *    passive scanner sees an identifier that changes and cannot be linked over
 *    time.
 *  - The node's stable id is removed from the advertisement; a peer that holds
 *    the node's 128-bit Identity Resolving Key (IRK) resolves its rotating
 *    6-byte RPA instead (the BLE "ah" function).
 *
 * The window-based operations are deterministic and byte-identical across every
 * AetherNet SDK (verified against fixtures/bleprivacy/vectors.json). The time
 * window is encoded as a little-endian int64.
 */
object BlePrivacy {
    /** Rotation period in seconds (15 minutes). */
    const val ROTATION_SECONDS = 900

    /** The rotation window index for a Unix-seconds timestamp. */
    fun windowFor(unixSeconds: Long): Long = unixSeconds / ROTATION_SECONDS

    /**
     * The rotating BLE Service UUID for a rotation key and time window. Every
     * node sharing the rotation key derives the same UUID within the window,
     * enabling mutual discovery with no static identifier on the air.
     */
    fun serviceUuid(rotationKey: ByteArray, window: Long): String {
        val mac = hmacSha256(rotationKey, windowBytes(window))
        return formatUuid(mac.copyOf(16))
    }

    /**
     * A 6-byte Resolvable Private Address for a 16-byte IRK and time window:
     * `hash(3) || prand(3)`, where prand is HMAC-derived (with the RPA
     * address-type bits set) and hash = AES-128(IRK, prand-block). Rotates every
     * window; only a peer holding the IRK can link successive addresses.
     */
    fun resolvableAddress(irk: ByteArray, window: Long): ByteArray {
        require(irk.size == 16) { "IRK must be 16 bytes." }

        val prand = hmacSha256(irk, windowBytes(window)).copyOf(3)
        prand[0] = ((prand[0].toInt() and 0x3F) or 0x40).toByte() // RPA address-type bits (0b01)

        val hash = ah(irk, prand)

        val rpa = ByteArray(6)
        System.arraycopy(hash, 0, rpa, 0, 3)
        System.arraycopy(prand, 0, rpa, 3, 3)
        return rpa
    }

    /**
     * True if [rpa] was generated from [irk] — i.e. this node recognises the
     * peer behind the rotating address.
     */
    fun resolveAddress(irk: ByteArray, rpa: ByteArray): Boolean {
        if (irk.size != 16 || rpa.size != 6) return false

        val prand = rpa.copyOfRange(3, 6)
        val hash = ah(irk, prand)
        return hash.copyOf(3).contentEquals(rpa.copyOf(3))
    }

    // BLE "ah" hash: AES-128-ECB(irk, 0^13 || prand), keep the first 3 bytes.
    private fun ah(irk: ByteArray, prand: ByteArray): ByteArray {
        val block = ByteArray(16)
        System.arraycopy(prand, 0, block, 13, 3)

        val cipher = Cipher.getInstance("AES/ECB/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(irk, "AES"))
        val ct = cipher.doFinal(block)
        return ct.copyOf(3)
    }

    private fun hmacSha256(key: ByteArray, data: ByteArray): ByteArray {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        return mac.doFinal(data)
    }

    private fun windowBytes(window: Long): ByteArray =
        ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(window).array()

    private fun formatUuid(b: ByteArray): String {
        fun hex(from: Int, to: Int): String {
            val sb = StringBuilder((to - from) * 2)
            for (i in from until to) sb.append("%02x".format(b[i].toInt() and 0xFF))
            return sb.toString()
        }
        return "${hex(0, 4)}-${hex(4, 6)}-${hex(6, 8)}-${hex(8, 10)}-${hex(10, 16)}"
    }
}
