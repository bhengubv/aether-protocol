// SPDX-License-Identifier: MIT
//
// Proof-of-Vicinity token model and canonical signable-body codec. Kotlin port of
// AetherNet.Market.Models.PoVToken / PoVTransportType / PoVScore and AetherNet.Market.PoVTokenCodec,
// byte-identical to the C# reference and the Go port.
//
// The canonical body that BOTH the witness and the subject sign with their real Ed25519 identity keys
// must stay byte-identical across every language implementation so a token signed by one node verifies
// on any other:
//
//   SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1 byte)
//
// timestamp_ticks is .NET DateTime.Ticks (100ns intervals since 0001-01-01).

package aethernet.market

import aethernet.incentive.appendJsonString
import org.json.JSONObject
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * The transport used for a co-presence Proof-of-Vicinity exchange. Only short-range transports are
 * valid (prevents remote minting). Enum wire bytes: ble = 0, nfc = 1, nearlink = 2.
 */
enum class PoVTransportType(val value: Byte) {
    /** Bluetooth Low Energy (short range — prevents remote forgery). */
    Ble(0),

    /** Near-Field Communication (requires physical proximity). */
    Nfc(1),

    /** Huawei NearLink (short range, similar to BLE). */
    NearLink(2);

    /** Whether the transport is a valid short-range PoV channel. */
    fun isShortRange(): Boolean = when (this) {
        Ble, Nfc, NearLink -> true
    }

    /** The lowercase wire name of the transport. */
    fun wireName(): String = when (this) {
        Ble -> "ble"
        Nfc -> "nfc"
        NearLink -> "nearlink"
    }

    companion object {
        /** Resolves a transport from its wire byte, or null if unknown (not short-range). */
        fun fromValue(value: Byte): PoVTransportType? = entries.find { it.value == value }
    }
}

/**
 * A Proof-of-Vicinity token issued by one node (the witness) to another (the subject) during a
 * physical co-presence event. Both parties must countersign — this prevents unilateral forgery. The
 * token is transmitted over a short-range transport (BLE/NFC/NearLink only) to prevent remote minting.
 *
 * Wire format: hand-rolled snake_case JSON ([buildString] encode, [org.json.JSONObject] decode) rather
 * than kotlinx.serialization, so this type compiles under AOSP Soong's plain kotlinc. Canonical key
 * order: `witness_uhid`, `subject_uhid`, `timestamp_ticks`, `transport_used`, `witness_signature`,
 * `subject_signature`.
 */
