// SPDX-License-Identifier: MIT

package aethernet.sync

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.util.UUID

/**
 * The kind of state change a [SyncRecord] carries. On the wire this is a single
 * u8 ordinal, identical to the C# `SyncOp` enum.
 */
enum class SyncOp(val code: Int) {
    /** Create or update the item. */
    UPSERT(0),

    /** Delete the item. */
    DELETE(1),

    /** Mark the item read (read-state sync). */
    READ(2);

    companion object {
        /** Maps a wire ordinal back to a [SyncOp]; throws for anything above [READ]. */
        fun fromCode(code: Int): SyncOp = when (code) {
            0 -> UPSERT
            1 -> DELETE
            2 -> READ
            else -> throw IllegalArgumentException("Unknown SyncRecord op $code")
        }
    }
}

/**
 * One state change to a synced item (a message, a read-marker, a deletion),
 * emitted by one of a user's devices and gossiped to that user's other devices
 * so they all converge on the same state — with no server.
 *
 * The [encryptedPayload] is already end-to-end encrypted to the user's device
 * set, so any node that relays the record (over the mesh or via DTN
 * store-and-forward) learns nothing about its content.
 *
 * [recordId] is a globally-unique id. It is carried on the wire as its 16
 * RFC-4122 big-endian bytes (matching the fixture's `uuid` string, character
 * order == byte order), and the reconciler's final tie-break compares exactly
 * those 16 bytes as unsigned. See [SyncRecordSerializer] for the byte layout.
 *
 * @property recordId Globally-unique id for this record.
 * @property deviceId The device that produced the record.
 * @property op Create/update, delete, or read-marker.
 * @property itemId The item this record is about (the sync key).
 * @property logicalClock The device's monotonic counter at emit time.
 * @property createdAtMs Wall-clock time (Unix ms) the record was created.
 * @property encryptedPayload The E2E-encrypted item content (opaque; empty for a delete/read).
 */
data class SyncRecord(
    val recordId: UUID,
    val deviceId: String,
    val op: SyncOp,
    val itemId: String,
    val logicalClock: Long,
    val createdAtMs: Long,
    val encryptedPayload: ByteArray,
) {
    /**
     * The 16 RFC-4122 big-endian bytes of [recordId] — the same ordering as the
     * fixture's dashed uuid string and the C# `Guid.TryWriteBytes(bigEndian:true)`.
     * Used both on the wire and as the reconciler's final unsigned tie-breaker.
     */
    val recordIdBytes: ByteArray
        get() = ByteBuffer.allocate(16)
            .putLong(recordId.mostSignificantBits)
            .putLong(recordId.leastSignificantBits)
            .array()

    // data class equals/hashCode do a reference compare on ByteArray; override so
    // two records with equal payload bytes compare equal (used in round-trip asserts).
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is SyncRecord) return false
        return recordId == other.recordId &&
            deviceId == other.deviceId &&
            op == other.op &&
            itemId == other.itemId &&
            logicalClock == other.logicalClock &&
            createdAtMs == other.createdAtMs &&
            encryptedPayload.contentEquals(other.encryptedPayload)
    }

    override fun hashCode(): Int {
        var result = recordId.hashCode()
        result = 31 * result + deviceId.hashCode()
        result = 31 * result + op.hashCode()
        result = 31 * result + itemId.hashCode()
        result = 31 * result + logicalClock.hashCode()
        result = 31 * result + createdAtMs.hashCode()
        result = 31 * result + encryptedPayload.contentHashCode()
        return result
    }
}

/**
 * Binary wire format for a [SyncRecord] — the unit a device gossips to a user's
 * other devices. Little-endian integers, RFC-4122 big-endian record id,
 * u16-length-prefixed UTF-8 strings, i32-length-prefixed payload — identical
 * bytes across every AetherNet SDK (verified against fixtures/sync/vectors.json).
 *
 * Layout: version(u8=1) · record_id(16, big-endian) · op(u8) · logical_clock(i64 LE)
 * · created_at_ms(i64 LE) · device_id(u16 len + utf8) · item_id(u16 len + utf8)
 * · encrypted_payload(i32 len + bytes).
 */
object SyncRecordSerializer {
    /** Wire format version; readers reject any other value. */
    const val FORMAT_VERSION: Int = 0x01

