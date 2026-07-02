/**
 * Heartbeat data models (PacketType.Heartbeat = 10).
 *
 * The wire payload is UTF-8 JSON with snake_case keys and field order `sequence` then
 * `sent_at_ms` — both bare integers, so the encoding is byte-identical across all language
 * ports (locked by fixtures/heartbeat/vectors.json).
 *
 * SPDX-License-Identifier: MIT
 */

/**
 * JSON payload for a Heartbeat packet. A node periodically broadcasts a heartbeat (TTL 1 —
 * direct neighbours only) so peers can track liveness. `sequence` lets a receiver detect
 * loss/ordering; `sentAtMs` lets it gauge freshness. The heartbeat's originator is the
 * enclosing packet's sourceUhid.
 */
export interface HeartbeatPayload {
  /** Monotonic heartbeat sequence number from the sender (starts at 1, increments per beat). */
  sequence: number;
  /** Unix timestamp in milliseconds when the sender emitted this heartbeat. */
  sentAtMs: number;
}

/**
 * A peer's last observed liveness, maintained by the HeartbeatService on the receiving node.
 */
export interface PeerLiveness {
  /** UHID of the peer this liveness record describes. */
  uhid: string;
  /** The sequence of the most recent heartbeat seen from the peer. */
  lastSequence: number;
  /** The peer-stamped sentAtMs of the most recent heartbeat. */
  lastSentAtMs: number;
  /** Local Unix-ms timestamp when the most recent heartbeat was received. */
  receivedAtMs: number;
}
