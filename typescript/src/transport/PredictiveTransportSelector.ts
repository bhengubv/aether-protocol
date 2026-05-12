/**
 * Predictive transport selector — 2-state Kalman RTT filter over PerTransportMetrics.
 * SPDX-License-Identifier: MIT
 *
 * Why Kalman over EWMA?
 * ─────────────────────
 * EWMA is a 1-pole IIR that smooths history but cannot predict future RTT when a
 * link is actively degrading.  The Kalman filter models RTT as a constant-velocity
 * process [rtt, drift]:
 *
 *   x_t = F × x_{t−1} + w   (F = [[1,1],[0,1]])
 *   z_t = H × x_t   + v    (H = [1,0])
 *
 * Positive drift signals a rising RTT *before* it exceeds a threshold, enabling
 * proactive transport switching.  The posterior variance further penalises
 * uncertain links even when their point estimate looks good.
 *
 * Score formula:
 *   (effectiveBps / powerCost) × (1 − lossRate) / max(kalmanRtt, 1) × (1 / (1 + σ/100))
 *
 * TypeScript is single-threaded — no locking needed.
 */

import { type ITransportService } from './ITransportService.js';

// ── KalmanRttFilter ───────────────────────────────────────────────────────────

/** @internal */
class KalmanRttFilter {
  private readonly qRtt: number;
  private readonly qDrift: number;
  private readonly r: number;

  private _rtt: number;
  private _drift: number;
  private _p00: number;
  private _p01: number;
  private _p11: number;

  constructor(
    initialRttMs = 200.0,
    qRtt   = 25.0,
    qDrift =  5.0,
    r      = 100.0,
  ) {
    this._rtt   = initialRttMs;
    this._drift = 0.0;
    this._p00   = 400.0;
    this._p01   = 0.0;
    this._p11   = 100.0;
    this.qRtt   = qRtt;
    this.qDrift = qDrift;
    this.r      = r;
  }

  get rttEstimateMs(): number { return this._rtt; }
  get driftMs():       number { return this._drift; }
  get rttVariance():   number { return this._p00; }

  /**
   * Incorporate a new RTT measurement and return the updated estimate.
   * Full Kalman predict→update cycle.
   */
  update(measuredRttMs: number): number {
    // ── 1. Predict ────────────────────────────────────────────────────────────
    const rttPred   = this._rtt + this._drift;
    const driftPred = this._drift;

    const pp00 = this._p00 + 2.0 * this._p01 + this._p11 + this.qRtt;
    const pp01 = this._p01 + this._p11;
    const pp11 = this._p11 + this.qDrift;

    // ── 2. Kalman gain (H = [1, 0]) ──────────────────────────────────────────
    const S  = pp00 + this.r;
    const k0 = pp00 / S;
    const k1 = pp01 / S;

    // ── 3. Update ─────────────────────────────────────────────────────────────
    const innovation = measuredRttMs - rttPred;
    this._rtt   = rttPred   + k0 * innovation;
    this._drift = driftPred + k1 * innovation;

    // P = (I − K·H) × P_pred   (I − K·H = [[1−k0, 0], [−k1, 1]])
    this._p00 = (1.0 - k0) * pp00;
    this._p01 = (1.0 - k0) * pp01;
    this._p11 = -k1 * pp01 + pp11;

    // Clamp to prevent numerical drift below zero.
    this._p00 = Math.max(this._p00, 1e-6);
    this._p11 = Math.max(this._p11, 1e-6);

    return this._rtt;
  }
}

// ── PredictiveTransportSelector ───────────────────────────────────────────────

/** A transport paired with its Kalman-predictive score and uncertainty metadata. */
export interface PredictedRankedTransport {
  transport:      ITransportService;
  score:          number;
  predictedRttMs: number;
  rttVariance:    number;
}

/**
 * Predictive transport selector using per-transport Kalman RTT filters.
 *
 * Extends {@link rankTransports} by replacing the EWMA RTT term with a
 * Kalman-estimated RTT and adding a reliability penalty proportional to the
 * RTT variance.
 *
 * TypeScript is single-threaded so all operations are race-free within one
 * event-loop turn — no locking required.
 */
export class PredictiveTransportSelector {
  private readonly filters = new Map<ITransportService, KalmanRttFilter>();

  // ── Registration ──────────────────────────────────────────────────────────

  register(transport: ITransportService, initialRttMs = 200.0): void {
    if (!this.filters.has(transport)) {
      this.filters.set(transport, new KalmanRttFilter(initialRttMs));
    }
  }

  unregister(transport: ITransportService): void {
    this.filters.delete(transport);
  }

  // ── Observation ───────────────────────────────────────────────────────────

  /**
   * Feeds a new RTT measurement into both the transport's PerTransportMetrics
   * EWMA and our Kalman filter.  Call after every completed send.
   */
  observeMetrics(
    transport:        ITransportService,
    rttMs:            number,
    success:          boolean,
    bytesTransferred: number,
  ): void {
    transport.metrics?.recordSample(rttMs, success, bytesTransferred);

    if (rttMs <= 0 || !success) return;

    this.filters.get(transport)?.update(rttMs);
  }

  // ── Ranking ───────────────────────────────────────────────────────────────

  /**
   * Returns transports in descending predictive-score order.
   *
   * Only available transports are included.  `payloadBytes` is used to
   * exclude transports too slow to deliver this payload within 30 s.
   */
  rank(payloadBytes = 512): PredictedRankedTransport[] {
    const result: PredictedRankedTransport[] = [];

    for (const [transport, filter] of this.filters) {
      if (!transport.isAvailable) continue;

      const bw = transport.maxBandwidthBps;
      if (bw > 0) {
        const serialSec = (payloadBytes * 8) / bw;
        if (serialSec > 30) continue;
      }

      const kalmanRtt = Math.max(filter.rttEstimateMs, 1.0);
      const variance  = filter.rttVariance;
      const stddev    = Math.sqrt(variance);
      const power     = Math.max(transport.powerCostRelative, 1);

      let lossRate: number;
      let effectiveBps: number;

      if (transport.metrics) {
        lossRate    = transport.metrics.ewmaLossRate;
        effectiveBps = Math.max(transport.metrics.ewmaThroughputBps, bw * 0.1);
      } else {
        lossRate    = 0.05;
        effectiveBps = bw * 0.1;
      }

      // Reliability factor: 1.0 at σ=0 ms, ~0.5 at σ=100 ms.
      const reliabilityFactor = 1.0 / (1.0 + stddev / 100.0);
      const score = (effectiveBps / power) * (1.0 - lossRate) / kalmanRtt * reliabilityFactor;

      result.push({ transport, score, predictedRttMs: kalmanRtt, rttVariance: variance });
    }

    return result.sort((a, b) => b.score - a.score);
  }

  /**
   * Returns the highest-scoring available transport, or `undefined` if none.
   */
  selectBest(payloadBytes = 512): ITransportService | undefined {
    const ranked = this.rank(payloadBytes);
    return ranked.length > 0 ? ranked[0].transport : undefined;
  }

  /**
   * Returns the Kalman state `{rttMs, driftMs, variance}` for a registered
   * transport, or `undefined` if the transport is not registered.
   */
  getKalmanState(
    transport: ITransportService,
  ): { rttMs: number; driftMs: number; variance: number } | undefined {
    const f = this.filters.get(transport);
    if (!f) return undefined;
    return { rttMs: f.rttEstimateMs, driftMs: f.driftMs, variance: f.rttVariance };
  }
}
