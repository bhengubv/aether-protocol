// SPDX-License-Identifier: MIT

package aethernet.sync

import aethernet.security.Ed25519Service
import java.io.ByteArrayOutputStream

/**
 * A signed device-membership record. A user links a new device by having their
 * long-term Ed25519 identity key sign the new device's own public key; every
 * other device verifies that signature to admit the newcomer into the "self"
 * device set — no central directory, no server. Because Ed25519 signatures are
 * deterministic, the serialized record is byte-identical across SDKs.
 *
 * @property deviceId The linked device's identifier.
 * @property devicePublicKey The device's own 32-byte Ed25519 public key.
 * @property issuedAtMs When the link was issued (Unix ms).
 * @property signature 64-byte Ed25519 signature by the user's identity key over the signed body.
 */
data class DeviceLink(
    val deviceId: String,
    val devicePublicKey: ByteArray,
    val issuedAtMs: Long,
    val signature: ByteArray,
) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is DeviceLink) return false
        return deviceId == other.deviceId &&
            devicePublicKey.contentEquals(other.devicePublicKey) &&
            issuedAtMs == other.issuedAtMs &&
            signature.contentEquals(other.signature)
    }

    override fun hashCode(): Int {
        var result = deviceId.hashCode()
        result = 31 * result + devicePublicKey.contentHashCode()
        result = 31 * result + issuedAtMs.hashCode()
        result = 31 * result + signature.contentHashCode()
        return result
    }
}

/** Serializes, signs and verifies [DeviceLink] records. */
object DeviceLinkCodec {
    /** Wire format version; readers reject any other value. */
    const val FORMAT_VERSION: Int = 0x01

    /**
     * The canonical signed body (everything but the signature): version ·
     * device_id(u16 len + utf8) · device_public_key(32) · issued_at_ms(i64 LE).
     * Signer and verifier operate over exactly these bytes.
     */
    fun signedBody(deviceId: String, devicePublicKey: ByteArray, issuedAtMs: Long): ByteArray {
        require(devicePublicKey.size == 32) { "Device public key must be 32 bytes." }
        val id = deviceId.toByteArray(Charsets.UTF_8)
        require(id.size <= 0xFFFF) { "DeviceId is too long." }

        val out = ByteArrayOutputStream(1 + 2 + id.size + 32 + 8)
        out.write(FORMAT_VERSION)
        writeU16(out, id.size)
        out.write(id)
        out.write(devicePublicKey)
        writeI64(out, issuedAtMs)
        return out.toByteArray()
    }

    /** Creates a device-link signed by the user's 32-byte Ed25519 identity private key. */
    fun create(deviceId: String, devicePublicKey: ByteArray, issuedAtMs: Long, identityPrivateKey: ByteArray): DeviceLink {
        val body = signedBody(deviceId, devicePublicKey, issuedAtMs)
        val signature = Ed25519Service.sign(identityPrivateKey, body)
        return DeviceLink(deviceId, devicePublicKey, issuedAtMs, signature)
    }

    /**
     * True if [link] was signed by the identity behind [identityPublicKey] — i.e.
     * this device belongs to that user.
     */
    fun verify(link: DeviceLink, identityPublicKey: ByteArray): Boolean {
        if (link.signature.size != 64) return false
        if (link.devicePublicKey.size != 32) return false
        val body = signedBody(link.deviceId, link.devicePublicKey, link.issuedAtMs)
        return Ed25519Service.verify(identityPublicKey, body, link.signature)
    }

    /** Serializes a link as its signed body followed by the 64-byte signature. */
    fun serialize(link: DeviceLink): ByteArray {
        require(link.signature.size == 64) { "Signature must be 64 bytes." }
        val body = signedBody(link.deviceId, link.devicePublicKey, link.issuedAtMs)
        val out = ByteArrayOutputStream(body.size + 64)
        out.write(body)
        out.write(link.signature)
        return out.toByteArray()
    }

    /** Parses a serialized link, validating framing. */
    fun deserialize(data: ByteArray): DeviceLink {
        require(data.size >= 1 + 2 + 32 + 8 + 64) { "DeviceLink is too short." }
        var o = 0
        require((data[o++].toInt() and 0xff) == FORMAT_VERSION) { "Unsupported DeviceLink format version." }

        val idLen = (data[o].toInt() and 0xff) or ((data[o + 1].toInt() and 0xff) shl 8)
        o += 2
        require(o + idLen + 32 + 8 + 64 <= data.size) { "DeviceLink is truncated." }
        val deviceId = String(data, o, idLen, Charsets.UTF_8)
        o += idLen
        val devicePublicKey = data.copyOfRange(o, o + 32)
        o += 32
        var issuedAtMs = 0L
        for (i in 0 until 8) {
            issuedAtMs = issuedAtMs or ((data[o + i].toLong() and 0xff) shl (8 * i))
        }
        o += 8
        val signature = data.copyOfRange(o, o + 64)
        return DeviceLink(deviceId, devicePublicKey, issuedAtMs, signature)
    }

    // ── primitives ──

    private fun writeU16(out: ByteArrayOutputStream, v: Int) {
        out.write(v and 0xff)
        out.write((v ushr 8) and 0xff)
    }

    private fun writeI64(out: ByteArrayOutputStream, v: Long) {
        var x = v
        for (i in 0 until 8) {
            out.write((x and 0xff).toInt())
            x = x ushr 8
        }
    }
}
