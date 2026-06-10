// SPDX-License-Identifier: MIT
/**
 * Tests for the AetherNet Bandwidth Measurement Framework (ABMF, W18-5).
 *
 * Covers the same scenarios as the Go/Python agents.
 *
 * Run with: tsx --test typescript/tests/bandwidth.test.ts
 */

import { describe, it, before, after } from "node:test";
import { strict as assert } from "node:assert";

import {
  BandwidthConfidence,
  BandwidthEstimator,
  BandwidthDirector,
  NodeActivityMonitor,
  NodeActivityState,
  makeBandwidthProbeAck,
} from "../src/bandwidth/index.js";

// ── Helpers ───────────────────────────────────────────────────────────────────

/** Current time in microseconds as bigint. */
function nowUs(): bigint {
  return BigInt(Math.round(Date.now() * 1000));
}

/** Build a probe ack with symmetrical timing (sender and receiver share the same clock). */
function makeProbeAck(rttMs: number, probeBytes = 1024) {
  const sendUs = nowUs();
  const rxUs   = sendUs + BigInt(Math.round(rttMs * 1000 / 2)); // half rtt to receiver
  const txUs   = rxUs;                                           // zero processing time
  const recvUs = sendUs + BigInt(Math.round(rttMs * 1000));
  return makeBandwidthProbeAck({
    sequence:          1,
    senderSendUs:      sendUs,
    receiverReceiveUs: rxUs,
    receiverSendUs:    txUs,
    senderReceiveUs:   recvUs,
    probeBytes,
  });
}

// ── BandwidthProbeAck ─────────────────────────────────────────────────────────

describe("BandwidthProbeAck", () => {
  it("rtt is clock-sync-free (sender-side timestamps only)", () => {
    // RTT = (senderReceive - senderSend) - (receiverSend - receiverReceive)
    // With zero receiver processing time this equals the full round-trip.
    const sendUs  = 1_000_000n; // arbitrary epoch offset
    const recvUs  = 1_020_000n; // 20 ms round-trip
    const rxUs    = 1_010_000n; // receiver got it at 10 ms (arbitrary receiver clock)
    const txUs    = 1_010_000n; // receiver sent back immediately (0 ms processing)

    const ack = makeBandwidthProbeAck({
      sequence:          42,
      senderSendUs:      sendUs,
      receiverReceiveUs: rxUs,
      receiverSendUs:    txUs,
      senderReceiveUs:   recvUs,
      probeBytes:        512,
    });

    // RTT = (1_020_000 - 1_000_000) - (1_010_000 - 1_010_000) = 20_000 µs = 20 ms
    assert.equal(ack.rtt, 20.0, "rtt should be 20 ms");
  });

  it("rtt is clock-sync-free when receiver clock is offset by hours", () => {
    // Receiver clock is 10 hours ahead — should not affect RTT.
    const TEN_HOURS_US = 36_000_000_000n;
    const sendUs  = 1_000_000n;
    const recvUs  = 1_040_000n; // 40 ms round-trip
    const rxUs    = sendUs + TEN_HOURS_US + 20_000n; // receiver clock offset
    const txUs    = rxUs + 5_000n;                   // 5 ms processing time

    const ack = makeBandwidthProbeAck({
      sequence:          1,
      senderSendUs:      sendUs,
      receiverReceiveUs: rxUs,
      receiverSendUs:    txUs,
      senderReceiveUs:   recvUs,
      probeBytes:        256,
    });

    // RTT = (1_040_000 - 1_000_000) - (txUs - rxUs) = 40_000 - 5_000 = 35_000 µs = 35 ms
    assert.equal(ack.rtt, 35.0, "rtt should be 35 ms (clock offset must not affect result)");
  });

  it("forwardOwd uses receiver timestamps (approximate)", () => {
    const ack = makeBandwidthProbeAck({
      sequence:          1,
      senderSendUs:      1_000_000n,
      receiverReceiveUs: 1_012_000n, // 12 ms one-way
      receiverSendUs:    1_012_000n,
      senderReceiveUs:   1_024_000n,
      probeBytes:        128,
    });
    assert.equal(ack.forwardOwd, 12.0, "forwardOwd should be 12 ms");
  });
});

// ── BandwidthEstimator — RTO ──────────────────────────────────────────────────

