// SPDX-License-Identifier: MIT

package aethernet.incentive

import org.json.JSONObject
import java.math.BigDecimal
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID

/**
 * Generic "value-earned" relay-tip envelope carried inside a
 * [aethernet.protocol.PacketType.TipPacket] (24). Kotlin port of
 * `AetherNet.Incentive.TipPacketPayload`, byte-identical to the C# reference and
 * every other language implementation.
 *
 * This model is deliberately value-agnostic. [amount] is a bare number with NO
 * units, NO policy, and NO settlement semantics attached at the protocol layer.
 * The protocol carries the signal that one node wishes to credit another for some
 * kind of relayed traffic; what (if anything) that signal is worth is entirely the
 * host's business. A bare node accepts and relays the packet but settles nothing —
 * only a host that has wired a [MeshTipSettlementProvider] override decides how to
 * interpret the value.
 *
 * The payload is self-signed by the tipper: [signature] is an Ed25519 signature
 * over the canonical byte layout produced by [buildCanonicalData]. The signature
 * binds the tipper, recipient, amount, traffic type, reference, and timestamp
 * together so an intermediate relay cannot tamper with any field without
 * invalidating it.
 *
 * **Amount is the INVARIANT decimal string** — the .NET
 * `decimal.ToString(InvariantCulture)` round-trip form (e.g. `"12.50"`,
 * `"0.0001"`, `"123456.789"`), NOT a float. Keeping it a string is what makes the
 * signed bytes stable across locales and decimal scales without baking in any unit
 * or fixed-point assumption, and is required for byte-identity with the C#
 * canonical data. Use [of] to construct from a [BigDecimal] — it canonicalises via
 * [BigDecimal.toPlainString], which reproduces the .NET invariant form (preserving
 * trailing-zero scale, never using E-notation).
 *
 * Wire format: hand-rolled snake_case JSON ([buildString] encode,
 * [org.json.JSONObject] decode) rather than kotlinx.serialization, so this type
 * compiles under AOSP Soong's plain kotlinc (no serialization plugin). Canonical
 * key order: `tipper_uhid`, `recipient_uhid`, `amount`, `traffic_type`,
 * `reference_id`, `timestamp`, `signature`.
 */
