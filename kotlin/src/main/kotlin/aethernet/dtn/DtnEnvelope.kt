// SPDX-License-Identifier: MIT

package aethernet.dtn

import aethernet.models.DtnBundle
import aethernet.models.DtnDeliveryReceipt
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.time.Instant
import java.util.UUID

/**
 * Canonical binary DTN envelope (wire format v1). Byte-identical across all
 * eight AetherNet SDKs; the Go encoder (`go/cmd/dtnfixturegen`) is the oracle
 * and the `fixtures/dtn/expected/` .bin vectors pin the bytes.
 *
 * Layout — every multi-byte integer is LITTLE-ENDIAN, except the 16-byte bundle
 * id which is RFC-4122 BIG-ENDIAN (mirrors PacketSerializer; never the legacy
 * .NET mixed-endian Guid layout). Cleartext routing fields come first and the
 * opaque `encrypted_payload` is last, so the future T1 privacy bump can move
 * sender/recipient into the ciphertext without a re-layout.
 *
 * Kotlin's [DtnBundle] carries `status` as a String label and `priority` as a
 * plain Int, so this serializer maps the label to/from the canonical u8 ordinal
 * (Pending0/InCustody1/Delivered2/Expired3/Failed4) on the wire.
 */
object DtnEnvelope {
    const val VERSION: Int = 0x01
    private const val MAX_PAYLOAD = 16 * 1024 * 1024 // AETHERNET_MAX_PAYLOAD_LEN

    // ─────────────────────────── DtnBundle ───────────────────────────

    fun serializeBundle(b: DtnBundle): ByteArray {
        val out = ByteArrayOutputStream(64 + b.encryptedPayload.size)
        out.write(VERSION)
        out.write(uuidToBytes(b.id))
        out.write(b.priority and 0xff)
        out.write(statusToByte(b.status))
        writeI32(out, b.copyCount)
        writeI32(out, b.maxCopies)
        writeI32(out, b.hopCount)
        writeI64(out, b.createdAt.toEpochMilli())
        writeI64(out, b.expiresAt.toEpochMilli())
        writeStr(out, b.senderUhid)
        writeStr(out, b.recipientUhid)
        writeStr(out, b.senderGeohash ?: "")
        writeStr(out, b.recipientLastGeohash ?: "")
        writeBytes32(out, b.encryptedPayload)
        return out.toByteArray()
    }

    fun deserializeBundle(data: ByteArray): DtnBundle {
        val r = Reader(data)
        r.version()
        val id = r.uuid()
        val priority = r.u8()
        require(priority <= 3) { "DTN: invalid priority $priority" }
        val status = byteToStatus(r.u8())
        val copyCount = r.i32()
        val maxCopies = r.i32()
        val hopCount = r.i32()
        val createdAt = Instant.ofEpochMilli(r.i64())
        val expiresAt = Instant.ofEpochMilli(r.i64())
        val senderUhid = r.str()
        val recipientUhid = r.str()
        val senderGeohash = r.str()
        val recipientLastGeohash = r.str()
        val payload = r.bytes32()
        return DtnBundle(
            id = id,
            senderUhid = senderUhid,
            recipientUhid = recipientUhid,
            encryptedPayload = payload,
            priority = priority,
            status = status,
            copyCount = copyCount,
            maxCopies = maxCopies,
            senderGeohash = senderGeohash,
            recipientLastGeohash = recipientLastGeohash,
            hopCount = hopCount,
            createdAt = createdAt,
            expiresAt = expiresAt
        )
    }

    // ─────────────────── CustodyAck (18 bytes fixed) ──────────────────

    fun serializeCustodyAck(bundleId: UUID, accepted: Boolean): ByteArray {
        val out = ByteArrayOutputStream(18)
        out.write(VERSION)
        out.write(uuidToBytes(bundleId))
        out.write(if (accepted) 1 else 0)
        return out.toByteArray()
    }

    fun deserializeCustodyAck(data: ByteArray): Pair<UUID, Boolean> {
        val r = Reader(data)
        r.version()
        val id = r.uuid()
        val accepted = r.u8() != 0
        return Pair(id, accepted)
    }

    // ───────────────────────── DeliveryReceipt ────────────────────────

