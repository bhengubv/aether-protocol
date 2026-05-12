/**
 * BehavioralAnomalyDetector — translates mesh behavioral signals into
 * NodeReputationService reputation signals.
 *
 * Detectable patterns:
 *  1. Volume spike  — packet rate exceeds spike_multiplier × EWMA baseline in
 *                     a rolling volumeWindowMs window → recordRreqFloodAttempt
 *  2. Dest scatter  — single source contacts > scatterThreshold unique
 *                     destinations in scatterWindowMs → recordRreqFloodAttempt
 *  3. Geohash mismatch — claimed geohash prefix ≠ observed routing prefix
 *                        (rate-limited to one signal per geohashRateLimitMs
 *                        per node) → recordSignatureFailure
 *  4. SPK-sig failure — direct passthrough → recordSignatureFailure
 *
 * SPDX-License-Identifier: MIT
 */

import type { NodeReputationService } from "./reputation.js";

// ── Options ────────────────────────────────────────────────────────────────────

export interface AnomalyDetectorOptions {
  /** Rolling window for volume-spike detection (ms). Default: 30_000 */
  volumeWindowMs?: number;
  /** Multiplier over EWMA baseline that constitutes a spike. Default: 5.0 */
  volumeSpikeMultiplier?: number;
  /** EWMA smoothing factor α ∈ (0,1). Default: 0.20 */
  ewmaAlpha?: number;
  /** Sliding window for destination-scatter detection (ms). Default: 60_000 */
  scatterWindowMs?: number;
  /** Unique destination threshold that triggers a flood signal. Default: 50 */
  scatterThreshold?: number;
  /** Number of leading geohash chars used for prefix comparison. Default: 4 */
  geohashPrefixLength?: number;
  /**
   * Minimum ms between geohash-mismatch signals for the same UHID.
   * Default: 60_000. Use Infinity to suppress all subsequent signals after
   * the first.
   */
  geohashRateLimitMs?: number;
}

// ── Per-node volume state ──────────────────────────────────────────────────────

interface VolumeState {
  windowStartMs: number;
  windowCount: number;
  ewmaBaseline: number;
}

// ── Detector ───────────────────────────────────────────────────────────────────

export class BehavioralAnomalyDetector {
  private readonly reputation: NodeReputationService;

  // Resolved options
  private readonly volumeWindowMs: number;
  private readonly volumeSpikeMultiplier: number;
  private readonly ewmaAlpha: number;
  private readonly scatterWindowMs: number;
  private readonly scatterThreshold: number;
  private readonly geohashPrefixLength: number;
  private readonly geohashRateLimitMs: number;

  // Per-UHID volume tracking
  private readonly volumeState: Map<string, VolumeState> = new Map();

  // Per-UHID scatter tracking (chronologically ordered)
  private readonly scatterEntries: Map<
    string,
    Array<{ destination: string; timestampMs: number }>
  > = new Map();

  // Per-UHID: timestamp of last emitted geohash-mismatch signal
  private readonly geoLastSignalMs: Map<string, number> = new Map();

  constructor(
    reputation: NodeReputationService,
    options?: AnomalyDetectorOptions
  ) {
    this.reputation = reputation;
    this.volumeWindowMs = options?.volumeWindowMs ?? 30_000;
    this.volumeSpikeMultiplier = options?.volumeSpikeMultiplier ?? 5.0;
    this.ewmaAlpha = options?.ewmaAlpha ?? 0.20;
    this.scatterWindowMs = options?.scatterWindowMs ?? 60_000;
    this.scatterThreshold = options?.scatterThreshold ?? 50;
    this.geohashPrefixLength = options?.geohashPrefixLength ?? 4;
    this.geohashRateLimitMs = options?.geohashRateLimitMs ?? 60_000;
  }

  /**
   * Observe a routed packet from sourceUhid to destinationUhid at timestampMs.
   * Evaluates the volume-spike and destination-scatter patterns.
   */
  observePacket(
    sourceUhid: string,
    destinationUhid: string,
    timestampMs: number
  ): void {
    this.checkVolumeSpike(sourceUhid, timestampMs);
    this.checkDestinationScatter(sourceUhid, destinationUhid, timestampMs);
  }

