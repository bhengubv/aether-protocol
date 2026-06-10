// SPDX-License-Identifier: MIT

/**
 * AetherNet Bandwidth Measurement Framework — model types.
 *
 * Ported from:
 *   src/AetherNet.Core/Bandwidth/BandwidthModels.cs
 *
 * All BigInt fields carry bps values that may exceed Number.MAX_SAFE_INTEGER
 * on 100 Gbps+ links.  TimeSpan → number (milliseconds).
 */

// ── Confidence ────────────────────────────────────────────────────────────────

/**
 * How confident we are in the current bandwidth estimate.
 * Rises with probe rounds; resets on topology change or extended idle.
 */
export enum BandwidthConfidence {
  None   = 0,
  Low    = 1,
  Medium = 2,
  High   = 3,
}

// ── BandwidthSample ───────────────────────────────────────────────────────────

/**
 * Point-in-time bandwidth measurement for a single transport link.
 *
 * Derivation follows BBRv3 (draft-cardwell-iccrg-bbr-congestion-control-02):
 *   - btlBwBps   — max delivery rate over 10×RTprop window.
 *   - rtPropMs   — minimum RTT observed in last 10 s (ProbeRTT window).
 *   - srttMs     — RFC 6298 smoothed RTT (α = 1/8).
 *   - rttVarMs   — RFC 6298 mean deviation (β = 1/4).
 *
 * AetherNet extensions:
 *   - bdpBytes   — pre-computed BDP so callers never have to re-derive it.
 *   - phyCapBps  — PHY-layer cap from RSSI mapping; 0n if unknown.
 *   - confidence — explicit quality tier used by ABR.
 */
export interface BandwidthSample {
  readonly transportName: string;

  /** BBRv3 BtlBw: maximum sustained delivery rate (bps). */
  readonly btlBwBps: bigint;

  /** Available bandwidth ceiling: btlBwBps × (1 − lossRate). */
  readonly availableBps: bigint;

  /** Bandwidth-Delay Product: btlBwBps × rtProp / 8 (bytes). */
  readonly bdpBytes: bigint;

  /** RFC 6298 smoothed RTT in milliseconds. */
  readonly srttMs: number;

  /** RFC 6298 RTT mean deviation (RTTVAR) in milliseconds. */
  readonly rttVarMs: number;

  /** BBRv3 RTprop: minimum observed RTT over the last 10 s (milliseconds). */
  readonly rtPropMs: number;

  /** EWMA fractional loss rate [0, 1]; α = 0.10. */
  readonly lossRate: number;

  /** PHY-layer bandwidth cap from RSSI hints (bps). 0n = unknown. */
  readonly phyCapBps: bigint;

  readonly confidence: BandwidthConfidence;
  readonly measuredAt: Date;

  /**
   * RFC 6298 §2.4 RTO: SRTT + max(G, 4×RTTVAR), G = 1 ms clock granularity.
   * Clamped to [200 ms, 60 s] per §2.4.
   * Returns milliseconds.
   */
  readonly rto: number;

  /** Effective bandwidth: min(btlBwBps, phyCapBps) if phyCapBps > 0, else btlBwBps. */
  readonly effectiveBps: bigint;
}

/** Factory to create an immutable BandwidthSample. */
export function makeBandwidthSample(fields: {
  transportName: string;
  btlBwBps: bigint;
  availableBps: bigint;
  bdpBytes: bigint;
  srttMs: number;
  rttVarMs: number;
  rtPropMs: number;
  lossRate: number;
  phyCapBps: bigint;
  confidence: BandwidthConfidence;
  measuredAt: Date;
}): BandwidthSample {
  const rtoRaw = fields.srttMs + Math.max(1.0, 4.0 * fields.rttVarMs);
  const rto = Math.min(Math.max(rtoRaw, 200.0), 60_000.0);
  const effectiveBps = fields.phyCapBps > 0n
    ? (fields.btlBwBps < fields.phyCapBps ? fields.btlBwBps : fields.phyCapBps)
    : fields.btlBwBps;

  return { ...fields, rto, effectiveBps };
}

// ── Probe wire models ─────────────────────────────────────────────────────────

/**
 * Four-timestamp probe ACK for two-way delay / RTT measurement (RFC 5136 §3).
 * All timestamps are microseconds since Unix epoch on each peer's local clock.
 * Clock synchronisation is not required — RTT is computed from sender-side
 * timestamps only.
 */
export interface BandwidthProbeAck {
  readonly sequence: number;
  readonly senderSendUs: bigint;
  readonly receiverReceiveUs: bigint;
  readonly receiverSendUs: bigint;
  readonly senderReceiveUs: bigint;
  readonly probeBytes: number;

  /**
   * Round-trip time in milliseconds (clock-sync-free).
   * RTT = (senderReceive − senderSend) − receiver processing time.
   */
  readonly rtt: number;

  /**
   * Forward one-way delay in milliseconds (sender → receiver).
   * Requires loose clock sync; treat as approximate unless NTP/PTP is available.
   */
  readonly forwardOwd: number;
}