    fun serializeDeliveryReceipt(receipt: DtnDeliveryReceipt): ByteArray {
        val out = ByteArrayOutputStream(64)
        out.write(VERSION)
        out.write(uuidToBytes(receipt.bundleId))
        writeStr(out, receipt.recipientUhid)
        writeI32(out, receipt.totalHops)
        writeI32(out, receipt.totalCustodyTransfers)
        writeI64(out, receipt.deliveredAt.toEpochMilli())
        return out.toByteArray()
    }

    fun deserializeDeliveryReceipt(data: ByteArray): DtnDeliveryReceipt {
        val r = Reader(data)
        r.version()
        val id = r.uuid()
        val recipient = r.str()
        val totalHops = r.i32()
        val totalCustody = r.i32()
        val deliveredAt = Instant.ofEpochMilli(r.i64())
        return DtnDeliveryReceipt(
            bundleId = id,
            recipientUhid = recipient,
            totalHops = totalHops,
            totalCustodyTransfers = totalCustody,
            deliveredAt = deliveredAt
        )
    }

    // ───────── status ↔ ordinal (model carries a String label) ─────────

    private fun statusToByte(s: String): Int = when (s) {
        "Pending" -> 0
        "InCustody" -> 1
        "Delivered" -> 2
        "Expired" -> 3
        "Failed" -> 4
        else -> 0
    }

    private fun byteToStatus(b: Int): String = when (b) {
        0 -> "Pending"
        1 -> "InCustody"
        2 -> "Delivered"
        3 -> "Expired"
        4 -> "Failed"
        else -> throw IllegalArgumentException("DTN: invalid status $b")
    }

    // ───────────────────────────── primitives ─────────────────────────

    /** RFC-4122 big-endian (ByteBuffer defaults to big-endian). */
    private fun uuidToBytes(id: UUID): ByteArray =
        ByteBuffer.allocate(16)
            .putLong(id.mostSignificantBits)
            .putLong(id.leastSignificantBits)
            .array()

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

    private fun writeStr(out: ByteArrayOutputStream, s: String) {
        val bytes = s.toByteArray(Charsets.UTF_8)
        require(bytes.size <= 0xFFFF) { "DTN: string too long (${bytes.size} bytes)" }
        writeU16(out, bytes.size)
        out.write(bytes)
    }

    private fun writeBytes32(out: ByteArrayOutputStream, b: ByteArray) {
        writeI32(out, b.size)
        out.write(b)
    }

    private class Reader(private val d: ByteArray) {
        private var o = 0

        fun version() {
            val v = u8()
            require(v == VERSION) { "DTN: unsupported envelope version 0x${v.toString(16)}" }
        }

        fun u8(): Int {
            require(o + 1 <= d.size) { "DTN: truncated envelope" }
            return d[o++].toInt() and 0xff
        }

        fun uuid(): UUID {
            require(o + 16 <= d.size) { "DTN: truncated envelope" }
            val bb = ByteBuffer.wrap(d, o, 16) // big-endian read
            o += 16
            val msb = bb.long
            val lsb = bb.long
            return UUID(msb, lsb)
        }

        fun i32(): Int {
            require(o + 4 <= d.size) { "DTN: truncated envelope" }
            val v = (d[o].toInt() and 0xff) or
                ((d[o + 1].toInt() and 0xff) shl 8) or
                ((d[o + 2].toInt() and 0xff) shl 16) or
                ((d[o + 3].toInt() and 0xff) shl 24)
            o += 4
            return v
        }

        fun i64(): Long {
            require(o + 8 <= d.size) { "DTN: truncated envelope" }
            var v = 0L
            for (i in 0 until 8) {
                v = v or ((d[o + i].toLong() and 0xff) shl (8 * i))
            }
            o += 8
            return v
        }

        fun u16(): Int {
            require(o + 2 <= d.size) { "DTN: truncated envelope" }
            val v = (d[o].toInt() and 0xff) or ((d[o + 1].toInt() and 0xff) shl 8)
            o += 2
            return v
        }

        fun str(): String {
            val n = u16()
            require(o + n <= d.size) { "DTN: truncated string" }
            val s = String(d, o, n, Charsets.UTF_8)
            o += n
            return s
        }

        fun bytes32(): ByteArray {
            val n = i32()
            require(n in 0..MAX_PAYLOAD) { "DTN: invalid payload length $n" }
            require(o + n <= d.size) { "DTN: truncated payload" }
            val b = d.copyOfRange(o, o + n)
            o += n
            return b
        }
    }
}