data class PoVToken(
    /** UHID of the node issuing the voucher. Wire key: `witness_uhid`. */
    val witnessUhid: String = "",

    /** UHID of the node being vouched for. Wire key: `subject_uhid`. */
    val subjectUhid: String = "",

    /**
     * Co-presence event time as .NET `DateTime.Ticks` (100ns since 0001-01-01). Stored as ticks
     * (not a Java time type) so the signed canonical body is byte-identical to C#. Wire key:
     * `timestamp_ticks`.
     */
    val timestampTicks: Long = 0L,

    /** Transport channel used (must be short-range). Wire key: `transport_used`. */
    val transportUsed: PoVTransportType = PoVTransportType.Ble,

    /** Ed25519 signature by the witness over the canonical body. Wire key: `witness_signature`. */
    val witnessSignature: ByteArray? = null,

    /**
     * Ed25519 countersignature by the subject — required for token validity. Wire key:
     * `subject_signature`.
     */
    val subjectSignature: ByteArray? = null,
) {
    /** The canonical signable bytes for this token. */
    fun signableData(): ByteArray = buildSignableTokenData(subjectUhid, timestampTicks, transportUsed)

    /** Canonical snake_case wire JSON. Fixed key order — see class doc. */
    fun toJson(): String = buildString {
        append("{\"witness_uhid\":"); appendJsonString(witnessUhid)
        append(",\"subject_uhid\":"); appendJsonString(subjectUhid)
        append(",\"timestamp_ticks\":").append(timestampTicks)
        append(",\"transport_used\":").append(transportUsed.value.toInt())
        append(",\"witness_signature\":")
        if (witnessSignature == null) append("null") else appendJsonString(base64(witnessSignature))
        append(",\"subject_signature\":")
        if (subjectSignature == null) append("null") else appendJsonString(base64(subjectSignature))
        append('}')
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PoVToken) return false
        if (witnessUhid != other.witnessUhid) return false
        if (subjectUhid != other.subjectUhid) return false
        if (timestampTicks != other.timestampTicks) return false
        if (transportUsed != other.transportUsed) return false
        if (!sigEquals(witnessSignature, other.witnessSignature)) return false
        if (!sigEquals(subjectSignature, other.subjectSignature)) return false
        return true
    }

    override fun hashCode(): Int {
        var result = witnessUhid.hashCode()
        result = 31 * result + subjectUhid.hashCode()
        result = 31 * result + timestampTicks.hashCode()
        result = 31 * result + transportUsed.hashCode()
        result = 31 * result + (witnessSignature?.contentHashCode() ?: 0)
        result = 31 * result + (subjectSignature?.contentHashCode() ?: 0)
        return result
    }

    companion object {
        /** Number of .NET DateTime ticks (100ns) per second. */
        const val TICKS_PER_SECOND = 10_000_000L

        /**
         * The .NET `DateTime.Ticks` value at the Unix epoch (1970-01-01T00:00:00Z), i.e. ticks
         * between 0001-01-01 and 1970-01-01. Used to convert between .NET ticks and Unix time.
         */
        const val UNIX_EPOCH_TICKS = 621_355_968_000_000_000L

        /**
         * Builds the canonical signable bytes for a PoV token body. The same layout is signed by the
         * witness (on issue) and counter-signed by the subject (on accept).
         * ```
         *   SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1 byte)
         * ```
         */
        fun buildSignableTokenData(
            subjectUhid: String,
            timestampTicks: Long,
            transport: PoVTransportType,
        ): ByteArray {
            val subjectBytes = subjectUhid.toByteArray(Charsets.UTF_8)
            val buffer = ByteBuffer.allocate(4 + subjectBytes.size + 8 + 1).order(ByteOrder.LITTLE_ENDIAN)
            buffer.putInt(subjectBytes.size)
            buffer.put(subjectBytes)
            buffer.putLong(timestampTicks)
            buffer.put(transport.value)
            return buffer.array()
        }

        /** Parse from canonical JSON. Returns null on malformed / missing-field input. */
        fun fromJson(json: String): PoVToken? = try {
            val o = JSONObject(json)
            val transport = PoVTransportType.fromValue(o.getInt("transport_used").toByte())
                ?: return null
            val wSig = if (!o.has("witness_signature") || o.isNull("witness_signature")) null
            else unbase64(o.getString("witness_signature"))
            val sSig = if (!o.has("subject_signature") || o.isNull("subject_signature")) null
            else unbase64(o.getString("subject_signature"))
            PoVToken(
                witnessUhid = o.getString("witness_uhid"),
                subjectUhid = o.getString("subject_uhid"),
                timestampTicks = o.getLong("timestamp_ticks"),
                transportUsed = transport,
                witnessSignature = wSig,
                subjectSignature = sSig,
            )
        } catch (_: Exception) {
            null
        }

        /**
         * Converts a .NET `DateTime.Ticks` value to Unix milliseconds (UTC). Provided for hosts that
         * want wall-clock time; the canonical body always uses the raw ticks.
         */
        fun ticksToUnixMillis(ticks: Long): Long = (ticks - UNIX_EPOCH_TICKS) / 10_000L

        /** Converts Unix milliseconds to a .NET `DateTime.Ticks` value. */
        fun unixMillisToTicks(unixMillis: Long): Long = unixMillis * 10_000L + UNIX_EPOCH_TICKS

        private fun sigEquals(a: ByteArray?, b: ByteArray?): Boolean =
            if (a == null) b == null else b != null && a.contentEquals(b)

        private fun base64(b: ByteArray): String = java.util.Base64.getEncoder().encodeToString(b)
        private fun unbase64(s: String): ByteArray = java.util.Base64.getDecoder().decode(s)
    }
}

/**
 * The Proof-of-Vicinity trust score for a node — a purely local anti-Sybil routing/identity signal
 * that attaches NO value semantics.
 */
data class PoVScore(
    /** UHID of the scored node. */
    val uhid: String,
    /** Number of distinct witnesses who have issued PoV tokens to this node. */
    val uniqueWitnesses: Int,
    /** Weighted score (0.0–1.0). */
    val weightedScore: Double,
    /** Time of the most recent score update, in Unix milliseconds. */
    val lastUpdatedUnixMs: Long,
)
