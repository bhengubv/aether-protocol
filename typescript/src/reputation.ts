/**
 * NodeReputationService — per-UHID behavioral reputation scoring.
 *
 * Aggregates observed signals into a score in [0.0, 1.0].
 * Unknown peers default to 1.0 (benefit of the doubt).
 * Mirrors the C# InMemoryNodeReputationService in
 * src/AetherNet.Core/Reputation/InMemoryNodeReputationService.cs.
 *
 * SPDX-License-Identifier: MIT
 */

// Score deltas — negative signals
const DELTA_RREQ_FLOOD       = -0.05;
const DELTA_REPLAY           = -0.15;
const DELTA_SIG_FAILURE      = -0.20;
const DELTA_CUSTODY_REFUSAL  = -0.05;
const DELTA_DELIVERY_FAIL    = -0.02;

// Score deltas — positive signals
const DELTA_DELIVERY_OK      = +0.01;

/** Epsilon for snap-to-boundary clamping (matches C# 1e-12). */
const EPSILON = 1e-12;

export class NodeReputationService {
  private readonly scores: Map<string, number> = new Map();

  // ── Signal recorders ───────────────────────────────────────────────────────

  /** Record that a RREQ was rate-limited from the given UHID. */
  recordRreqFloodAttempt(uhid: string): void {
    this.applyDelta(uhid, DELTA_RREQ_FLOOD);
  }

  /** Record a duplicate-nonce replay attempt from the given UHID. */
  recordReplayAttempt(uhid: string): void {
    this.applyDelta(uhid, DELTA_REPLAY);
  }

  /** Record an Ed25519 signature verification failure from the given UHID. */
  recordSignatureFailure(uhid: string): void {
    this.applyDelta(uhid, DELTA_SIG_FAILURE);
  }

  /** Record a DTN custody refusal by the given UHID. */
  recordCustodyRefusal(uhid: string): void {
    this.applyDelta(uhid, DELTA_CUSTODY_REFUSAL);
  }

  /**
   * Record a confirmed successful delivery via the given UHID.
   * @param uhid        The peer's UHID.
   * @param roundTripMs Observed round-trip time in milliseconds (reserved for
   *                    future EWMA weighting; currently unused in scoring).
   */
  recordDeliverySuccess(uhid: string, roundTripMs: number): void {
    this.applyDelta(uhid, DELTA_DELIVERY_OK);
  }

  /** Record a delivery failure (lost bundle / unacknowledged hop) via the given UHID. */
  recordDeliveryFailure(uhid: string): void {
    this.applyDelta(uhid, DELTA_DELIVERY_FAIL);
  }

  // ── Score queries ──────────────────────────────────────────────────────────

  /**
   * Returns the current reputation score for the given UHID in [0.0, 1.0].
   * Returns 1.0 for unknown peers (benefit of the doubt until signals arrive).
   */
  getReputationScore(uhid: string): number {
    return this.scores.has(uhid) ? this.scores.get(uhid)! : 1.0;
  }

  /**
   * Returns a snapshot copy of all known reputation scores.
   * Mutations to the returned Map do not affect internal state.
   */
  getAllScores(): Map<string, number> {
    return new Map(this.scores);
  }

  // ── Gossip helpers ─────────────────────────────────────────────────────────

  /**
   * Apply a pre-weighted delta (already multiplied by the reporter's reputation)
   * to the target UHID's score. The delta is clamped to [-1.0, 1.0] before
   * being forwarded to the private applyDelta helper.
   */
  applyWeightedDelta(uhid: string, weightedDelta: number): void {
    const clamped = Math.max(-1.0, Math.min(1.0, weightedDelta));
    this.applyDelta(uhid, clamped);
  }

  // ── Private helpers ────────────────────────────────────────────────────────

  /**
   * Clamp v to [0.0, 1.0] and snap near-boundary float values to exact 0 or 1
   * to prevent scores like 5.5e-17 from accumulating.
   */
  private clampScore(v: number): number {
    const clamped = Math.min(1.0, Math.max(0.0, v));
    if (clamped < EPSILON) return 0.0;
    if (clamped > 1.0 - EPSILON) return 1.0;
    return clamped;
  }

  /**
   * Apply a signed delta to the given UHID's score, initialising from 1.0
   * if the peer is not yet known.
   */
  private applyDelta(uhid: string, delta: number): void {
    const current = this.scores.has(uhid) ? this.scores.get(uhid)! : 1.0;
    this.scores.set(uhid, this.clampScore(current + delta));
  }
}
