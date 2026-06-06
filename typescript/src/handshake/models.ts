/**
 * Handshake wire payload + negotiated-capabilities record.
 *
 * Cross-language compatibility note: HelloPayload is serialized as JSON with
 * snake_case keys to match the C# reference (and every other Aether wire
 * format). Drift between language implementations breaks first-contact
 * negotiation, so the JSON shape is load-bearing — see
 * src/AetherNet.Core/Handshake/HelloPayload.cs.
 *
 * SPDX-License-Identifier: MIT
 */

/**
 * Wire payload carried inside a {@link import("../protocol/PacketType.js").PacketType.Hello}
 * or {@link import("../protocol/PacketType.js").PacketType.HelloAck} packet's
 * {@link import("../protocol/MeshPacket.js").MeshPacket.payload}.
 *
 * JSON shape (snake_case, matches C# HelloPayload):
 * ```
 * {
 *   "min_version": 1,
 *   "max_version": 2,
 *   "capabilities": ["signal-x3dh", "double-ratchet", "dtn-custody"],
 *   "implementation": "aether-typescript/1.0.0"
 * }
 * ```
 *
 * Notes on security: this payload is NEITHER encrypted NOR authenticated by
 * design — the handshake runs before any Signal session exists. Peer identity
 * is verified later via Ed25519 packet signatures on the data packets the
 * peer subsequently sends. Treat the announced capabilities as a hint, not
 * as a security claim.
 */
export interface HelloPayload {
  /** Lowest protocol version the announcer can speak. */
  min_version: number;
  /** Highest protocol version the announcer can speak. */
  max_version: number;
  /**
   * Capability tags advertised by the announcer. Capability names are wire
   * constants — case-sensitive, not human strings.
   */
  capabilities: string[];
  /**
   * Free-form implementation banner (e.g. "aether-typescript/1.0.0").
   * Diagnostic only; not used for compatibility decisions.
   */
  implementation: string;
}

/**
 * Negotiated protocol-version + capability set for a remote peer, locked in
 * once the Hello/HelloAck exchange completes (or via the backward-compat
 * fallback for peers that never replied).
 *
 * The {@link negotiatedVersion} is the highest protocol version both sides
 * advertised support for. The {@link capabilities} set is the intersection
 * of both sides' advertised capability tags — services should gate optional
 * features (Double-Ratchet, DTN custody, voice, etc.) on capability presence
 * rather than on raw protocol-version.
 */
export interface PeerCapabilities {
  /** UHID of the peer this record describes. */
  peerUhid: string;
  /**
   * Highest mutually-supported protocol version. Defaults to 1 for peers
   * that never replied with a HelloAck (backward-compat).
   */
  negotiatedVersion: number;
  /**
   * Intersection of capability tags both sides claim to support. Empty for
   * peers that never replied.
   */
  capabilities: ReadonlySet<string>;
  /**
   * Free-form implementation banner the peer announced (e.g.
   * "aether-csharp/1.0.0"). Empty for peers that never replied.
   */
  implementationVersion: string;
  /** UTC timestamp when negotiation completed. */
  negotiatedAt: Date;
}

/**
 * Event fired when a peer's announced version range does not overlap with
 * ours — we cannot speak to them. Subscribers should drop the peer from
 * their connected-peer set.
 */
export interface IncompatiblePeerEvent {
  /** UHID of the incompatible peer. */
  peerUhid: string;
  /** Lowest version the peer claimed to support. */
  theirMinVersion: number;
  /** Highest version the peer claimed to support. */
  theirMaxVersion: number;
  /** Lowest version we accept. */
  ourMinVersion: number;
  /** Highest version we speak. */
  ourMaxVersion: number;
  /** Human-readable explanation for the mismatch. */
  reason: string;
}