describe("BandwidthEstimator — RTO floor", () => {
  it("rto is clamped to 200 ms minimum even for very fast links", () => {
    const est = new BandwidthEstimator("BLE", 2_000_000n);

    // Feed a 1 ms RTT probe — RFC 6298 would compute RTO ≈ 3 ms;
    // the 200 ms floor must apply.
    const ack = makeProbeAck(1.0, 64);
    est.recordProbeResult(ack, nowUs());

    assert.ok(
      est.currentSample.rto >= 200,
      `rto must be ≥ 200 ms, got ${est.currentSample.rto}`,
    );
  });

  it("rto is clamped to 60 000 ms maximum for extremely high RTT", () => {
    const est = new BandwidthEstimator("HTTP Relay", 1_000_000n);

    // Feed a 50-second RTT — RTO floor at 60 s.
    const ack = makeProbeAck(50_000.0, 64);
    est.recordProbeResult(ack, nowUs());

    assert.ok(
      est.currentSample.rto <= 60_000,
      `rto must be ≤ 60 000 ms, got ${est.currentSample.rto}`,
    );
  });
});

// ── BandwidthEstimator — delivery recording ───────────────────────────────────

describe("BandwidthEstimator — recordDelivery", () => {
  it("updates BtlBw after a single delivery", () => {
    const est = new BandwidthEstimator("Wi-Fi Direct", 600_000_000n);
    const sendUs    = nowUs();
    const deliverUs = sendUs + 100_000n; // 100 ms elapsed

    est.recordDelivery(12_500, sendUs, deliverUs); // 12 500 bytes in 100 ms → 1 Mbps

    assert.ok(est.currentSample.btlBwBps > 0n, "btlBwBps should be > 0 after delivery");
  });

  it("ignores delivery where deliverUs <= sendUs", () => {
    const est = new BandwidthEstimator("BLE", 2_000_000n);
    const before = est.currentSample.btlBwBps;

    const us = nowUs();
    est.recordDelivery(1000, us, us); // zero elapsed — should be ignored

    // Initial snapshot keeps max bandwidth — compare to verify no bad update.
    assert.equal(est.currentSample.btlBwBps, before);
  });

  it("confidence advances with probe rounds", () => {
    const est = new BandwidthEstimator("NearLink", 100_000_000n);

    assert.equal(est.currentSample.confidence, BandwidthConfidence.None);

    // Feed 5+ deliveries to reach Low confidence.
    for (let i = 0; i < 5; i++) {
      const s = nowUs();
      est.recordDelivery(1024, s, s + 10_000n);
    }
    assert.ok(
      est.currentSample.confidence >= BandwidthConfidence.Low,
      "confidence should be at least Low after 5 rounds",
    );

    // Feed 20+ total to reach Medium.
    for (let i = 0; i < 15; i++) {
      const s = nowUs();
      est.recordDelivery(1024, s, s + 10_000n);
    }
    assert.ok(
      est.currentSample.confidence >= BandwidthConfidence.Medium,
      "confidence should be at least Medium after 20 rounds",
    );
  });
});

// ── BandwidthEstimator — loss ─────────────────────────────────────────────────

describe("BandwidthEstimator — recordLoss", () => {
  it("increases the loss rate after loss events", () => {
    const est = new BandwidthEstimator("BLE", 500_000n);
    const before = est.currentSample.lossRate;
    est.recordLoss(256);
    assert.ok(
      est.currentSample.lossRate > before,
      "lossRate must increase after recordLoss",
    );
  });

  it("ignores zero and negative byte counts", () => {
    const est = new BandwidthEstimator("BLE", 500_000n);
    const before = est.currentSample.lossRate;
    est.recordLoss(0);
    est.recordLoss(-1);
    assert.equal(est.currentSample.lossRate, before);
  });
});

// ── BandwidthEstimator — PHY hint ─────────────────────────────────────────────

describe("BandwidthEstimator — applyPhyHint", () => {
  it("caps BtlBw at the PHY-derived limit", () => {
    const est = new BandwidthEstimator("BLE", 2_000_000n);

    // Seed a BtlBw sample that exceeds the -95 dBm PHY cap (40 kbps = 40_000 bps).
    const sendUs = nowUs();
    est.recordDelivery(5_000, sendUs, sendUs + 1_000n); // high rate

    // Now apply a very weak signal hint.
    est.applyPhyHint(-100); // < -95 dBm → 40 000 bps cap

    assert.ok(
      est.currentSample.btlBwBps <= 40_000n,
      `btlBwBps ${est.currentSample.btlBwBps} must be ≤ 40 000 bps after weak PHY hint`,
    );
  });

  it("effectiveBps respects phyCapBps", () => {
    const est = new BandwidthEstimator("BLE", 2_000_000n);
    est.applyPhyHint(-85); // 500 kbps cap
    const s = est.currentSample;
    assert.ok(s.phyCapBps === 500_000n);
    assert.ok(s.effectiveBps <= 500_000n);
  });
});

