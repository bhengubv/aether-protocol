// SPDX-License-Identifier: MIT

package aethernet.heartbeat

/**
 * JSON payload for [aethernet.protocol.PacketType.Heartbeat] packets. Wire format: UTF-8 JSON with
 * snake_case property names. Both fields are integers, so the encoding is byte-identical across all
 * eight language ports (locked by fixtures/heartbeat/vectors.json).
 *
 * A node periodically broadcasts a heartbeat (TTL 1 — direct neighbours only) so peers can track
 * liveness. [sequence] lets a receiver detect loss/ordering; [sentAtMs] lets it gauge freshness. The
 * heartbeat's originator is the enclosing packet's `sourceUhid`.
 *
 * Wire vectors (fixtures/heartbeat/vectors.json), byte-identical with C#:
 *  - sequence=1, sentAtMs=1700000000000 → {"sequence":1,"sent_at_ms":1700000000000}
 *  - sequence=0, sentAtMs=0             → {"sequence":0,"sent_at_ms":0}
 */
data class HeartbeatPayload(
    /** Monotonic heartbeat sequence number from the sender (starts at 1, increments per beat). */
    val sequence: Int,
    /** Unix timestamp in milliseconds when the sender emitted this heartbeat. */
    val sentAtMs: Long
) {
    /**
     * Serialize to the canonical UTF-8 JSON wire bytes. Built by hand (no kotlinx.serialization —
     * AOSP Soong forbids it), the same manual string-building approach used by the SOS payload
     * encoder. snake_case keys, field order sequence then sent_at_ms, NO whitespace, both values
     * bare integers.
     */
    fun toJsonBytes(): ByteArray {
        val sb = StringBuilder()
        sb.append('{')
        sb.append("\"sequence\":").append(sequence).append(',')
        sb.append("\"sent_at_ms\":").append(sentAtMs)
        sb.append('}')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }
}

/**
 * A peer's last observed liveness, maintained by [HeartbeatService] on the receiving node.
 */
data class PeerLiveness(
    /** UHID of the peer this liveness record describes. */
    val uhid: String,
    /** The [HeartbeatPayload.sequence] of the most recent heartbeat seen from the peer. */
    val lastSequence: Int,
    /** The peer-stamped [HeartbeatPayload.sentAtMs] of the most recent heartbeat. */
    val lastSentAtMs: Long,
    /** Local Unix-ms timestamp when the most recent heartbeat was received. */
    val receivedAtMs: Long
)