    /** Serializes a record to its canonical bytes. */
    fun serialize(record: SyncRecord): ByteArray {
        val device = record.deviceId.toByteArray(Charsets.UTF_8)
        val item = record.itemId.toByteArray(Charsets.UTF_8)
        val payload = record.encryptedPayload
        require(device.size <= 0xFFFF) { "DeviceId is too long." }
        require(item.size <= 0xFFFF) { "ItemId is too long." }

        val out = ByteArrayOutputStream(1 + 16 + 1 + 8 + 8 + 2 + device.size + 2 + item.size + 4 + payload.size)
        out.write(FORMAT_VERSION)
        out.write(record.recordIdBytes) // 16 bytes, big-endian
        out.write(record.op.code and 0xff)
        writeI64(out, record.logicalClock)
        writeI64(out, record.createdAtMs)
        writeStr(out, device)
        writeStr(out, item)
        writeI32(out, payload.size)
        out.write(payload)
        return out.toByteArray()
    }

    /** Parses canonical bytes back into a record, validating framing. */
    fun deserialize(data: ByteArray): SyncRecord {
        require(data.size >= 1 + 16 + 1 + 8 + 8 + 2 + 2 + 4) { "SyncRecord is too short." }
        val r = Reader(data)
        r.version()
        val recordId = r.uuidBigEndian()
        val op = SyncOp.fromCode(r.u8())
        val logicalClock = r.i64()
        val createdAtMs = r.i64()
        val deviceId = r.str()
        val itemId = r.str()
        val payloadLen = r.i32()
        require(payloadLen >= 0) { "SyncRecord payload length is invalid." }
        val payload = r.take(payloadLen)
        return SyncRecord(recordId, deviceId, op, itemId, logicalClock, createdAtMs, payload)
    }

    // ── primitives (mirror DtnEnvelope: LE ints, big-endian uuid) ──

    private fun writeI32(out: ByteArrayOutputStream, v: Int) {
        out.write(v and 0xff)
        out.write((v ushr 8) and 0xff)
        out.write((v ushr 16) and 0xff)
        out.write((v ushr 24) and 0xff)
    }

    private fun writeI64(out: ByteArrayOutputStream, v: Long) {
        var x = v
        for (i in 0 until 8) {
            out.write((x and 0xff).toInt())
            x = x ushr 8
        }
    }

    private fun writeU16(out: ByteArrayOutputStream, v: Int) {
        out.write(v and 0xff)
        out.write((v ushr 8) and 0xff)
    }

    private fun writeStr(out: ByteArrayOutputStream, utf8: ByteArray) {
        writeU16(out, utf8.size)
        out.write(utf8)
    }

    private class Reader(private val d: ByteArray) {
        private var o = 0

        fun version() {
            val v = u8()
            require(v == FORMAT_VERSION) { "Unsupported SyncRecord format version." }
        }

        fun u8(): Int {
            require(o + 1 <= d.size) { "SyncRecord is truncated." }
            return d[o++].toInt() and 0xff
        }

        /** Reads 16 big-endian bytes as an RFC-4122 UUID (msb then lsb). */
        fun uuidBigEndian(): UUID {
            require(o + 16 <= d.size) { "SyncRecord is truncated." }
            val bb = ByteBuffer.wrap(d, o, 16) // big-endian by default
            o += 16
            val msb = bb.long
            val lsb = bb.long
            return UUID(msb, lsb)
        }

        fun i32(): Int {
            require(o + 4 <= d.size) { "SyncRecord payload length is truncated." }
            val v = (d[o].toInt() and 0xff) or
                ((d[o + 1].toInt() and 0xff) shl 8) or
                ((d[o + 2].toInt() and 0xff) shl 16) or
                ((d[o + 3].toInt() and 0xff) shl 24)
            o += 4
            return v
        }

        fun i64(): Long {
            require(o + 8 <= d.size) { "SyncRecord is truncated." }
            var v = 0L
            for (i in 0 until 8) {
                v = v or ((d[o + i].toLong() and 0xff) shl (8 * i))
            }
            o += 8
            return v
        }

        fun u16(): Int {
            require(o + 2 <= d.size) { "SyncRecord string length is truncated." }
            val v = (d[o].toInt() and 0xff) or ((d[o + 1].toInt() and 0xff) shl 8)
            o += 2
            return v
        }

        fun str(): String {
            val n = u16()
            require(o + n <= d.size) { "SyncRecord string is truncated." }
            val s = String(d, o, n, Charsets.UTF_8)
            o += n
            return s
        }

        fun take(n: Int): ByteArray {
            require(o + n <= d.size) { "SyncRecord payload length is invalid." }
            val b = d.copyOfRange(o, o + n)
            o += n
            return b
        }
    }
}