// ── BandwidthEstimator — gossip warm-start ────────────────────────────────────

describe("BandwidthEstimator — warmFromGossip", () => {
  it("pre-seeds estimator when confidence is None", () => {
    const est = new BandwidthEstimator("NearLink", 100_000_000n);
    assert.equal(est.currentSample.confidence, BandwidthConfidence.None);

    est.warmFromGossip(5_000_000n, 20.0, BandwidthConfidence.Medium);

    assert.ok(est.currentSample.btlBwBps > 0n, "btlBwBps should be seeded");
    assert.equal(est.currentSample.confidence, BandwidthConfidence.Low, "warmed → Low");
  });

  it("never downgrades an existing estimate", () => {
    const est = new BandwidthEstimator("NearLink", 100_000_000n);

    // Build up a real estimate first.
    for (let i = 0; i < 25; i++) {
      const s = nowUs();
      est.recordDelivery(10_000, s, s + 10_000n);
    }
    const before = est.currentSample.btlBwBps;
    const confBefore = est.currentSample.confidence;

    // Attempt gossip warm with a much lower value — should be ignored.
    est.warmFromGossip(100n, 1000.0, BandwidthConfidence.Low);

    assert.equal(est.currentSample.btlBwBps, before, "btlBwBps must not be downgraded");
    assert.equal(est.currentSample.confidence, confBefore, "confidence must not be downgraded");
  });

  it("second gossip call is ignored (once warmed, once warmed)", () => {
    const est = new BandwidthEstimator("BLE", 2_000_000n);
    est.warmFromGossip(1_000_000n, 30.0, BandwidthConfidence.Medium);
    const after1 = est.currentSample.btlBwBps;

    est.warmFromGossip(999_000_000n, 1.0, BandwidthConfidence.High);
    assert.equal(est.currentSample.btlBwBps, after1, "second gossip must not override first");
  });
});

// ── BandwidthEstimator — onSampleImproved ────────────────────────────────────

describe("BandwidthEstimator — onSampleImproved", () => {
  it("fires when confidence advances", async () => {
    const est = new BandwidthEstimator("BLE", 2_000_000n);
    let fired = false;
    est.onSampleImproved.push(() => { fired = true; });

    // Warm from gossip advances confidence from None → Low.
    est.warmFromGossip(500_000n, 20.0, BandwidthConfidence.Medium);

    // Callbacks are async (Promise.resolve().then(...)); wait a microtask tick.
    await Promise.resolve();
    assert.ok(fired, "onSampleImproved must fire when confidence advances");
  });
});

// ── BandwidthDirector ─────────────────────────────────────────────────────────