data class TipPacketPayload(
    /** UHID of the node offering the tip (the signer of this payload). Wire key: `tipper_uhid`. */
    val tipperUhid: String = "",

    /** UHID of the node the tip is addressed to. Wire key: `recipient_uhid`. */
    val recipientUhid: String = "",

    /**
     * Generic value being credited, as the invariant decimal string. The protocol
     * imposes NO unit, NO minimum, NO maximum, and NO policy. Wire key: `amount`.
     */
    val amount: String = "0",

    /**
     * Free-form tag describing the kind of relayed traffic this tip is for,
     * e.g. `"message-relay"` or `"gateway-share"`. Opaque to the protocol.
     * Wire key: `traffic_type`.
     */
    val trafficType: String = "",

    /**
     * Optional correlation id linking this tip to some host-defined unit of work.
     * `null` when the tip stands alone (serialised as 16 zero bytes in the
     * canonical data). Wire key: `reference_id`.
     */
    val referenceId: UUID? = null,

    /** When the tipper created this payload, in Unix milliseconds. Wire key: `timestamp`. */
    val timestampUnixMs: Long = 0L,

    /**
     * Ed25519 signature over [buildCanonicalData], produced by the tipper's
     * identity key. `null` until the payload has been signed. Wire key: `signature`.
     */
    val signature: ByteArray? = null,
) {
    /**
     * Builds the canonical byte array that is signed/verified for this payload.
     * The [signature] field itself is excluded from the canonical data.
     *
     * Layout (little-endian lengths, matching the project's signable-data
     * conventions in `PacketSigning.constructSignableData`):
     * ```
     *   TipperLen(4 LE i32)    || Tipper(UTF-8)
     *   RecipientLen(4 LE i32) || Recipient(UTF-8)
     *   AmountLen(4 LE i32)    || Amount(UTF-8, invariant round-trip decimal string)
     *   TrafficLen(4 LE i32)   || TrafficType(UTF-8)
     *   ReferenceId(16, all-zero GUID when null, .NET mixed-endian byte order)
     *   TimestampUnixMs(8 LE i64)
     * ```
     */
    fun buildCanonicalData(): ByteArray {
        val tipperBytes = tipperUhid.toByteArray(Charsets.UTF_8)
        val recipientBytes = recipientUhid.toByteArray(Charsets.UTF_8)
        val amountBytes = amount.toByteArray(Charsets.UTF_8)
        val trafficBytes = trafficType.toByteArray(Charsets.UTF_8)

        val totalLength =
            4 + tipperBytes.size +
                4 + recipientBytes.size +
                4 + amountBytes.size +
                4 + trafficBytes.size +
                16 + // ReferenceId GUID
                8 // Timestamp (i64 LE)

        val buffer = ByteBuffer.allocate(totalLength).order(ByteOrder.LITTLE_ENDIAN)

        writeLengthPrefixed(buffer, tipperBytes)
        writeLengthPrefixed(buffer, recipientBytes)
        writeLengthPrefixed(buffer, amountBytes)
        writeLengthPrefixed(buffer, trafficBytes)

        // ReferenceId — 16 bytes, all-zero when null, .NET GUID byte order otherwise.
        buffer.put(guidBytesDotNet(referenceId))

        // Timestamp — Unix milliseconds, little-endian int64.
        buffer.putLong(timestampUnixMs)

        return buffer.array()
    }

    /** Canonical snake_case wire JSON. Fixed key order — see class doc. */
    fun toJson(): String = buildString {
        append("{\"tipper_uhid\":"); appendJsonString(tipperUhid)
        append(",\"recipient_uhid\":"); appendJsonString(recipientUhid)
        append(",\"amount\":"); appendJsonString(amount)
        append(",\"traffic_type\":"); appendJsonString(trafficType)
        append(",\"reference_id\":")
        if (referenceId == null) append("null") else appendJsonString(referenceId.toString())
        append(",\"timestamp\":").append(timestampUnixMs)
        append(",\"signature\":")
        if (signature == null) append("null") else appendJsonString(base64(signature))
        append('}')
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is TipPacketPayload) return false
        if (tipperUhid != other.tipperUhid) return false
        if (recipientUhid != other.recipientUhid) return false
        if (amount != other.amount) return false
        if (trafficType != other.trafficType) return false
        if (referenceId != other.referenceId) return false
        if (timestampUnixMs != other.timestampUnixMs) return false
        if (signature == null) {
            if (other.signature != null) return false
        } else {
            if (other.signature == null) return false
            if (!signature.contentEquals(other.signature)) return false
        }
        return true
    }

    override fun hashCode(): Int {
        var result = tipperUhid.hashCode()
        result = 31 * result + recipientUhid.hashCode()
        result = 31 * result + amount.hashCode()
        result = 31 * result + trafficType.hashCode()
        result = 31 * result + (referenceId?.hashCode() ?: 0)
        result = 31 * result + timestampUnixMs.hashCode()
        result = 31 * result + (signature?.contentHashCode() ?: 0)
        return result
    }

    companion object {
        /**
         * Constructs a payload from a [BigDecimal] amount, canonicalising it to the
         * invariant decimal string via [BigDecimal.toPlainString] (reproduces the
         * .NET `decimal.ToString(InvariantCulture)` form: trailing-zero scale
         * preserved, never E-notation).
         */
        fun of(
            tipperUhid: String,
            recipientUhid: String,
            amount: BigDecimal,
            trafficType: String,
            referenceId: UUID? = null,
            timestampUnixMs: Long = 0L,
            signature: ByteArray? = null,
        ): TipPacketPayload = TipPacketPayload(
            tipperUhid = tipperUhid,
            recipientUhid = recipientUhid,
            amount = amount.toPlainString(),
            trafficType = trafficType,
            referenceId = referenceId,
            timestampUnixMs = timestampUnixMs,
            signature = signature,
        )

        /** Parse from canonical JSON. Returns null on malformed / missing-field input. */
        fun fromJson(json: String): TipPacketPayload? = try {
            val o = JSONObject(json)
            val refStr = if (o.isNull("reference_id")) null else o.getString("reference_id")
            val sigStr = if (!o.has("signature") || o.isNull("signature")) null else o.getString("signature")
            TipPacketPayload(
                tipperUhid = o.getString("tipper_uhid"),
                recipientUhid = o.getString("recipient_uhid"),
                amount = o.getString("amount"),
                trafficType = o.getString("traffic_type"),
                referenceId = refStr?.let { UUID.fromString(it) },
                timestampUnixMs = o.getLong("timestamp"),
                signature = sigStr?.let { unbase64(it) },
            )
        } catch (_: Exception) {
            null
        }

        private fun writeLengthPrefixed(buffer: ByteBuffer, value: ByteArray) {
            buffer.putInt(value.size)
            buffer.put(value)
        }

        /**
         * Returns the 16-byte .NET in-memory representation of a UUID, which is what
         * `System.Guid.TryWriteBytes` produces; all-zero when [u] is null (matching
         * `Guid.Empty`). [java.util.UUID] exposes the value in big-endian (RFC 4122)
         * order; .NET stores the first three groups little-endian (Data1: 4 bytes,
         * Data2: 2 bytes, Data3: 2 bytes) and the final 8 bytes as-is. This
         * mixed-endian layout is required for byte-identity with the C# canonical
         * data.
         */
        private fun guidBytesDotNet(u: UUID?): ByteArray {
            val out = ByteArray(16)
            if (u == null) return out
            val msb = u.mostSignificantBits
            val lsb = u.leastSignificantBits
            // Big-endian RFC-4122 bytes of the UUID.
            val be = ByteArray(16)
            for (i in 0 until 8) be[i] = (msb ushr (8 * (7 - i))).toByte()
            for (i in 0 until 8) be[8 + i] = (lsb ushr (8 * (7 - i))).toByte()
            // Data1 (bytes 0..3) — reversed.
            out[0] = be[3]; out[1] = be[2]; out[2] = be[1]; out[3] = be[0]
            // Data2 (bytes 4..5) — reversed.
            out[4] = be[5]; out[5] = be[4]
            // Data3 (bytes 6..7) — reversed.
            out[6] = be[7]; out[7] = be[6]
            // Data4 (bytes 8..15) — as-is.
            for (i in 8 until 16) out[i] = be[i]
            return out
        }

        private fun base64(b: ByteArray): String = java.util.Base64.getEncoder().encodeToString(b)
        private fun unbase64(s: String): ByteArray = java.util.Base64.getDecoder().decode(s)
    }
}

/**
 * Append [s] as a JSON string literal (with quotes) onto the receiver, escaping
 * per RFC 8259. Local to the incentive wire encoder so it stays self-contained for
 * AOSP Soong (no cross-package helper dependency).
 */
internal fun StringBuilder.appendJsonString(s: String) {
    append('"')
    for (c in s) {
        when (c) {
            '"' -> append("\\\"")
            '\\' -> append("\\\\")
            '\n' -> append("\\n")
            '\r' -> append("\\r")
            '\t' -> append("\\t")
            '\b' -> append("\\b")
            else -> if (c < ' ') append("\\u").append(c.code.toString(16).padStart(4, '0')) else append(c)
        }
    }
    append('"')
}
