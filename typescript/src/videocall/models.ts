/**
 * Video call-control data models (PacketType.VideoCall = 27).
 *
 * The wire payload is UTF-8 JSON with snake_case keys and field order `call_id`, `action`,
 * `sent_at_ms` — no whitespace, lowercase-dashed UUID, `sent_at_ms` a bare integer, `action`
 * an ASCII verb — so the encoding is byte-identical across every language port (locked by
 * fixtures/videocall/vectors.json).
 *
 * This is the caller-intent (call-control) layer, distinct from the media-plane
 * VideoSignaling (SDP/ICE negotiation) and VideoFrame (media). It mirrors how VoiceCall
 * carries voice call-control.
 *
 * SPDX-License-Identifier: MIT
 */

/**
 * JSON payload for a VideoCall packet — the video call-control signal
 * (ring / accept / decline / hangup). The caller mints a call id on ring; accept/decline/hangup
 * echo the same id back so both sides can correlate the signal to a call.
 */
export interface VideoCallControlPayload {
  /** Unique id for this call — minted by the caller on ring, echoed by accept/decline/hangup (lowercase-dashed UUID). */
  callId: string;
  /** Control verb: "ring", "accept", "decline", or "hangup". */
  action: string;
  /** Unix timestamp in milliseconds when the control signal was sent. */
  sentAtMs: number;
}

/**
 * Event surfaced when a video call-control signal arrives from a peer.
 */
export interface VideoCallStateChanged {
  /** Id of the call the signal refers to. */
  callId: string;
  /** The control verb received ("ring" / "accept" / "decline" / "hangup"). */
  action: string;
  /** UHID of the peer that sent the signal (the inbound packet's source). */
  fromUhid: string;
}
