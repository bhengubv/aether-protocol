// SPDX-License-Identifier: MIT

package aethernet.circuitrelay

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.util.UUID

/**
 * Message type of a [RelayFrame] — the circuit-relay-v2 control/data verbs. Any
 * AetherNet node can act as a relay: a client that cannot reach a target directly
 * reserves capacity on a relay it *can* reach, asks the relay to bridge to the
 * target, then tunnels data through the bridge. This is the native, no-libp2p
 * equivalent of libp2p circuit-relay-v2's HOP/STOP protocol. Byte values match the
 * C#/Go reference exactly for wire-format compatibility.
 */
enum class RelayMessageType(val value: Byte) {
    /** Client → relay: request a reservation (permission to be relayed to). */
    Reserve(1),
    /** Relay → client: reservation grant/refusal + limits (see [RelayStatus]). */
    ReserveResponse(2),
    /** Client → relay: bridge me to [RelayFrame.destinationUhid]. */
    Connect(3),
    /** Relay → target: client [RelayFrame.sourceUhid] wants to reach you. */
    Stop(4),
    /** Target → relay: accept/reject the inbound bridge. */
    StopResponse(5),
    /** Relay → client: bridge established/refused. */
    ConnectResponse(6),
    /** Either endpoint → relay → other endpoint: opaque tunnelled payload. */
    Data(7);

    companion object {
        /** Wire-ordinal → enum, or `null` if out of range (readers reject null). */
        fun fromByte(b: Int): RelayMessageType? = entries.firstOrNull { it.value.toInt() == b }
    }
}

/**
 * Status carried by a relay response frame. Mirrors the libp2p circuit-relay-v2
 * status codes closely enough to be intuitive, but is an independent native enum.
 * Byte values match the C#/Go reference exactly.
 */
enum class RelayStatus(val value: Byte) {
    /** Success (reservation granted / bridge established / no error). */
    Ok(0),
    /** Relay declined to reserve capacity for the client. */
    ReservationRefused(1),
    /** Connect attempted without a valid reservation. */
    NoReservation(2),
    /** The bridge's data or duration budget was exhausted. */
    ResourceLimitExceeded(3),
    /** Policy denied the reservation or connection. */
    PermissionDenied(4),
    /** Relay could not reach / was refused by the target. */
    ConnectionFailed(5),
    /** A received frame was malformed. */
    MalformedMessage(6);

    companion object {
        /** Wire-ordinal → enum, or `null` if out of range (readers reject null). */
        fun fromByte(b: Int): RelayStatus? = entries.firstOrNull { it.value.toInt() == b }
    }
}

/**
 * A single circuit-relay-v2 wire frame. One fixed-layout record carries every verb
 * (type-discriminated) so the format is trivial to keep byte-identical across all
 * eight language SDKs. It rides in `MeshPacket.payload` the same way the DTN
 * envelope does.
 *
 * Serialized by [RelayFrame.serialize]. All multi-byte integers are little-endian;
 * the 16-byte [connectionId] is the [UUID] in RFC-4122 big-endian order (never a
 * mixed-endian layout); strings are uint16-LE length-prefixed UTF-8; the payload is
 * int32-LE length-prefixed raw bytes and always last. A `null` [connectionId] is the
 * nil UUID (16 zero bytes) on the wire.
 */
