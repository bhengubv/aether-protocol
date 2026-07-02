// SPDX-License-Identifier: MIT

/**
 * AetherNet Bandwidth Measurement Framework (ABMF) — public API.
 *
 * Re-exports models, estimator, director, and monitor.
 */

export {
  BandwidthConfidence,
  NodeActivityState,
  makeBandwidthSample,
  makeBandwidthProbeAck,
  makeTransportActivitySnapshot,
  makeNodeActivitySnapshot,
  type BandwidthSample,
  type BandwidthProbeAck,
  type BandwidthGossipPayload,
  type TransportActivitySnapshot,
  type NodeActivitySnapshot,
} from "./models.js";

export {
  BandwidthEstimator,
  BTL_BW_WINDOW_SIZE,
  RT_PROP_WINDOW_MS,
  LOSS_ALPHA,
} from "./BandwidthEstimator.js";

export { BandwidthDirector } from "./BandwidthDirector.js";
export { NodeActivityMonitor } from "./NodeActivityMonitor.js";

// WIRE bindings — BandwidthProbe(53) / BandwidthAck(54) / BandwidthGossip(55)
export {
  BandwidthWireService,
  BandwidthWireCodec,
  type BandwidthProbe,
  type BandwidthProbeReceived,
} from "./BandwidthWireService.js";
