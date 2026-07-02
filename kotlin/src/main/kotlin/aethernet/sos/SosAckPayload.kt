// SPDX-License-Identifier: MIT

package aethernet.sos

import java.util.UUID

/**
 * JSON payload for [aethernet.protocol.PacketType.SosAck] packets. Wire format: UTF-8 JSON with
 * snake_case property names. Every field is integer- or string-typed (no floating point), so the
 * encoding is byte-identical across all eight language ports.
 *
 * An `SosAck` is sent by a node that has just received an
 * [aethernet.protocol.PacketType.SosBroadcast], directed back toward the alert's originator, so the
 * person raising the emergency learns their broadcast actually reached at least one device. The
 * acknowledging node's identity is carried by the enclosing packet's `sourceUhid` — it is not
 * duplicated in the body.
 *
 * Wire vectors (fixtures/sos/vectors.json), byte-identical with C#:
 *  - broadcastId=0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f, receivedAtMs=1700000000000 →
 *    {"broadcast_id":"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f","received_at_ms":1700000000000}
 *  - broadcastId=00000000-0000-0000-0000-000000000000, receivedAtMs=0 →
 *    {"broadcast_id":"00000000-0000-0000-0000-000000000000","received_at_ms":0}
 */
data class SosAckPayload(
    /** Id of the [aethernet.models.SosAlert] / SOS broadcast being acknowledged. */
    val broadcastId: UUID,
    /** Unix timestamp in milliseconds at which the acknowledging node received the SOS. */
    val receivedAtMs: Long
) {
    /**
     * Serialize to the canonical UTF-8 JSON wire bytes. Built by hand (no kotlinx.serialization —
     * AOSP Soong forbids it), the same manual string-building approach used by the SOS envelope
     * encoder. snake_case keys, field order broadcast_id then received_at_ms, NO whitespace, UUID
     * lowercase-dashed, received_at_ms a bare integer.
     */
    fun toJsonBytes(): ByteArray {
        val sb = StringBuilder()
        sb.append('{')
        sb.append("\"broadcast_id\":\"").append(broadcastId).append("\",")
        sb.append("\"received_at_ms\":").append(receivedAtMs)
        sb.append('}')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }
}
