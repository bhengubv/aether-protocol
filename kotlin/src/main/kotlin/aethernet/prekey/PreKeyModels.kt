// SPDX-License-Identifier: MIT

package aethernet.prekey

import aethernet.security.PreKeyBundle
import aethernet.voice.JsonReader
import java.util.Base64
import java.util.UUID

/**
 * JSON payload for [aethernet.protocol.PacketType.PreKeyRequest] (25) — a directed ask for a peer's
 * published [PreKeyBundle] so the requester can start an X3DH session while the peer is offline. This
 * is the mesh TRANSPORT of the request; no key agreement happens here.
 *
 * Wire format: UTF-8 JSON, field order request_id, requester_uhid, NO whitespace, lowercase-dashed
 * UUID (Java's UUID.toString()). Byte-identity gate: fixtures/prekey/vectors.json — must be
 * byte-identical with C# in every port.
 *
 * Wire vector (fixtures/prekey/vectors.json "request"):
 *  - requestId=11112222-3333-4444-5555-666677778888, requesterUhid="aether:alice:01" →
 *    {"request_id":"11112222-3333-4444-5555-666677778888","requester_uhid":"aether:alice:01"}
 */
data class PreKeyRequestPayload(
    /** Correlation id minted by the requester; echoed in the response. */
    val requestId: UUID,
    /** UHID of the node asking for the bundle — where the response is sent. */
    val requesterUhid: String
) {
    /**
     * Serialize to the canonical UTF-8 JSON wire bytes. Built by hand (no kotlinx.serialization —
     * AOSP Soong forbids it), the same manual string-building approach used by the videocall / SOS /
     * channels payload encoders. snake_case keys, field order request_id, requester_uhid, NO
     * whitespace, UUID lowercase-dashed (Java's UUID.toString()).
     */
    fun toJsonBytes(): ByteArray {
        val sb = StringBuilder()
        sb.append('{')
        sb.append("\"request_id\":\"").append(requestId).append("\",")
        sb.append("\"requester_uhid\":\"").append(jsonEscape(requesterUhid)).append('"')
        sb.append('}')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }

    companion object {
        /** Parse from canonical JSON. Returns null on a missing/malformed request_id. */
        fun fromJson(json: String): PreKeyRequestPayload? {
            val requestId = JsonReader.readString(json, "request_id")?.let {
                runCatching { UUID.fromString(it) }.getOrNull()
            } ?: return null
            val requesterUhid = JsonReader.readString(json, "requester_uhid") ?: ""
            return PreKeyRequestPayload(requestId = requestId, requesterUhid = requesterUhid)
        }
    }
}

/**
 * JSON payload for [aethernet.protocol.PacketType.PreKeyResponse] (26) — the responder's published
 * [PreKeyBundle] carried back to the requester. All public-key material is STANDARD base64 (RFC 4648,
 * '+/' alphabet, '=' padding), matching System.Text.Json's byte[] default in the C# reference.
 *
 * Field order (pinned): request_id, uhid, identity_key, identity_key_x25519, pre_key_id, pre_key,
 * signed_pre_key_id, signed_pre_key, signed_pre_key_signature. Integer ids are bare. NO whitespace.
 * Byte-identity gate: fixtures/prekey/vectors.json — must be byte-identical with C# in every port.
 */
