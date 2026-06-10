// SPDX-License-Identifier: MIT

/**
 * AetherNet Bandwidth Measurement Framework — per-transport estimator.
 *
 * Ported from:
 *   src/AetherNet.Transport/Bandwidth/BandwidthEstimator.cs
 *
 * BBRv3-inspired four-phase state machine.  JavaScript is single-threaded so
 * no mutex is needed — just plain object mutation.
 */

import {
  BandwidthConfidence,
  type BandwidthSample,
  type BandwidthProbeAck,
  makeBandwidthSample,
} from "./models.js";

// ── Constants ─────────────────────────────────────────────────────────────────

/** Number of delivery-rate samples kept in the BtlBw max-filter window. */
export const BTL_BW_WINDOW_SIZE = 10;

/** Minimum RTT window duration in milliseconds (BBRv3 ProbeRTT period). */
export const RT_PROP_WINDOW_MS = 10_000.0;

/** EWMA loss rate smoothing factor (α). */
export const LOSS_ALPHA = 0.10;

/** RFC 6298 SRTT smoothing factor (1/8). */
const SRTT_ALPHA = 0.125;

/** RFC 6298 RTTVAR smoothing factor (1/4). */
const RTT_VAR_BETA = 0.25;

/** 5 % improvement threshold for onSampleImproved callbacks. */
const IMPROVEMENT_THRESHOLD = 0.05;

// ── BandwidthEstimator ────────────────────────────────────────────────────────

/**
 * Per-transport link bandwidth estimator.
 *
 * Two observation paths:
 *   Passive — recordDelivery() feeds the BBRv3 delivery-rate filter.
 *   Active  — recordProbeResult() consumes ack timestamps to maintain
 *             estimates on idle links.
 *
 * AetherNet innovations:
 *   PHY-layer capping — applyPhyHint() constrains estimates before probes.
 *   Gossip warm-start — warmFromGossip() pre-seeds from a peer's value.
 *   Confidence tiers  — consumers can distinguish 1-probe from 30-round.
 */
export class BandwidthEstimator {
  // ── BtlBw max-filter: circular buffer of (rateBps, timestampMs) ──────────
  private readonly _btlBwWindow: Array<{ rateBps: bigint; timestampMs: number }>;
  private _btlBwHead = 0;
  private _btlBwCount = 0;

  // ── RTprop min-filter ────────────────────────────────────────────────────
  private readonly _rtPropSamples: Array<{ rttMs: number; timestampMs: number }> = [];

  // ── RFC 6298 SRTT / RTTVAR ───────────────────────────────────────────────
  private _srttMs = 0.0;
  private _rttVarMs = 0.0;
  private _firstRtt = true;

  // ── Loss EWMA ────────────────────────────────────────────────────────────
  private _lossRate = 0.0;

  // ── PHY cap ──────────────────────────────────────────────────────────────
  private _phyCapBps: bigint = 0n;

  // ── Confidence ───────────────────────────────────────────────────────────
  private _probeRounds = 0;

  // ── Snapshot cache ────────────────────────────────────────────────────────
  private _current: BandwidthSample;

  // ── Gossip warm-start flag ────────────────────────────────────────────────
  private _warmedFromGossip = false;

  /**
   * Callbacks invoked when BtlBw improves by ≥ 5 % or confidence advances.
   * Equivalent to the C# SampleImproved event.
   */
  public onSampleImproved: Array<(sample: BandwidthSample) => void> = [];

  constructor(
    public readonly transportName: string,
    private readonly _maxBandwidthBps: bigint,
  ) {
    this._btlBwWindow = Array.from({ length: BTL_BW_WINDOW_SIZE }, () => ({
      rateBps: 0n,
      timestampMs: 0,
    }));
    // Optimistic initialisation: start at theoretical max with None confidence.
    this._current = this._buildSnapshot(_maxBandwidthBps, 50.0);
  }

  // ── Public accessors ──────────────────────────────────────────────────────

  get currentSample(): BandwidthSample {
    return this._current;
  }

  // ── Observation feed ──────────────────────────────────────────────────────

  /**
   * Record a successful delivery of `bytes`.
   * Both timestamps are microseconds since Unix epoch on the same clock.
   */
  recordDelivery(bytes: number, sendUs: bigint, deliverUs: bigint): void {
    if (bytes <= 0 || deliverUs <= sendUs) return;

    const elapsedMs = Number(deliverUs - sendUs) / 1000.0;
    const deliveryRateBps = BigInt(Math.round((bytes * 8.0) / (elapsedMs / 1000.0)));
    const rttMs = elapsedMs; // one-way → treat as RTT estimate (conservative)

    this._addToBtlBwWindow(deliveryRateBps, this._nowMs());
    this._updateRttEstimates(rttMs);
    this._probeRounds++;
    this._commit();
  }

