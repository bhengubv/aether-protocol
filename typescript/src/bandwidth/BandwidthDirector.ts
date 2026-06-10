// SPDX-License-Identifier: MIT

/**
 * AetherNet Bandwidth Measurement Framework — cross-transport director.
 *
 * Ported from:
 *   src/AetherNet.Transport/Bandwidth/BandwidthDirector.cs
 *
 * Maintains a (peerUhid × transportName) → BandwidthSample matrix and
 * provides transport recommendations + gossip coordination.
 */

import {
  BandwidthConfidence,
  type BandwidthSample,
  type BandwidthGossipPayload,
} from "./models.js";
import { type BandwidthEstimator } from "./BandwidthEstimator.js";

// ── Power cost table ──────────────────────────────────────────────────────────

/** Lower value = preferred. Mirrors ITransportService conventions. */
const DEFAULT_POWER_COSTS: ReadonlyMap<string, number> = new Map([
  ["NearLink",     1],
  ["BLE",          2],
  ["Wi-Fi Direct", 3],
  ["CircleLink",   3],
  ["QUIC Relay",  10],
  ["HTTP Relay",  10],
]);

function powerCostOf(transportName: string): number {
  const key = [...DEFAULT_POWER_COSTS.keys()].find(
    (k) => k.toLowerCase() === transportName.toLowerCase(),
  );
  return key !== undefined ? (DEFAULT_POWER_COSTS.get(key) ?? 5) : 5;
}

// ── BandwidthDirector ─────────────────────────────────────────────────────────

/**
 * Cross-transport bandwidth synthesis and mesh gossip coordinator.
 *
 * Capabilities:
 *   1. Multi-transport BDP matrix — answers "which transport to use for N bytes to peer X?"
 *   2. Mesh gossip pre-warming — seeds a new session with a non-zero estimate.
 */
export class BandwidthDirector {
  /** (peerUhid, transportName) → latest sample */
  private readonly _matrix = new Map<string, BandwidthSample>();

  /** transportName (lower-case) → estimator */
  private readonly _estimators = new Map<string, BandwidthEstimator>();

  // ── Registration ──────────────────────────────────────────────────────────

  /** Register an estimator. Called once per transport at startup. */
  register(estimator: BandwidthEstimator): void {
    this._estimators.set(estimator.transportName.toLowerCase(), estimator);

    estimator.onSampleImproved.push((sample) => {
      // When any estimator fires, update every known peer's entry for this transport.
      const transportKey = sample.transportName.toLowerCase();
      for (const [key] of this._matrix) {
        const [, t] = this._splitKey(key);
        if (t === transportKey) {
          this._matrix.set(key, sample);
        }
      }
    });
  }

  // ── Query ─────────────────────────────────────────────────────────────────

  /** Get the bandwidth estimate for a specific peer on a specific transport. */
  getEstimate(peerUhid: string, transport: string): BandwidthSample | null {
    return this._matrix.get(this._key(peerUhid, transport)) ?? null;
  }

  /**
   * Get all current estimates for a peer across all transports,
   * ranked by availableBps descending.
   */
  getEstimates(peerUhid: string): BandwidthSample[] {
    const peerKey = peerUhid.toLowerCase();
    const results: BandwidthSample[] = [];
    for (const [key, sample] of this._matrix) {
      const [p] = this._splitKey(key);
      if (p === peerKey) results.push(sample);
    }
    results.sort((a, b) => {
      if (b.availableBps > a.availableBps) return 1;
      if (b.availableBps < a.availableBps) return -1;
      return 0;
    });
    return results;
  }

  /**
   * Recommend the best transport for a payload of `payloadBytes`.
   * Scoring: AvailableBps / PowerCost × bdpBonus × confidenceFactor.
   */
  recommendTransport(peerUhid: string, payloadBytes: bigint): string | null {
    const candidates = this.getEstimates(peerUhid);

    if (candidates.length === 0) {
      // No measurement data — fall back to lowest power-cost registered transport.
      let best: BandwidthEstimator | null = null;
      let bestCost = Number.MAX_SAFE_INTEGER;
      for (const est of this._estimators.values()) {
        const cost = powerCostOf(est.transportName);
        if (cost < bestCost) {
          bestCost = cost;
          best = est;
        }
      }
      return best?.transportName ?? null;
    }

    let bestName: string | null = null;
    let bestScore = -Infinity;

    for (const s of candidates) {
      const powerCost = powerCostOf(s.transportName);
      const available = Number(s.availableBps);
      // Oversize payloads get a NEUTRAL 1.0 (not 0.0) so the available-bandwidth/
      // power term still ranks them — keeps transport selection byte-identical
      // across all 8 SDKs.
      const bdpBonus  = payloadBytes > s.bdpBytes ? 1.0 : 1.5;
      const confidenceFactor = s.confidence === BandwidthConfidence.None ? 0.5 : 1.0;
      const score = (available / powerCost) * bdpBonus * confidenceFactor;

      if (score > bestScore) {
        bestScore = score;
        bestName  = s.transportName;
      }
    }

    return bestName;
  }

  // ── Gossip ────────────────────────────────────────────────────────────────

  /**
   * Build a gossip payload for a new peer that has just completed handshake.
   * Returns null if no estimator exists for the transport, or if confidence is None.
   */
  buildGossipPayload(
    peerUhid: string,
    transportName: string,
  ): BandwidthGossipPayload | null {
    const estimator = this._estimators.get(transportName.toLowerCase());
    if (!estimator) return null;

    const s = estimator.currentSample;
    if (s.confidence === BandwidthConfidence.None) return null;

    const rtPropUs = BigInt(Math.round(s.rtPropMs * 1000.0));
    return {
      peerUhid,
      transportName,
      btlBwBps:   s.btlBwBps,
      rtPropUs,
      confidence: s.confidence,
      measuredAt: s.measuredAt,
    };
  }

  /** Receive and apply a gossip payload from a remote peer. */
  applyGossip(payload: BandwidthGossipPayload): void {
    const estimator = this._estimators.get(payload.transportName.toLowerCase());
    if (!estimator) return;

    const rtPropMs = Number(payload.rtPropUs) / 1000.0;
    estimator.warmFromGossip(payload.btlBwBps, rtPropMs, payload.confidence);

    // Seed the matrix so getEstimate returns something even before we probe.
    const key = this._key(payload.peerUhid, payload.transportName);
    this._matrix.set(key, estimator.currentSample);
  }

  // ── Internal helpers ──────────────────────────────────────────────────────

  private _key(peerUhid: string, transport: string): string {
    return `${peerUhid.toLowerCase()}::${transport.toLowerCase()}`;
  }

  private _splitKey(key: string): [string, string] {
    const sep = key.indexOf("::");
    return [key.slice(0, sep), key.slice(sep + 2)];
  }
}
