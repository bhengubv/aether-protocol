// SPDX-License-Identifier: MIT

/**
 * AetherNet Bandwidth Measurement Framework — node activity monitor.
 *
 * Ported from:
 *   src/AetherNet.Transport/Bandwidth/NodeActivityMonitor.cs
 *
 * Runs a setInterval loop at `sampleIntervalMs` (default 500 ms).
 * Each tick computes ingress/egress rates from byte counters and publishes
 * a NodeActivitySnapshot.
 */

import {
  NodeActivityState,
  type NodeActivitySnapshot,
  type TransportActivitySnapshot,
  type BandwidthSample,
  makeTransportActivitySnapshot,
  makeNodeActivitySnapshot,
} from "./models.js";
import { type BandwidthEstimator } from "./BandwidthEstimator.js";

// ── Transport traffic accumulators ────────────────────────────────────────────

interface TransportTraffic {
  ingressBytes: number;
  egressBytes: number;
  lastEgressMs: number;
}

// ── NodeActivityMonitor ───────────────────────────────────────────────────────

/**
 * Observable node activity monitor — the UI-facing layer of the ABMF.
 *
 * Consumption patterns:
 *   Status bar / widget — poll current every 1 s.
 *   Reactive UI        — subscribe(cb); unsubscribe via returned teardown.
 *   ABR controller     — subscribe, watch for Degraded state.
 */
export class NodeActivityMonitor {
  // ── Configuration ─────────────────────────────────────────────────────────
  sampleIntervalMs = 500;
  idleThresholdSeconds = 5;

  // ── Registered transports ─────────────────────────────────────────────────
  private readonly _transports = new Map<
    string,
    { estimator: BandwidthEstimator; traffic: TransportTraffic }
  >();

  // ── Active-peer tracking ──────────────────────────────────────────────────
  // Maps peerUhid → last-seen Unix ms. A peer is "active" if it had ingress or
  // egress within idleThresholdSeconds. Populated only by the peer-aware
  // recordIngressFromPeer/recordEgressToPeer methods; the transport-only
  // methods do not contribute (the caller did not supply a peer). Stale
  // entries are pruned each tick so the map stays bounded by recently-active
  // peers, not the lifetime peer set.
  private readonly lastSeenPeerMs = new Map<string, number>();

  // ── Timer ─────────────────────────────────────────────────────────────────
  private _timer: ReturnType<typeof setInterval> | null = null;
  private _lastTickMs = 0;

  // ── Snapshot ──────────────────────────────────────────────────────────────
  private _current: NodeActivitySnapshot = this._offlineSnapshot();

  // ── Subscribers ───────────────────────────────────────────────────────────
  private readonly _subscribers: Array<(snap: NodeActivitySnapshot) => void> = [];

  // ── Public API ────────────────────────────────────────────────────────────

  /** The most recent snapshot. */
  get current(): NodeActivitySnapshot {
    return this._current;
  }

  /** Register a transport's estimator. Called once per transport at startup. */
  register(name: string, estimator: BandwidthEstimator): void {
    this._transports.set(name.toLowerCase(), {
      estimator,
      traffic: {
        ingressBytes: 0,
        egressBytes:  0,
        lastEgressMs: Date.now(),
      },
    });
  }

  /** Record inbound bytes on a transport. Call from transport receive path. */
  recordIngress(transport: string, bytes: number): void {
    const entry = this._transports.get(transport.toLowerCase());
    if (entry) entry.traffic.ingressBytes += bytes;
  }

  /** Record outbound bytes on a transport. Call from transport send path. */
  recordEgress(transport: string, bytes: number): void {
    const entry = this._transports.get(transport.toLowerCase());
    if (entry) {
      entry.traffic.egressBytes += bytes;
      entry.traffic.lastEgressMs = Date.now();
    }
  }

  /**
   * Record inbound bytes on a transport from a specific peer.
   * Tracks the peer for the NodeActivitySnapshot.activePeers count.
   */
  recordIngressFromPeer(transport: string, peerUhid: string, bytes: number): void {
    this.recordIngress(transport, bytes);
    if (peerUhid) this.lastSeenPeerMs.set(peerUhid, Date.now());
  }

  /**
   * Record outbound bytes on a transport to a specific peer.
   * Tracks the peer for the NodeActivitySnapshot.activePeers count.
   */
  recordEgressToPeer(transport: string, peerUhid: string, bytes: number): void {
    this.recordEgress(transport, bytes);
    if (peerUhid) this.lastSeenPeerMs.set(peerUhid, Date.now());
  }

  /** Start the background sampling loop. */
  start(): void {
    if (this._timer !== null) return;
    this._lastTickMs = Date.now();
    this._timer = setInterval(() => this._onTick(), this.sampleIntervalMs);
  }

  /** Stop the background sampling loop. */
  stop(): void {
    if (this._timer !== null) {
      clearInterval(this._timer);
      this._timer = null;
    }
  }

  /**
   * Subscribe to snapshot updates.
   * Returns an unsubscribe teardown function.
   */
  subscribe(cb: (snap: NodeActivitySnapshot) => void): () => void {
    this._subscribers.push(cb);
    return () => {
      const idx = this._subscribers.indexOf(cb);
      if (idx !== -1) this._subscribers.splice(idx, 1);
    };
  }