  /** Record that `bytes` were lost (timeout or explicit NAK). */
  recordLoss(bytes: number): void {
    if (bytes <= 0) return;
    this._lossRate = LOSS_ALPHA * 1.0 + (1 - LOSS_ALPHA) * this._lossRate;
    this._commit();
  }

  /**
   * Feed an active probe ack into the estimator.
   * `localReceiveUs` is the local clock µs at ACK receipt (currently unused
   * in the algorithm — kept for API symmetry with the C# reference).
   */
  recordProbeResult(ack: BandwidthProbeAck, localReceiveUs: bigint): void {
    void localReceiveUs; // intentionally unused; kept for API symmetry
    const rttMs = ack.rtt;
    if (rttMs <= 0 || rttMs > 30_000) return;

    // Delivery rate from probe: bytes × 8 / RTT
    const deliveryRateBps =
      ack.probeBytes > 0
        ? BigInt(Math.round((ack.probeBytes * 8.0) / (rttMs / 1000.0)))
        : 0n;

    this._updateRttEstimates(rttMs);
    if (deliveryRateBps > 0n) {
      this._addToBtlBwWindow(deliveryRateBps, this._nowMs());
    }
    this._probeRounds++;
    this._commit();
  }

  /**
   * Pre-warm from a gossip payload.
   * Only effective when confidence is None — never downgrades an existing estimate.
   * `rtPropMs` is milliseconds.
   */
  warmFromGossip(
    btlBwBps: bigint,
    rtPropMs: number,
    confidence: BandwidthConfidence,
  ): void {
    void confidence; // used for guard logic via _probeRounds / _warmedFromGossip
    if (this._probeRounds > 0 || this._warmedFromGossip) return; // never downgrade

    const now = this._nowMs();
    this._addToBtlBwWindow(btlBwBps, now);
    if (rtPropMs > 0) {
      this._srttMs = rtPropMs;
      this._rttVarMs = rtPropMs / 2.0;
      this._firstRtt = false;
      this._addToRtPropWindow(rtPropMs, now);
    }
    this._warmedFromGossip = true;
    this._commit();
  }

  /**
   * Apply a physical-layer hint.
   * RSSI-to-BtlBw caps the estimate before probes complete.
   * Uses the BLE calibration table as a conservative fallback.
   */
  applyPhyHint(rssiDbm: number): void {
    let cap: bigint;
    if      (rssiDbm >= -50) cap = 600_000_000n;
    else if (rssiDbm >= -67) cap = 200_000_000n;
    else if (rssiDbm >= -70) cap =   2_000_000n;
    else if (rssiDbm >= -80) cap =  54_000_000n;
    else if (rssiDbm >= -85) cap =     500_000n;
    else if (rssiDbm >= -95) cap =     125_000n;
    else                     cap =      40_000n;

    this._phyCapBps = cap;
    this._commit();
  }

  // ── Internal helpers ──────────────────────────────────────────────────────

  /**
   * RFC 6298 §2.3 RTT sample integration.
   * First sample initialises SRTT = R, RTTVAR = R/2.
   */
  private _updateRttEstimates(rttMs: number): void {
    if (this._firstRtt) {
      this._srttMs = rttMs;
      this._rttVarMs = rttMs / 2.0;
      this._firstRtt = false;
    } else {
      this._rttVarMs =
        (1 - RTT_VAR_BETA) * this._rttVarMs +
        RTT_VAR_BETA * Math.abs(this._srttMs - rttMs);
      this._srttMs = (1 - SRTT_ALPHA) * this._srttMs + SRTT_ALPHA * rttMs;
    }

    // Success sample → also update loss EWMA (0 loss observed).
    this._lossRate = LOSS_ALPHA * 0.0 + (1 - LOSS_ALPHA) * this._lossRate;

    this._addToRtPropWindow(rttMs, this._nowMs());
  }