data class PreKeyResponsePayload(
    val requestId: UUID,
    val uhid: String,
    val identityKey: ByteArray,
    val identityKeyX25519: ByteArray,
    val preKeyId: Int,
    val preKey: ByteArray,
    val signedPreKeyId: Int,
    val signedPreKey: ByteArray,
    val signedPreKeySignature: ByteArray
) {
    /**
     * Serialize to the canonical UTF-8 JSON wire bytes. Hand-built (no kotlinx.serialization). Field
     * order matches the C# JsonPropertyName pinning; every ByteArray is STANDARD base64
     * (Base64.getEncoder()); ids are bare integers; NO whitespace.
     */
    fun toJsonBytes(): ByteArray {
        val sb = StringBuilder()
        sb.append('{')
        sb.append("\"request_id\":\"").append(requestId).append("\",")
        sb.append("\"uhid\":\"").append(jsonEscape(uhid)).append("\",")
        sb.append("\"identity_key\":\"").append(base64(identityKey)).append("\",")
        sb.append("\"identity_key_x25519\":\"").append(base64(identityKeyX25519)).append("\",")
        sb.append("\"pre_key_id\":").append(preKeyId).append(',')
        sb.append("\"pre_key\":\"").append(base64(preKey)).append("\",")
        sb.append("\"signed_pre_key_id\":").append(signedPreKeyId).append(',')
        sb.append("\"signed_pre_key\":\"").append(base64(signedPreKey)).append("\",")
        sb.append("\"signed_pre_key_signature\":\"").append(base64(signedPreKeySignature)).append('"')
        sb.append('}')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }

    /** Project this wire payload into the security-layer [PreKeyBundle]. */
    fun toBundle(): PreKeyBundle = PreKeyBundle(
        uhid = uhid,
        identityKey = identityKey,
        identityKeyX25519 = identityKeyX25519,
        preKeyId = preKeyId,
        preKey = preKey,
        signedPreKeyId = signedPreKeyId,
        signedPreKey = signedPreKey,
        signedPreKeySignature = signedPreKeySignature
    )

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PreKeyResponsePayload) return false
        if (requestId != other.requestId) return false
        if (uhid != other.uhid) return false
        if (!identityKey.contentEquals(other.identityKey)) return false
        if (!identityKeyX25519.contentEquals(other.identityKeyX25519)) return false
        if (preKeyId != other.preKeyId) return false
        if (!preKey.contentEquals(other.preKey)) return false
        if (signedPreKeyId != other.signedPreKeyId) return false
        if (!signedPreKey.contentEquals(other.signedPreKey)) return false
        if (!signedPreKeySignature.contentEquals(other.signedPreKeySignature)) return false
        return true
    }

    override fun hashCode(): Int {
        var result = requestId.hashCode()
        result = 31 * result + uhid.hashCode()
        result = 31 * result + identityKey.contentHashCode()
        result = 31 * result + identityKeyX25519.contentHashCode()
        result = 31 * result + preKeyId
        result = 31 * result + preKey.contentHashCode()
        result = 31 * result + signedPreKeyId
        result = 31 * result + signedPreKey.contentHashCode()
        result = 31 * result + signedPreKeySignature.contentHashCode()
        return result
    }

    companion object {
        /** Build a response payload from a bundle, echoing the originating request id. */
        fun fromBundle(requestId: UUID, b: PreKeyBundle): PreKeyResponsePayload = PreKeyResponsePayload(
            requestId = requestId,
            uhid = b.uhid,
            identityKey = b.identityKey,
            identityKeyX25519 = b.identityKeyX25519,
            preKeyId = b.preKeyId,
            preKey = b.preKey,
            signedPreKeyId = b.signedPreKeyId,
            signedPreKey = b.signedPreKey,
            signedPreKeySignature = b.signedPreKeySignature
        )

        /**
         * Parse from canonical JSON. Returns null on a missing/malformed request_id, an empty uhid,
         * or any un-decodable base64 key field.
         */
        fun fromJson(json: String): PreKeyResponsePayload? {
            val requestId = JsonReader.readString(json, "request_id")?.let {
                runCatching { UUID.fromString(it) }.getOrNull()
            } ?: return null
            val uhid = JsonReader.readString(json, "uhid")
            if (uhid.isNullOrEmpty()) return null
            val identityKey = readBase64(json, "identity_key") ?: return null
            val identityKeyX25519 = readBase64(json, "identity_key_x25519") ?: return null
            val preKeyId = JsonReader.readInt(json, "pre_key_id") ?: return null
            val preKey = readBase64(json, "pre_key") ?: return null
            val signedPreKeyId = JsonReader.readInt(json, "signed_pre_key_id") ?: return null
            val signedPreKey = readBase64(json, "signed_pre_key") ?: return null
            val signedPreKeySignature = readBase64(json, "signed_pre_key_signature") ?: return null
            return PreKeyResponsePayload(
                requestId = requestId,
                uhid = uhid,
                identityKey = identityKey,
                identityKeyX25519 = identityKeyX25519,
                preKeyId = preKeyId,
                preKey = preKey,
                signedPreKeyId = signedPreKeyId,
                signedPreKey = signedPreKey,
                signedPreKeySignature = signedPreKeySignature
            )
        }

        private fun readBase64(json: String, key: String): ByteArray? {
            val s = JsonReader.readString(json, key) ?: return null
            return runCatching { Base64.getDecoder().decode(s) }.getOrNull()
        }
    }
}

/**
 * Event raised when a peer's pre-key bundle arrives in a [aethernet.protocol.PacketType.PreKeyResponse].
 * The peer's identity ([fromUhid]) is the inbound packet's sourceUhid. Feed [bundle] to
 * [aethernet.security.SignalProtocol.processPreKeyBundle] to perform the actual X3DH.
 */
data class PreKeyBundleReceived(
    /** The request id echoed from the original PreKeyRequest (nil UUID if unsolicited). */
    val requestId: UUID,
    /** UHID of the peer that sent the bundle. */
    val fromUhid: String,
    /** The received pre-key bundle. */
    val bundle: PreKeyBundle
)

/** STANDARD base64 (RFC 4648, '+/' alphabet, '=' padding) — matches System.Text.Json's byte[] default. */
private fun base64(b: ByteArray): String = Base64.getEncoder().encodeToString(b)

/** JSON-escape a string value for the hand-built wire encoder. */
private fun jsonEscape(s: String): String {
    val sb = StringBuilder()
    for (c in s) {
        when (c) {
            '\\' -> sb.append("\\\\")
            '"' -> sb.append("\\\"")
            '\n' -> sb.append("\\n")
            '\r' -> sb.append("\\r")
            '\t' -> sb.append("\\t")
            else -> sb.append(c)
        }
    }
    return sb.toString()
}
