/**
 * ReputationGossipService — propagates peer reputation signals over the mesh.
 *
 * Nodes broadcast signed reputation-update packets (type 52) and apply
 * incoming updates weighted by the reporter's own reputation score, so that
 * low-reputation nodes cannot significantly affect the scores of others.
 *
 * SPDX-License-Identifier: MIT
 */

import type { NodeReputationService } from "./reputation.js";

// ── Constants ──────────────────────────────────────────────────────────────────

export const REPUTATION_UPDATE_TYPE = 52;

/** Packets older than this window are rejected as stale. */
const FRESHNESS_WINDOW_MS = 5 * 60 * 1000;

// ── Interfaces ─────────────────────────────────────────────────────────────────

export interface ReputationUpdatePayload {
  reporter_uhid: string;
  target_uhid: string;
  score_delta: number;
  timestamp_ms: number;
  reason: string;
}

export interface Packet {
  type: number;
  source_uhid: string;
  destination_uhid: string;
  ttl: number;
  payload: string;        // JSON string
  timestamp_ms: number;
  packet_nonce?: string;
  signature?: string;
}

export interface MeshSender {
  readonly localUhid: string;
  /** Broadcasts the packet to all reachable peers and returns the delivered count. */
  broadcast(packet: Packet): number;
}

export interface PacketSigner {
  signPacket(packet: Packet): Packet;
  verifyPacket(packet: Packet, senderPublicKey: Uint8Array): boolean;
}

// ── Service ────────────────────────────────────────────────────────────────────

export class ReputationGossipService {
  constructor(
    private readonly sender: MeshSender,
    private readonly signing: PacketSigner,
    private readonly reputation: NodeReputationService
  ) {}

  /**
   * Build, sign, and broadcast a reputation-update packet for the given target.
   *
   * @param targetUhid  The peer whose reputation is being reported.
   * @param scoreDelta  Proposed delta in [-1, 1]; clamped before sending.
   * @param reason      Human-readable description of the observed behaviour.
   * @returns           Number of peers the packet was delivered to.
   */
  broadcastReputationUpdate(
    targetUhid: string,
    scoreDelta: number,
    reason: string
  ): number {
    const clamped = Math.max(-1.0, Math.min(1.0, scoreDelta));

    const p: ReputationUpdatePayload = {
      reporter_uhid: this.sender.localUhid,
      target_uhid: targetUhid,
      score_delta: clamped,
      timestamp_ms: Date.now(),
      reason,
    };

    const packet: Packet = {
      type: REPUTATION_UPDATE_TYPE,
      source_uhid: this.sender.localUhid,
      destination_uhid: "*",
      ttl: 3,
      payload: JSON.stringify(p),
      timestamp_ms: Date.now(),
    };

    const signed = this.signing.signPacket(packet);
    return this.sender.broadcast(signed);
  }

  /**
   * Handle an incoming gossip packet from a mesh peer.
   *
   * Steps:
   *  1. Reject non-reputation-update packets.
   *  2. Verify the packet signature.
   *  3. Reject stale payloads (outside FRESHNESS_WINDOW_MS).
   *  4. Validate non-empty reporter and target UHIDs.
   *  5. Reject packets where the reporter is ourselves (no self-echo).
   *  6. Weight the claimed delta by the reporter's reputation score,
   *     defaulting to R=1.0 for unknown reporters.
   *  7. Apply the effective weighted delta to the target's reputation.
   *
   * @param packet          Received packet.
   * @param senderPublicKey Ed25519 public key of the sender for signature verification.
   * @returns               true if the update was applied, false if rejected.
   */
  handleGossipPacket(packet: Packet, senderPublicKey: Uint8Array): boolean {
    // 1. Only handle reputation-update packets
    if (packet.type !== REPUTATION_UPDATE_TYPE) {
      return false;
    }

    // 2. Verify signature
    if (!this.signing.verifyPacket(packet, senderPublicKey)) {
      return false;
    }

    // 3. Parse payload
    let p: ReputationUpdatePayload;
    try {
      p = JSON.parse(packet.payload) as ReputationUpdatePayload;
    } catch {
      return false;
    }

    // 4. Freshness check
    if (Math.abs(Date.now() - p.timestamp_ms) > FRESHNESS_WINDOW_MS) {
      return false;
    }

    // 5. Validate non-empty reporter and target
    if (!p.reporter_uhid || !p.target_uhid) {
      return false;
    }

    // 6. Reject self-reported gossip (no self-echo)
    if (p.reporter_uhid === this.sender.localUhid) {
      return false;
    }

    // 7. Clamp the claimed delta
    const clampedDelta = Math.max(-1.0, Math.min(1.0, p.score_delta));

    // 8. Weight by reporter reputation (unknown reporters default to R=1.0)
    const R = this.reputation.getReputationScore(p.reporter_uhid);
    const effectiveDelta = clampedDelta * R;

    // 9. Apply the weighted delta to the target
    this.reputation.applyWeightedDelta(p.target_uhid, effectiveDelta);

    return true;
  }
}