  /**
   * Insert a delivery-rate sample into the max-filter window.
   * Discards samples older than 10×RTprop.
   */
  private _addToBtlBwWindow(rateBps: bigint, nowMs: number): void {
    const windowDurationMs = 10.0 * Math.max(1.0, this._minRtPropMs());
    const expiry = nowMs - windowDurationMs;

    // Evict expired entries from the tail of the circular buffer.
    while (this._btlBwCount > 0) {
      const tail =
        (this._btlBwHead + BTL_BW_WINDOW_SIZE - this._btlBwCount) %
        BTL_BW_WINDOW_SIZE;
      if (this._btlBwWindow[tail].timestampMs < expiry) {
        this._btlBwCount--;
      } else {
        break;
      }
    }

    this._btlBwWindow[this._btlBwHead] = { rateBps, timestampMs: nowMs };
    this._btlBwHead = (this._btlBwHead + 1) % BTL_BW_WINDOW_SIZE;
    if (this._btlBwCount < BTL_BW_WINDOW_SIZE) this._btlBwCount++;
  }

  private _addToRtPropWindow(rttMs: number, nowMs: number): void {
    this._rtPropSamples.push({ rttMs, timestampMs: nowMs });
    // Evict samples older than RT_PROP_WINDOW_MS.
    const expiry = nowMs - RT_PROP_WINDOW_MS;
    let removeCount = 0;
    for (const s of this._rtPropSamples) {
      if (s.timestampMs < expiry) removeCount++;
      else break;
    }
    if (removeCount > 0) this._rtPropSamples.splice(0, removeCount);
  }

  private _maxBtlBwBps(): bigint {
    if (this._btlBwCount === 0) return 0n;
    let max = 0n;
    for (let i = 0; i < this._btlBwCount; i++) {
      const idx =
        (this._btlBwHead + BTL_BW_WINDOW_SIZE - this._btlBwCount + i) %
        BTL_BW_WINDOW_SIZE;
      if (this._btlBwWindow[idx].rateBps > max)
        max = this._btlBwWindow[idx].rateBps;
    }
    return max;
  }

  private _minRtPropMs(): number {
    if (this._rtPropSamples.length === 0)
      return this._srttMs > 0 ? this._srttMs : 50.0;
    let min = Number.MAX_VALUE;
    for (const s of this._rtPropSamples) {
      if (s.rttMs < min) min = s.rttMs;
    }
    return min > 0 ? min : 1.0;
  }

  private _computeConfidence(): BandwidthConfidence {
    if (this._probeRounds === 0 && !this._warmedFromGossip)
      return BandwidthConfidence.None;
    if (this._probeRounds === 0) return BandwidthConfidence.Low;
    if (this._probeRounds < 5)  return BandwidthConfidence.Low;
    if (this._probeRounds < 20) return BandwidthConfidence.Medium;
    return BandwidthConfidence.High;
  }

  /** Rebuild the snapshot and fire onSampleImproved if significant. */
  private _commit(): void {
    const prev = this._current;
    this._current = this._buildSnapshot(this._maxBtlBwBps(), this._minRtPropMs());
    const cur = this._current;

    const prevBtl = prev.btlBwBps;
    const improved =
      prevBtl === 0n ||
      cur.btlBwBps - prevBtl > BigInt(Math.round(Number(prevBtl) * IMPROVEMENT_THRESHOLD)) ||
      cur.confidence > prev.confidence;

    if (improved && this.onSampleImproved.length > 0) {
      // Fire asynchronously (matches C# ThreadPool.QueueUserWorkItem behaviour).
      const snap = cur;
      Promise.resolve().then(() => {
        for (const cb of this.onSampleImproved) cb(snap);
      });
    }
  }

  private _buildSnapshot(btlBw: bigint, rtPropMs: number): BandwidthSample {
    const srttMs  = Math.max(1.0, this._srttMs);
    const rttVarMs = Math.max(0.0, this._rttVarMs);
    const lossClamp = Math.min(Math.max(this._lossRate, 0.0), 1.0);
    const available = BigInt(Math.round(Number(btlBw) * (1.0 - lossClamp)));
    const bdpBytes  = btlBw > 0n
      ? BigInt(Math.round(Number(btlBw) / 8.0 * (rtPropMs / 1000.0)))
      : 0n;
    const effective = this._phyCapBps > 0n
      ? (btlBw < this._phyCapBps ? btlBw : this._phyCapBps)
      : btlBw;
    const effectiveAvailable = BigInt(Math.round(Number(effective) * (1.0 - lossClamp)));

    return makeBandwidthSample({
      transportName: this.transportName,
      btlBwBps:      effective,
      availableBps:  effectiveAvailable,
      bdpBytes,
      srttMs,
      rttVarMs,
      rtPropMs,
      lossRate:      this._lossRate,
      phyCapBps:     this._phyCapBps,
      confidence:    this._computeConfidence(),
      measuredAt:    new Date(),
    });
  }

  private _nowMs(): number {
    return Date.now();
  }
}
