// SPDX-License-Identifier: MIT
/**
 * Cross-language fixture driver for the AetherNet Bandwidth Measurement
 * Framework (ABMF). Drives the TypeScript SDK through the SHARED corpus at
 * `tests/cross-language/bandwidth-fixtures.json` — the same corpus the C#
 * reference driver (AetherNet.Core.Tests/Bandwidth/BandwidthFixtureTests.cs)
 * consumes. Every AetherNet SDK MUST produce identical results; this is the
 * oracle that proves numeric parity across all 8 languages.
 *
 * Integer / string / enum fields are asserted EXACTLY. Floating-point fields
 * (srttMs, rttVarMs, rtPropMs, lossRate) are asserted within `toleranceAbs`.
 * The JSON is the contract: do NOT edit it or loosen tolerances — a real
 * numeric divergence must surface as a test failure.
 *
 * Run with: tsx --test typescript/tests/bandwidth-fixtures.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import {
  BandwidthConfidence,
  BandwidthEstimator,
  BandwidthDirector,
  makeBandwidthSample,
  makeBandwidthProbeAck,
  type BandwidthSample,
} from "../src/bandwidth/index.js";

// ── Corpus loading (walk up to find the shared JSON) ───────────────────────────

interface Corpus {
  toleranceAbs: number;
  probeAck: ProbeAckCase[];
  rto: RtoCase[];
  phyCap: PhyCapCase[];
  estimator: EstimatorCase[];
  director: DirectorCase[];
}

function loadCorpus(): Corpus {
  let dir = dirname(fileURLToPath(import.meta.url));
  // Walk up the directory tree until tests/cross-language/bandwidth-fixtures.json exists.
  for (;;) {
    const candidate = join(dir, "tests", "cross-language", "bandwidth-fixtures.json");
    if (existsSync(candidate)) {
      return JSON.parse(readFileSync(candidate, "utf8")) as Corpus;
    }
    const parent = dirname(dir);
    if (parent === dir) {
      throw new Error(
        "bandwidth-fixtures.json not found walking up from " +
          dirname(fileURLToPath(import.meta.url)),
      );
    }
    dir = parent;
  }
}

const CORPUS = loadCorpus();
const TOL = CORPUS.toleranceAbs;

function parseConfidence(s: string): BandwidthConfidence {
  switch (s) {
    case "None":   return BandwidthConfidence.None;
    case "Low":    return BandwidthConfidence.Low;
    case "Medium": return BandwidthConfidence.Medium;
    case "High":   return BandwidthConfidence.High;
    default: throw new Error(`bad confidence ${s}`);
  }
}

// ── probeAck ───────────────────────────────────────────────────────────────────
// Build a probe ack from the four timestamps; assert RTT and forward-OWD in
// MICROSECONDS exactly. The TS factory exposes rtt/forwardOwd in milliseconds,
// so ×1000 recovers the integer µs the C# driver compares against.

interface ProbeAckCase {
  name: string;
  senderSendUs: number;
  receiverReceiveUs: number;
  receiverSendUs: number;
  senderReceiveUs: number;
  probeBytes: number;
  expectRttUs: number;
  expectForwardOwdUs: number;
}

describe("ABMF fixtures — probeAck (rtt/owd µs exact)", () => {
  for (const f of CORPUS.probeAck) {
    it(f.name, () => {
      const ack = makeBandwidthProbeAck({
        sequence:          1,
        senderSendUs:      BigInt(f.senderSendUs),
        receiverReceiveUs: BigInt(f.receiverReceiveUs),
        receiverSendUs:    BigInt(f.receiverSendUs),
        senderReceiveUs:   BigInt(f.senderReceiveUs),
        probeBytes:        f.probeBytes,
      });

      const rttUs = Math.round(ack.rtt * 1000.0);
      const owdUs = Math.round(ack.forwardOwd * 1000.0);

      assert.equal(rttUs, f.expectRttUs, `rtt µs (case ${f.name})`);
      assert.equal(owdUs, f.expectForwardOwdUs, `forwardOwd µs (case ${f.name})`);
    });
  }
});

// ── rto ────────────────────────────────────────────────────────────────────────
// Build a BandwidthSample carrying srttMs/rttVarMs; assert the derived RTO in
// milliseconds within ±0.1 (RFC 6298 §2.4, clamped to [200, 60000]).

interface RtoCase {
  name: string;
  srttMs: number;
  rttVarMs: number;
  expectRtoMs: number;
}

function sampleWithRtt(srttMs: number, rttVarMs: number): BandwidthSample {
  return makeBandwidthSample({
    transportName: "T",
    btlBwBps:      1000n,
    availableBps:  1000n,
    bdpBytes:      0n,
    srttMs,
    rttVarMs,
    rtPropMs:      10.0,
    lossRate:      0.0,
    phyCapBps:     0n,
    confidence:    BandwidthConfidence.High,
    measuredAt:    new Date(),
  });
}

describe("ABMF fixtures — rto (RFC 6298, ±0.1 ms)", () => {
  const RTO_TOL = 0.1;
  for (const f of CORPUS.rto) {
    it(f.name, () => {
      const sample = sampleWithRtt(f.srttMs, f.rttVarMs);
      assert.ok(
        Math.abs(sample.rto - f.expectRtoMs) <= RTO_TOL,
        `rto ms (case ${f.name}): expected ${f.expectRtoMs}, got ${sample.rto}`,
      );
    });
  }
});

// ── phyCap ─────────────────────────────────────────────────────────────────────
// New estimator (max 10 Gbps), applyPhyHint(rssiDbm); assert phyCapBps exact.

interface PhyCapCase {
  name: string;
  rssiDbm: number;
  expectCapBps: number;
}

describe("ABMF fixtures — phyCap (RSSI→cap exact)", () => {
  for (const f of CORPUS.phyCap) {
    it(f.name, () => {
      const est = new BandwidthEstimator("T", 10_000_000_000n);
      est.applyPhyHint(f.rssiDbm);
      assert.equal(
        est.currentSample.phyCapBps,
        BigInt(f.expectCapBps),
        `phyCapBps (case ${f.name})`,
      );
    });
  }
});

// ── estimator ──────────────────────────────────────────────────────────────────
// New estimator(transport, maxBps); apply ops; assert integer fields exact,
// confidence exact, float fields within toleranceAbs.

interface EstimatorOp {
  op: "delivery" | "loss" | "phyHint" | "gossip";
  // delivery
  bytes?: number;
  sendUs?: number;
  deliverUs?: number;
  // phyHint
  rssiDbm?: number;
  // gossip
  btlBwBps?: number;
  rtPropMs?: number;
  confidence?: string;
}

interface EstimatorExpect {
  btlBwBps?: number;
  effectiveBps?: number;
  availableBps?: number;
  bdpBytes?: number;
  phyCapBps?: number;
  confidence?: string;
  srttMs?: number;
  rttVarMs?: number;
  rtPropMs?: number;
  lossRate?: number;
}

interface EstimatorCase {
  name: string;
  transport: string;
  maxBps: number;
  ops: EstimatorOp[];
  expect: EstimatorExpect;
}

describe("ABMF fixtures — estimator (drives to expected sample)", () => {
  for (const f of CORPUS.estimator) {
    it(f.name, () => {
      const est = new BandwidthEstimator(f.transport, BigInt(f.maxBps));

      for (const op of f.ops) {
        switch (op.op) {
          case "delivery":
            est.recordDelivery(
              op.bytes!,
              BigInt(op.sendUs!),
              BigInt(op.deliverUs!),
            );
            break;
          case "loss":
            est.recordLoss(op.bytes!);
            break;
          case "phyHint":
            est.applyPhyHint(op.rssiDbm!);
            break;
          case "gossip":
            // rtPropMs is a float in milliseconds; btlBwBps → bigint.
            est.warmFromGossip(
              BigInt(op.btlBwBps!),
              op.rtPropMs!,
              parseConfidence(op.confidence!),
            );
            break;
          default:
            throw new Error(`unknown op ${(op as { op: string }).op}`);
        }
      }

      const s = est.currentSample;
      const exp = f.expect;

      // Integer / enum fields — exact (JSON number → bigint for the bps fields).
      if (exp.btlBwBps !== undefined)
        assert.equal(s.btlBwBps, BigInt(exp.btlBwBps), `btlBwBps (case ${f.name})`);
      if (exp.effectiveBps !== undefined)
        assert.equal(s.effectiveBps, BigInt(exp.effectiveBps), `effectiveBps (case ${f.name})`);
      if (exp.availableBps !== undefined)
        assert.equal(s.availableBps, BigInt(exp.availableBps), `availableBps (case ${f.name})`);
      if (exp.bdpBytes !== undefined)
        assert.equal(s.bdpBytes, BigInt(exp.bdpBytes), `bdpBytes (case ${f.name})`);
      if (exp.phyCapBps !== undefined)
        assert.equal(s.phyCapBps, BigInt(exp.phyCapBps), `phyCapBps (case ${f.name})`);
      if (exp.confidence !== undefined)
        assert.equal(s.confidence, parseConfidence(exp.confidence), `confidence (case ${f.name})`);

      // Float fields — tolerance.
      if (exp.srttMs !== undefined)
        assert.ok(
          Math.abs(s.srttMs - exp.srttMs) <= TOL,
          `srttMs (case ${f.name}): expected ${exp.srttMs}, got ${s.srttMs}`,
        );
      if (exp.rttVarMs !== undefined)
        assert.ok(
          Math.abs(s.rttVarMs - exp.rttVarMs) <= TOL,
          `rttVarMs (case ${f.name}): expected ${exp.rttVarMs}, got ${s.rttVarMs}`,
        );
      if (exp.rtPropMs !== undefined)
        assert.ok(
          Math.abs(s.rtPropMs - exp.rtPropMs) <= TOL,
          `rtPropMs (case ${f.name}): expected ${exp.rtPropMs}, got ${s.rtPropMs}`,
        );
      if (exp.lossRate !== undefined)
        assert.ok(
          Math.abs(s.lossRate - exp.lossRate) <= TOL,
          `lossRate (case ${f.name}): expected ${exp.lossRate}, got ${s.lossRate}`,
        );
    });
  }
});

// ── director ───────────────────────────────────────────────────────────────────
// Register one estimator per declared transport; apply gossips (rtPropUs is
// integer µs); recommend; assert the chosen transport (null when JSON null).

interface DirectorGossip {
  peerUhid: string;
  transport: string;
  btlBwBps: number;
  rtPropUs: number;
  confidence: string;
}

interface DirectorCase {
  name: string;
  register: string[];
  gossips: DirectorGossip[];
  recommend: { peerUhid: string; payloadBytes: number };
  expectTransport: string | null;
}

describe("ABMF fixtures — director (recommends expected transport)", () => {
  for (const f of CORPUS.director) {
    it(f.name, () => {
      const director = new BandwidthDirector();

      // Register one estimator per declared transport. Generous maxBps so the
      // PHY default does not cap the gossip-seeded values.
      for (const t of f.register) {
        director.register(new BandwidthEstimator(t, 10_000_000_000n));
      }

      for (const g of f.gossips) {
        director.applyGossip({
          peerUhid:      g.peerUhid,
          transportName: g.transport,
          btlBwBps:      BigInt(g.btlBwBps),
          rtPropUs:      BigInt(g.rtPropUs), // integer µs
          confidence:    parseConfidence(g.confidence),
          measuredAt:    new Date(),
        });
      }

      const result = director.recommendTransport(
        f.recommend.peerUhid,
        BigInt(f.recommend.payloadBytes),
      );

      if (f.expectTransport === null) {
        assert.equal(result, null, `recommend (case ${f.name})`);
      } else {
        assert.equal(result, f.expectTransport, `recommend (case ${f.name})`);
      }
    });
  }
});