data class RelayFrame(
    /** Which verb this frame carries. */
    val type: RelayMessageType,
    /** Result code (meaningful on the *Response frames; [RelayStatus.Ok] otherwise). */
    val status: RelayStatus = RelayStatus.Ok,
    /** UHID of the originating client (A). */
    val sourceUhid: String = "",
    /** UHID of the final target (B). */
    val destinationUhid: String = "",
    /** UHID of the relay node (R). May be empty on client→relay requests. */
    val relayUhid: String = "",
    /** Correlation id for a bridge session, shared by all frames of that session. `null` = nil UUID. */
    val connectionId: UUID? = null,
    /** Reservation expiry as Unix ms. 0 when not applicable. */
    val reservationExpiresAtMs: Long = 0,
    /** Bridge duration budget in seconds. 0 = unlimited. */
    val limitDurationSeconds: Int = 0,
    /** Bridge data budget in bytes. 0 = unlimited. */
    val limitDataBytes: Long = 0,
    /** Tunnelled payload ([RelayMessageType.Data] only; empty otherwise). */
    val payload: ByteArray = ByteArray(0)
) {
    // data class over a ByteArray field needs structural equals/hashCode.
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is RelayFrame) return false
        return type == other.type &&
            status == other.status &&
            sourceUhid == other.sourceUhid &&
            destinationUhid == other.destinationUhid &&
            relayUhid == other.relayUhid &&
            connectionId == other.connectionId &&
            reservationExpiresAtMs == other.reservationExpiresAtMs &&
            limitDurationSeconds == other.limitDurationSeconds &&
            limitDataBytes == other.limitDataBytes &&
            payload.contentEquals(other.payload)
    }

    override fun hashCode(): Int {
        var result = type.hashCode()
        result = 31 * result + status.hashCode()
        result = 31 * result + sourceUhid.hashCode()
        result = 31 * result + destinationUhid.hashCode()
        result = 31 * result + relayUhid.hashCode()
        result = 31 * result + (connectionId?.hashCode() ?: 0)
        result = 31 * result + reservationExpiresAtMs.hashCode()
        result = 31 * result + limitDurationSeconds
        result = 31 * result + limitDataBytes.hashCode()
        result = 31 * result + payload.contentHashCode()
        return result
    }

    companion object {
        /** Format-version byte at offset 0 of every relay frame. */
        const val VERSION: Int = 0x01

        private const val MAX_PAYLOAD = 16 * 1024 * 1024 // AETHERNET_MAX_PAYLOAD_LEN

        /** Nil UUID (all-zero) written when [connectionId] is `null`. */
        private val NIL_UUID = UUID(0L, 0L)

        // ───────────────────────────── serialize ──────────────────────────

        /** Encode a [RelayFrame] to its binary wire form. */
        fun serialize(f: RelayFrame): ByteArray {
            val out = ByteArrayOutputStream(48 + f.payload.size)
            out.write(VERSION)
            out.write(f.type.value.toInt() and 0xff)
            out.write(f.status.value.toInt() and 0xff)
            writeStr(out, f.sourceUhid)
            writeStr(out, f.destinationUhid)
            writeStr(out, f.relayUhid)
            out.write(uuidToBytes(f.connectionId ?: NIL_UUID))
            writeI64(out, f.reservationExpiresAtMs)
            writeI32(out, f.limitDurationSeconds)
            writeI64(out, f.limitDataBytes)
            writeBytes32(out, f.payload)
            return out.toByteArray()
        }

        /** Decode a [RelayFrame] from its binary wire form. */
        fun deserialize(data: ByteArray): RelayFrame {
            val r = Reader(data)
            r.version()

            val typeByte = r.u8()
            val type = RelayMessageType.fromByte(typeByte)
                ?: throw IllegalArgumentException("Relay: invalid message type $typeByte")

            val statusByte = r.u8()
            val status = RelayStatus.fromByte(statusByte)
                ?: throw IllegalArgumentException("Relay: invalid status $statusByte")

            val src = r.str()
            val dst = r.str()
            val relay = r.str()
            val connId = r.uuid()
            val reservationExpiresAtMs = r.i64()
            val limitDurationSeconds = r.i32()
            val limitDataBytes = r.i64()
            val payload = r.bytes32()

            return RelayFrame(
                type = type,
                status = status,
                sourceUhid = src,
                destinationUhid = dst,
                relayUhid = relay,
                connectionId = if (connId == NIL_UUID) null else connId,
                reservationExpiresAtMs = reservationExpiresAtMs,
                limitDurationSeconds = limitDurationSeconds,
                limitDataBytes = limitDataBytes,
                payload = payload
            )
        }

        // ───────────────────── primitives (mirror DtnEnvelope) ─────────────

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
            require(bytes.size <= 0xFFFF) { "Relay: string too long (${bytes.size} bytes)" }
            writeU16(out, bytes.size)
            out.write(bytes)
        }

        private fun writeBytes32(out: ByteArrayOutputStream, b: ByteArray) {
            require(b.size <= MAX_PAYLOAD) { "Relay: payload too large (${b.size} bytes)" }
            writeI32(out, b.size)
            out.write(b)
        }

        private class Reader(private val d: ByteArray) {
            private var o = 0

            fun version() {
                val v = u8()
                require(v == VERSION) { "Relay: unsupported frame version 0x${v.toString(16)}" }
            }

            fun u8(): Int {
                require(o + 1 <= d.size) { "Relay: truncated frame" }
                return d[o++].toInt() and 0xff
            }

            fun uuid(): UUID {
                require(o + 16 <= d.size) { "Relay: truncated frame" }
                val bb = ByteBuffer.wrap(d, o, 16) // big-endian read
                o += 16
                val msb = bb.long
                val lsb = bb.long
                return UUID(msb, lsb)
            }

            fun i32(): Int {
                require(o + 4 <= d.size) { "Relay: truncated frame" }
                val v = (d[o].toInt() and 0xff) or
                    ((d[o + 1].toInt() and 0xff) shl 8) or
                    ((d[o + 2].toInt() and 0xff) shl 16) or
                    ((d[o + 3].toInt() and 0xff) shl 24)
                o += 4
                return v
            }

            fun i64(): Long {
                require(o + 8 <= d.size) { "Relay: truncated frame" }
                var v = 0L
                for (i in 0 until 8) {
                    v = v or ((d[o + i].toLong() and 0xff) shl (8 * i))
                }
                o += 8
                return v
            }

            fun u16(): Int {
                require(o + 2 <= d.size) { "Relay: truncated frame" }
                val v = (d[o].toInt() and 0xff) or ((d[o + 1].toInt() and 0xff) shl 8)
                o += 2
                return v
            }

            fun str(): String {
                val n = u16()
                require(o + n <= d.size) { "Relay: truncated string" }
                val s = String(d, o, n, Charsets.UTF_8)
                o += n
                return s
            }

            fun bytes32(): ByteArray {
                val n = i32()
                require(n in 0..MAX_PAYLOAD) { "Relay: invalid payload length $n" }
                require(o + n <= d.size) { "Relay: truncated payload" }
                val b = d.copyOfRange(o, o + n)
                o += n
                return b
            }
        }
    }
}
