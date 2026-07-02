// SPDX-License-Identifier: MIT

package aethernet.videocall

import java.util.UUID

/**
 * JSON payload for [aethernet.protocol.PacketType.VideoCall] packets — the video call-control signal
 * (ring / accept / decline / hangup), distinct from the media-plane VideoSignaling (SDP/ICE
 * negotiation) and VideoFrame (media) handled by the streaming VideoCall service. This is the
 * caller-intent layer, mirroring how VoiceCall carries voice call-control.
 *
 * Wire format: UTF-8 JSON with snake_case keys, field order call_id, action, sent_at_ms, no
 * whitespace, lowercase-dashed UUID (Java's UUID.toString()), sent_at_ms a bare integer, action an
 * ASCII verb. Every field is integer- or string-typed (no floating point), so the encoding is
 * byte-identical across all eight language ports.
 *
 * Wire vectors (fixtures/videocall/vectors.json), byte-identical with C#:
 *  - callId=0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f, action="ring", sentAtMs=1700000000000 →
 *    {"call_id":"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f","action":"ring","sent_at_ms":1700000000000}
 *  - callId=00000000-0000-0000-0000-000000000000, action="hangup", sentAtMs=0 →
 *    {"call_id":"00000000-0000-0000-0000-000000000000","action":"hangup","sent_at_ms":0}
 */
data class VideoCallControlPayload(
    /** Unique id for this call (minted by the caller on ring; echoed by accept/decline/hangup). */
    val callId: UUID,
    /** Control verb: "ring", "accept", "decline", or "hangup". */
    val action: String,
    /** Unix timestamp in milliseconds when the control signal was sent. */
    val sentAtMs: Long
) {
    /**
     * Serialize to the canonical UTF-8 JSON wire bytes. Built by hand (no kotlinx.serialization —
     * AOSP Soong forbids it), the same manual string-building approach used by the SOS / channels
     * payload encoders. snake_case keys, field order call_id, action, sent_at_ms, NO whitespace,
     * UUID lowercase-dashed (Java's UUID.toString()), sent_at_ms a bare integer.
     */
    fun toJsonBytes(): ByteArray {
        val sb = StringBuilder()
        sb.append('{')
        sb.append("\"call_id\":\"").append(callId).append("\",")
        sb.append("\"action\":\"").append(jsonEscape(action)).append("\",")
        sb.append("\"sent_at_ms\":").append(sentAtMs)
        sb.append('}')
        return sb.toString().toByteArray(Charsets.UTF_8)
    }

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
}

/**
 * Event raised when a video call-control signal arrives from a peer. Mirrors the C#
 * VideoCallStateChanged. The peer's identity ([fromUhid]) is the inbound packet's sourceUhid — not
 * carried in the payload.
 */
data class VideoCallStateChanged(
    /** Id of the call the signal refers to. */
    val callId: UUID,
    /** The control verb received ("ring" / "accept" / "decline" / "hangup"). */
    val action: String,
    /** UHID of the peer that sent the signal. */
    val fromUhid: String
)