  /**
   * Observe a node's claimed geohash vs the geohash inferred from its routing
   * behaviour.  Emits a signature-failure signal when the leading
   * `geohashPrefixLength` characters differ, subject to per-node rate-limiting.
   *
   * @param nowMs  Current time in ms (injectable for testing; defaults to Date.now()).
   */
  observeGeohashClaim(
    uhid: string,
    claimedGeohash: string,
    observedRoutingGeohash: string,
    nowMs: number = Date.now()
  ): void {
    const claimedPrefix = claimedGeohash.slice(0, this.geohashPrefixLength);
    const observedPrefix = observedRoutingGeohash.slice(0, this.geohashPrefixLength);

    if (claimedPrefix === observedPrefix) return; // prefixes match — no issue

    // Rate-limit: suppress if we signalled within the last geohashRateLimitMs
    const lastMs = this.geoLastSignalMs.get(uhid);
    if (lastMs !== undefined && nowMs - lastMs < this.geohashRateLimitMs) {
      return; // still within the quiet period
    }

    this.geoLastSignalMs.set(uhid, nowMs);
    this.reputation.recordSignatureFailure(uhid);
  }

  /**
   * Directly record an SPK signature failure for the given UHID.
   * Passes straight through to the reputation service with no rate-limiting.
   */
  observeSpkSigFailure(uhid: string): void {
    this.reputation.recordSignatureFailure(uhid);
  }

  // ── Private helpers ────────────────────────────────────────────────────────

  /** Check whether sourceUhid's packet rate has spiked vs its EWMA baseline. */
  private checkVolumeSpike(sourceUhid: string, timestampMs: number): void {
    let state = this.volumeState.get(sourceUhid);

    if (!state) {
      // First packet — open a fresh window, no baseline yet
      state = { windowStartMs: timestampMs, windowCount: 1, ewmaBaseline: 0 };
      this.volumeState.set(sourceUhid, state);
      return;
    }

    if (timestampMs - state.windowStartMs < this.volumeWindowMs) {
      // Still in the current window
      state.windowCount += 1;
    } else {
      // Window has closed: evaluate spike, update EWMA, open new window.
      const completedCount = state.windowCount;
      const prevEwma = state.ewmaBaseline;

      // Spike check: requires an established baseline (ewmaBaseline > 0)
      if (prevEwma > 0 && completedCount > this.volumeSpikeMultiplier * prevEwma) {
        this.reputation.recordRreqFloodAttempt(sourceUhid);
      }

      // Update EWMA: seed with first real observation when no prior baseline
      const newEwma =
        prevEwma === 0
          ? completedCount
          : this.ewmaAlpha * completedCount + (1 - this.ewmaAlpha) * prevEwma;

      // Open fresh window
      state.windowStartMs = timestampMs;
      state.windowCount = 1;
      state.ewmaBaseline = newEwma;
    }
  }

  /** Check whether sourceUhid has scattered to too many unique destinations. */
  private checkDestinationScatter(
    sourceUhid: string,
    destinationUhid: string,
    timestampMs: number
  ): void {
    let entries = this.scatterEntries.get(sourceUhid);
    if (!entries) {
      entries = [];
      this.scatterEntries.set(sourceUhid, entries);
    }

    // Evict observations older than the sliding window (entries are appended
    // in chronological order so we can trim from the front)
    const cutoff = timestampMs - this.scatterWindowMs;
    let trimIdx = 0;
    while (trimIdx < entries.length && entries[trimIdx].timestampMs <= cutoff) {
      trimIdx++;
    }
    if (trimIdx > 0) entries.splice(0, trimIdx);

    // Record this observation
    entries.push({ destination: destinationUhid, timestampMs });

    // Trigger if unique destination count exceeds threshold
    const uniqueDests = new Set(entries.map((e) => e.destination));
    if (uniqueDests.size > this.scatterThreshold) {
      this.reputation.recordRreqFloodAttempt(sourceUhid);
    }
  }
}