/** Factory to create an immutable BandwidthProbeAck. */
export function makeBandwidthProbeAck(fields: {
  sequence: number;
  senderSendUs: bigint;
  receiverReceiveUs: bigint;
  receiverSendUs: bigint;
  senderReceiveUs: bigint;
  probeBytes: number;
}): BandwidthProbeAck {
  // RTT: (senderReceive - senderSend) - (receiverSend - receiverReceive)
  // All arithmetic in bigint microseconds, then convert to ms.
  const rttUs =
    (fields.senderReceiveUs - fields.senderSendUs) -
    (fields.receiverSendUs - fields.receiverReceiveUs);
  const rtt = Number(rttUs) / 1000.0;

  const owdUs = fields.receiverReceiveUs - fields.senderSendUs;
  const forwardOwd = Number(owdUs) / 1000.0;

  return { ...fields, rtt, forwardOwd };
}

// ── Gossip warm-start ─────────────────────────────────────────────────────────

/**
 * Gossip payload broadcast to new peers during handshake.
 * Allows the new session to start with a warm BtlBw estimate instead of
 * probing from zero — unique to AetherNet's mesh topology awareness.
 */
export interface BandwidthGossipPayload {
  readonly peerUhid: string;
  readonly transportName: string;
  readonly btlBwBps: bigint;
  /** RTprop encoded as microseconds (bigint). */
  readonly rtPropUs: bigint;
  readonly confidence: BandwidthConfidence;
  readonly measuredAt: Date;
}

// ── Node activity ─────────────────────────────────────────────────────────────

/**
 * High-level activity state of a node — suitable for status-bar indicators,
 * dashboard health badges, and connection-quality icons.
 */
export enum NodeActivityState {
  /** No transports available. Node is isolated. */
  Offline  = "Offline",

  /** Transports available but no data in the last 5 s. */
  Idle     = "Idle",

  /** Data flowing; link utilization < 50 % of estimated capacity. */
  Active   = "Active",

  /** Link utilization ≥ 50 %; performance good but approaching limits. */
  Busy     = "Busy",

  /** Loss rate > 5 % or delivery rate declining — likely interference. */
  Degraded = "Degraded",
}

/**
 * Activity snapshot for a single transport within the node.
 */
export interface TransportActivitySnapshot {
  readonly transportName: string;
  readonly isAvailable: boolean;

  /** Bytes per second being received on this transport. */
  readonly ingressBps: bigint;

  /** Bytes per second being sent on this transport. */
  readonly egressBps: bigint;

  /** Smoothed RTT in milliseconds from the bandwidth estimator. */
  readonly srttMs: number;

  /** Bottleneck bandwidth from the bandwidth estimator (bps). */
  readonly btlBwBps: bigint;

  /** Egress utilization fraction: egressBps / btlBwBps. 0 if btlBwBps = 0. */
  readonly utilizationFraction: number;

  readonly state: NodeActivityState;
  readonly confidence: BandwidthConfidence;

  /** Human-readable utilization percentage string (e.g. "34 %"). */
  readonly utilizationPercent: string;
}

/** Factory to create an immutable TransportActivitySnapshot. */
export function makeTransportActivitySnapshot(fields: {
  transportName: string;
  isAvailable: boolean;
  ingressBps: bigint;
  egressBps: bigint;
  srttMs: number;
  btlBwBps: bigint;
  utilizationFraction: number;
  state: NodeActivityState;
  confidence: BandwidthConfidence;
}): TransportActivitySnapshot {
  const utilizationPercent = `${(fields.utilizationFraction * 100.0).toFixed(0)} %`;
  return { ...fields, utilizationPercent };
}

/**
 * Full node activity snapshot — the top-level model surfaced to UI.
 */
export interface NodeActivitySnapshot {
  readonly state: NodeActivityState;

  /** Aggregate bytes per second flowing INTO this node (all transports). */
  readonly ingressBps: bigint;

  /** Aggregate bytes per second flowing OUT of this node (all transports). */
  readonly egressBps: bigint;

  /** Number of remote peers that had traffic in the last 5 s. */
  readonly activePeers: number;

  /** Number of transports currently carrying data. */
  readonly activeTransports: number;

  /** Per-transport breakdown. */
  readonly transports: ReadonlyArray<TransportActivitySnapshot>;

  /**
   * Dominant transport: the one carrying the most egress bytes.
   * Null if node is offline or idle.
   */
  readonly primaryTransportName: string | null;

  readonly timestamp: Date;

  /** Combined throughput (ingress + egress). */
  readonly totalBps: bigint;

  /** True if any transport has data flowing. */
  readonly hasActivity: boolean;
}

/** Factory to create an immutable NodeActivitySnapshot. */
export function makeNodeActivitySnapshot(fields: {
  state: NodeActivityState;
  ingressBps: bigint;
  egressBps: bigint;
  activePeers: number;
  activeTransports: number;
  transports: ReadonlyArray<TransportActivitySnapshot>;
  primaryTransportName: string | null;
  timestamp: Date;
}): NodeActivitySnapshot {
  const totalBps = fields.ingressBps + fields.egressBps;
  const hasActivity =
    fields.state === NodeActivityState.Active ||
    fields.state === NodeActivityState.Busy ||
    fields.state === NodeActivityState.Degraded;
  return { ...fields, totalBps, hasActivity };
}