  // ── Timer callback ────────────────────────────────────────────────────────

  private _onTick(): void {
    const nowMs = Date.now();
    const elapsedSec = Math.max(0.001, (nowMs - this._lastTickMs) / 1000.0);
    this._lastTickMs = nowMs;

    const transportSnapshots: TransportActivitySnapshot[] = [];
    let totalIngress = 0n;
    let totalEgress  = 0n;
    let activeTransports = 0;
    const idleThresholdMs = this.idleThresholdSeconds * 1000;

    // Count distinct peers active within the idle window; prune stale entries
    // so the map stays bounded by recently-active peers.
    let activePeers = 0;
    for (const [peerUhid, lastSeenMs] of this.lastSeenPeerMs) {
      if (nowMs - lastSeenMs < idleThresholdMs) activePeers++;
      else this.lastSeenPeerMs.delete(peerUhid);
    }

    for (const [name, { estimator, traffic }] of this._transports) {
      // Sample and reset byte counters.
      const ingressDelta = traffic.ingressBytes;
      const egressDelta  = traffic.egressBytes;
      traffic.ingressBytes = 0;
      traffic.egressBytes  = 0;

      const ingressBps = BigInt(Math.round((ingressDelta * 8.0) / elapsedSec));
      const egressBps  = BigInt(Math.round((egressDelta  * 8.0) / elapsedSec));

      const sample = estimator.currentSample;
      const utilFraction =
        sample.btlBwBps > 0n
          ? Math.min(Number(egressBps) / Number(sample.btlBwBps), 1.0)
          : 0.0;

      const isRecent = nowMs - traffic.lastEgressMs < idleThresholdMs;
      const state = this._computeTransportState(egressBps, ingressBps, sample, isRecent);

      if (state !== NodeActivityState.Offline && state !== NodeActivityState.Idle) {
        activeTransports++;
      }

      totalIngress += ingressBps;
      totalEgress  += egressBps;

      transportSnapshots.push(
        makeTransportActivitySnapshot({
          transportName:       name,
          isAvailable:         true,
          ingressBps,
          egressBps,
          srttMs:              sample.srttMs,
          btlBwBps:            sample.btlBwBps,
          utilizationFraction: utilFraction,
          state,
          confidence:          sample.confidence,
        }),
      );
    }

    const nodeState = this._computeNodeState(transportSnapshots);

    let primaryTransportName: string | null = null;
    if (transportSnapshots.length > 0) {
      let maxEgress = -1n;
      for (const t of transportSnapshots) {
        if (t.egressBps > maxEgress) {
          maxEgress = t.egressBps;
          primaryTransportName = t.transportName;
        }
      }
    }

    const prev = this._current;
    const snapshot = makeNodeActivitySnapshot({
      state:                nodeState,
      ingressBps:           totalIngress,
      egressBps:            totalEgress,
      activePeers,
      activeTransports,
      transports:           transportSnapshots,
      primaryTransportName,
      timestamp:            new Date(nowMs),
    });
    this._current = snapshot;

    // Notify all subscribers.
    const changed =
      snapshot.state !== prev.state ||
      Math.abs(Number(snapshot.totalBps - prev.totalBps)) > 1_000 ||
      snapshot.activeTransports !== prev.activeTransports;

    if (changed) {
      for (const cb of this._subscribers) {
        try { cb(snapshot); } catch { /* subscriber errors must not kill the timer */ }
      }
    }
  }

  // ── State computation ─────────────────────────────────────────────────────

  private _computeTransportState(
    egressBps: bigint,
    ingressBps: bigint,
    sample: BandwidthSample,
    isRecent: boolean,
  ): NodeActivityState {
    if (!isRecent && egressBps === 0n && ingressBps === 0n)
      return NodeActivityState.Idle;
    if (egressBps === 0n && ingressBps === 0n)
      return NodeActivityState.Idle;

    if (sample.lossRate > 0.05) return NodeActivityState.Degraded;

    const util =
      sample.btlBwBps > 0n
        ? Number(egressBps) / Number(sample.btlBwBps)
        : 0.0;

    return util >= 0.5 ? NodeActivityState.Busy : NodeActivityState.Active;
  }

  private _computeNodeState(
    transports: ReadonlyArray<TransportActivitySnapshot>,
  ): NodeActivityState {
    if (transports.length === 0) return NodeActivityState.Offline;
    if (transports.some((t) => t.state === NodeActivityState.Degraded))
      return NodeActivityState.Degraded;
    if (transports.some((t) => t.state === NodeActivityState.Busy))
      return NodeActivityState.Busy;
    if (transports.some((t) => t.state === NodeActivityState.Active))
      return NodeActivityState.Active;
    if (transports.every((t) => t.state === NodeActivityState.Offline))
      return NodeActivityState.Offline;
    return NodeActivityState.Idle;
  }

  // ── Static helpers ────────────────────────────────────────────────────────

  private _offlineSnapshot(): NodeActivitySnapshot {
    return makeNodeActivitySnapshot({
      state:                NodeActivityState.Offline,
      ingressBps:           0n,
      egressBps:            0n,
      activePeers:          0,
      activeTransports:     0,
      transports:           [],
      primaryTransportName: null,
      timestamp:            new Date(),
    });
  }
}