describe("BandwidthDirector", () => {
  it("getEstimate returns null before any data", () => {
    const dir = new BandwidthDirector();
    assert.equal(dir.getEstimate("peer-A", "BLE"), null);
  });

  it("recommendTransport falls back to lowest-power-cost transport when no matrix data", () => {
    const dir = new BandwidthDirector();
    const nearLink = new BandwidthEstimator("NearLink", 100_000_000n);
    const ble      = new BandwidthEstimator("BLE",      2_000_000n);
    dir.register(nearLink);
    dir.register(ble);

    // No peer measurements — should pick NearLink (power cost 1 < BLE's 2).
    const rec = dir.recommendTransport("peer-X", 1024n);
    assert.equal(rec, "NearLink");
  });

  it("applyGossip seeds the matrix so getEstimate returns a sample", () => {
    const dir = new BandwidthDirector();
    const ble  = new BandwidthEstimator("BLE", 2_000_000n);
    dir.register(ble);

    dir.applyGossip({
      peerUhid:      "peer-B",
      transportName: "BLE",
      btlBwBps:      500_000n,
      rtPropUs:      20_000n,  // 20 ms
      confidence:    BandwidthConfidence.Low,
      measuredAt:    new Date(),
    });

    const sample = dir.getEstimate("peer-B", "BLE");
    assert.ok(sample !== null, "getEstimate must return a sample after gossip");
    assert.ok(sample.btlBwBps > 0n, "seeded sample must have btlBwBps > 0");
  });

  it("buildGossipPayload returns null when confidence is None", () => {
    const dir = new BandwidthDirector();
    const est = new BandwidthEstimator("BLE", 2_000_000n);
    dir.register(est);

    // No observations yet → confidence is None.
    assert.equal(dir.buildGossipPayload("peer-C", "BLE"), null);
  });

  it("buildGossipPayload returns payload once confidence is non-None", () => {
    const dir = new BandwidthDirector();
    const est = new BandwidthEstimator("BLE", 2_000_000n);
    dir.register(est);

    // Warm from gossip to get confidence ≥ Low.
    est.warmFromGossip(500_000n, 20.0, BandwidthConfidence.Medium);

    const payload = dir.buildGossipPayload("peer-D", "BLE");
    assert.ok(payload !== null, "buildGossipPayload must return payload when confidence ≥ Low");
    assert.equal(payload.peerUhid, "peer-D");
  });

  it("getEstimates returns results sorted by availableBps descending", () => {
    const dir = new BandwidthDirector();

    // Seed the matrix directly via gossip for two transports.
    const near = new BandwidthEstimator("NearLink", 100_000_000n);
    const ble  = new BandwidthEstimator("BLE",        2_000_000n);
    dir.register(near);
    dir.register(ble);

    // Warm NearLink with a larger bandwidth.
    near.warmFromGossip(50_000_000n, 5.0, BandwidthConfidence.Medium);
    ble.warmFromGossip(500_000n, 20.0, BandwidthConfidence.Medium);

    dir.applyGossip({
      peerUhid: "peer-E", transportName: "NearLink",
      btlBwBps: 50_000_000n, rtPropUs: 5_000n,
      confidence: BandwidthConfidence.Medium, measuredAt: new Date(),
    });
    dir.applyGossip({
      peerUhid: "peer-E", transportName: "BLE",
      btlBwBps: 500_000n, rtPropUs: 20_000n,
      confidence: BandwidthConfidence.Medium, measuredAt: new Date(),
    });

    const estimates = dir.getEstimates("peer-E");
    assert.equal(estimates.length, 2);
    assert.ok(
      estimates[0].availableBps >= estimates[1].availableBps,
      "estimates must be sorted by availableBps descending",
    );
  });
});

// ── NodeActivityMonitor ───────────────────────────────────────────────────────

describe("NodeActivityMonitor — hasActivity states", () => {
  it("initial snapshot is Offline with hasActivity=false", () => {
    const mon = new NodeActivityMonitor();
    assert.equal(mon.current.state, NodeActivityState.Offline);
    assert.equal(mon.current.hasActivity, false);
  });

  it("hasActivity is false for Offline state", () => {
    const mon = new NodeActivityMonitor();
    assert.equal(
      mon.current.hasActivity,
      false,
      "Offline must have hasActivity=false",
    );
  });

  it("hasActivity is false for Idle state", async () => {
    const mon = new NodeActivityMonitor();
    const est = new BandwidthEstimator("BLE", 2_000_000n);
    mon.register("BLE", est);
    mon.sampleIntervalMs = 50;
    mon.idleThresholdSeconds = 0; // force idle immediately

    mon.start();
    await new Promise((r) => setTimeout(r, 120));
    mon.stop();

    // With no traffic recorded, state should be Idle.
    assert.equal(mon.current.state, NodeActivityState.Idle);
    assert.equal(mon.current.hasActivity, false);
  });

  it("hasActivity is true when state is Active", async () => {
    const mon = new NodeActivityMonitor();
    const est = new BandwidthEstimator("BLE", 2_000_000n);
    mon.register("BLE", est);
    mon.sampleIntervalMs = 50;
    mon.idleThresholdSeconds = 60; // keep alive

    // Record bytes into subscriber callback so each tick gets fresh traffic.
    let tickCount = 0;
    mon.subscribe(() => { tickCount++; });

    mon.start();

    // Keep feeding bytes before each tick so the counter is non-zero.
    // Run a small loop alongside the timer.
    const feedInterval = setInterval(() => {
      mon.recordIngress("BLE", 10_000);
      mon.recordEgress("BLE", 10_000);
    }, 10);

    await new Promise((r) => setTimeout(r, 160));
    clearInterval(feedInterval);
    mon.stop();

    // Active or Busy (depending on how fast the timer fires).
    const s = mon.current.state;
    const validActiveStates = [
      NodeActivityState.Active,
      NodeActivityState.Busy,
      NodeActivityState.Degraded,
    ];
    assert.ok(
      validActiveStates.includes(s) || mon.current.hasActivity,
      `Expected active state, got ${s} (ticks=${tickCount})`,
    );
  });

  it("subscribe teardown stops notifications", async () => {
    const mon = new NodeActivityMonitor();
    const est = new BandwidthEstimator("BLE", 2_000_000n);
    mon.register("BLE", est);
    mon.sampleIntervalMs = 50;
    mon.idleThresholdSeconds = 60;

    let count = 0;
    const unsub = mon.subscribe(() => { count++; });

    mon.start();
    mon.recordEgress("BLE", 100_000);
    await new Promise((r) => setTimeout(r, 70));
    unsub();

    const countAfterUnsub = count;
    mon.recordEgress("BLE", 100_000);
    await new Promise((r) => setTimeout(r, 70));
    mon.stop();

    // Count must not increase after unsubscribe.
    assert.equal(count, countAfterUnsub, "subscriber must not fire after teardown");
  });
});

// ── NodeActivityMonitor — activePeers ─────────────────────────────────────────

describe("NodeActivityMonitor — activePeers", () => {
  it("counts distinct peers after recordEgressToPeer with 2 peers", async () => {
    const mon = new NodeActivityMonitor();
    const est = new BandwidthEstimator("BLE", 2_000_000n);
    mon.register("BLE", est);
    mon.sampleIntervalMs = 50;
    mon.idleThresholdSeconds = 60; // keep peers inside the idle window

    mon.start();
    mon.recordEgressToPeer("BLE", "peer-A", 1_000);
    mon.recordEgressToPeer("BLE", "peer-B", 1_000);
    await new Promise((r) => setTimeout(r, 120));
    mon.stop();

    assert.ok(
      mon.current.activePeers >= 2,
      `expected activePeers >= 2, got ${mon.current.activePeers}`,
    );
  });

  it("activePeers stays 0 when egress is recorded without a peer", async () => {
    const mon = new NodeActivityMonitor();
    const est = new BandwidthEstimator("BLE", 2_000_000n);
    mon.register("BLE", est);
    mon.sampleIntervalMs = 50;
    mon.idleThresholdSeconds = 60;

    mon.start();
    mon.recordEgress("BLE", 10_000); // transport-only — must not count a peer
    mon.recordIngress("BLE", 10_000);
    await new Promise((r) => setTimeout(r, 120));
    mon.stop();

    assert.equal(
      mon.current.activePeers,
      0,
      "transport-only recordEgress/recordIngress must not register a peer",
    );
  });
});

// ── NodeActivitySnapshot — derived properties ─────────────────────────────────

describe("NodeActivitySnapshot — derived properties", () => {
  it("totalBps is sum of ingressBps + egressBps", async () => {
    const mon = new NodeActivityMonitor();
    const est = new BandwidthEstimator("BLE", 2_000_000n);
    mon.register("BLE", est);
    mon.sampleIntervalMs = 50;
    mon.idleThresholdSeconds = 60;

    mon.start();
    mon.recordIngress("BLE", 5_000);
    mon.recordEgress("BLE",  3_000);
    await new Promise((r) => setTimeout(r, 120));
    mon.stop();

    const s = mon.current;
    assert.equal(s.totalBps, s.ingressBps + s.egressBps);
  });
});

// ── PacketType constants ───────────────────────────────────────────────────────

describe("PacketType — bandwidth packet types", () => {
  it("BandwidthProbe = 53, BandwidthAck = 54, BandwidthGossip = 55", async () => {
    const { PacketType } = await import("../src/protocol/PacketType.js");
    assert.equal((PacketType as Record<string, unknown>)["BandwidthProbe"],  53);
    assert.equal((PacketType as Record<string, unknown>)["BandwidthAck"],    54);
    assert.equal((PacketType as Record<string, unknown>)["BandwidthGossip"], 55);
  });

  it("packetTypeToString returns correct labels", async () => {
    const { PacketType, packetTypeToString } = await import("../src/protocol/PacketType.js");
    assert.equal(packetTypeToString(PacketType.BandwidthProbe),  "BandwidthProbe");
    assert.equal(packetTypeToString(PacketType.BandwidthAck),    "BandwidthAck");
    assert.equal(packetTypeToString(PacketType.BandwidthGossip), "BandwidthGossip");
  });
});
