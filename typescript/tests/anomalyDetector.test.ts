/**
 * Unit tests for BehavioralAnomalyDetector.
 *
 * Run with: tsx --test typescript/tests/anomalyDetector.test.ts
 *
 * SPDX-License-Identifier: MIT
 */

import { describe, it, beforeEach } from "node:test";
import { strict as assert } from "node:assert";

import {
  BehavioralAnomalyDetector,
  type AnomalyDetectorOptions,
} from "../src/anomalyDetector.js";
import type { NodeReputationService } from "../src/reputation.js";

// ── Spy reputation service ────────────────────────────────────────────────────

interface SpyCalls {
  rreqFloodAttempt: string[];
  signatureFailure: string[];
  replayAttempt: string[];
  custodyRefusal: string[];
  deliverySuccess: Array<{ uhid: string; roundTripMs: number }>;
  deliveryFailure: string[];
}

function makeSpyReputation(): NodeReputationService & { calls: SpyCalls } {
  const calls: SpyCalls = {
    rreqFloodAttempt: [],
    signatureFailure: [],
    replayAttempt: [],
    custodyRefusal: [],
    deliverySuccess: [],
    deliveryFailure: [],
  };

  const spy = {
    calls,
    recordRreqFloodAttempt(uhid: string) { calls.rreqFloodAttempt.push(uhid); },
    recordReplayAttempt(uhid: string) { calls.replayAttempt.push(uhid); },
    recordSignatureFailure(uhid: string) { calls.signatureFailure.push(uhid); },
    recordCustodyRefusal(uhid: string) { calls.custodyRefusal.push(uhid); },
    recordDeliverySuccess(uhid: string, roundTripMs: number) {
      calls.deliverySuccess.push({ uhid, roundTripMs });
    },
    recordDeliveryFailure(uhid: string) { calls.deliveryFailure.push(uhid); },
    // Unused query methods — present to satisfy the type
    getReputationScore(_uhid: string): number { return 1.0; },
    getAllScores(): Map<string, number> { return new Map(); },
  } as unknown as NodeReputationService & { calls: SpyCalls };

  return spy;
}

const ALICE = "alice-uhid";
const BOB   = "bob-uhid";

// ── Helper: build detector with custom options ────────────────────────────────

function makeDetector(
  spy: ReturnType<typeof makeSpyReputation>,
  opts?: AnomalyDetectorOptions
): BehavioralAnomalyDetector {
  return new BehavioralAnomalyDetector(spy, opts);
}

// ── Volume spike ──────────────────────────────────────────────────────────────

describe("BehavioralAnomalyDetector — volume spike", () => {
  it("spike detected: window1=5 (ewma=5), window2=20, multiplier=3 → flood signal", () => {
    const spy = makeSpyReputation();
    const det = makeDetector(spy, {
      volumeWindowMs: 1000,
      volumeSpikeMultiplier: 3,
      ewmaAlpha: 1.0, // ewma = last window count exactly (α=1 ⇒ new value only)
    });

    // Window 1: 5 packets at t=0..499
    for (let i = 0; i < 5; i++) det.observePacket(ALICE, BOB, i * 100);

    // Window 2 starts at t=1000; 20 packets
    for (let i = 0; i < 20; i++) det.observePacket(ALICE, BOB, 1000 + i * 10);

    // Window 3 triggers evaluation of window 2 (need one packet past 2000)
    det.observePacket(ALICE, BOB, 2100);

    // 20 > 3 × 5 → flood signal
    assert.ok(
      spy.calls.rreqFloodAttempt.includes(ALICE),
      "expected flood signal for ALICE"
    );
  });

  it("no false spike: window1=10, window2=12, multiplier=5 → no signal", () => {
    const spy = makeSpyReputation();
    const det = makeDetector(spy, {
      volumeWindowMs: 1000,
      volumeSpikeMultiplier: 5,
      ewmaAlpha: 1.0,
    });

    for (let i = 0; i < 10; i++) det.observePacket(ALICE, BOB, i * 90);
    for (let i = 0; i < 12; i++) det.observePacket(ALICE, BOB, 1000 + i * 80);
    det.observePacket(ALICE, BOB, 2100); // close window 2

    // 12 is not > 5 × 10 = 50
    assert.equal(
      spy.calls.rreqFloodAttempt.length,
      0,
      "should not have emitted a flood signal"
    );
  });

  it("first window never triggers a spike (no prior baseline)", () => {
    const spy = makeSpyReputation();
    const det = makeDetector(spy, {
      volumeWindowMs: 1000,
      volumeSpikeMultiplier: 2,
      ewmaAlpha: 1.0,
    });

    // Huge window 1 — no baseline yet, so no signal should fire
    for (let i = 0; i < 100; i++) det.observePacket(ALICE, BOB, i * 9);
    det.observePacket(ALICE, BOB, 1100); // close window 1

    // Window 2: single packet — triggers eval of window 1
    det.observePacket(ALICE, BOB, 2200); // close window 2

    // The very first completed window should never emit (no prior ewma)
    assert.equal(spy.calls.rreqFloodAttempt.length, 0);
  });
});

// ── Destination scatter ───────────────────────────────────────────────────────

describe("BehavioralAnomalyDetector — destination scatter", () => {
  it("scatter detected: threshold=5, 6 unique destinations → flood signal", () => {
    const spy = makeSpyReputation();
    const det = makeDetector(spy, {
      scatterWindowMs: 60_000,
      scatterThreshold: 5,
    });

    const now = 1_000_000;
    for (let i = 0; i < 6; i++) {
      det.observePacket(ALICE, `dest-${i}`, now + i * 100);
    }

    assert.ok(
      spy.calls.rreqFloodAttempt.includes(ALICE),
      "expected scatter flood signal"
    );
  });

  it("scatter not triggered by repeated packets to same destinations", () => {
    const spy = makeSpyReputation();
    const det = makeDetector(spy, {
      scatterWindowMs: 60_000,
      scatterThreshold: 5,
    });

    const now = 1_000_000;
    // 100 packets but only 3 unique destinations
    for (let i = 0; i < 100; i++) {
      det.observePacket(ALICE, `dest-${i % 3}`, now + i * 100);
    }

    assert.equal(
      spy.calls.rreqFloodAttempt.length,
      0,
      "should not trigger flood for 3 unique dests"
    );
  });

  it("scatter respects sliding window expiry", () => {
    const spy = makeSpyReputation();
    const det = makeDetector(spy, {
      scatterWindowMs: 1000,
      scatterThreshold: 3,
    });

    const t0 = 0;
    // 3 unique dests in first window — at threshold, not over
    det.observePacket(ALICE, "dest-0", t0);
    det.observePacket(ALICE, "dest-1", t0 + 100);
    det.observePacket(ALICE, "dest-2", t0 + 200);

    // Let time pass so those observations expire
    const t1 = 2000;
    det.observePacket(ALICE, "dest-3", t1);       // 1 unique in window — fine
    det.observePacket(ALICE, "dest-4", t1 + 100); // 2 unique — fine

    assert.equal(
      spy.calls.rreqFloodAttempt.length,
      0,
      "expired observations should not count toward threshold"
    );
  });
});

// ── Geohash mismatch ──────────────────────────────────────────────────────────

describe("BehavioralAnomalyDetector — geohash mismatch", () => {
  it("different 4-char prefixes → signature failure signal", () => {
    const spy = makeSpyReputation();
    const det = makeDetector(spy);

    det.observeGeohashClaim(ALICE, "abcd1234", "wxyz5678");

    assert.deepEqual(spy.calls.signatureFailure, [ALICE]);
  });

  it("matching 4-char prefix → no signal", () => {
    const spy = makeSpyReputation();
    const det = makeDetector(spy);

    det.observeGeohashClaim(ALICE, "abcd1111", "abcd9999");

    assert.equal(spy.calls.signatureFailure.length, 0);
  });

  it("mismatch with rate_limit_ms=Infinity → only one signal for two consecutive calls", () => {
    const spy = makeSpyReputation();
    const det = makeDetector(spy, { geohashRateLimitMs: Infinity });

    const now = Date.now();
    det.observeGeohashClaim(ALICE, "abcd1234", "wxyz5678", now);
    // Second call immediately after — within Infinity-ms window → suppressed
    det.observeGeohashClaim(ALICE, "abcd1234", "wxyz5678", now + 1);

    assert.equal(
      spy.calls.signatureFailure.length,
      1,
      "second call should be rate-limited"
    );
    assert.equal(spy.calls.signatureFailure[0], ALICE);
  });

  it("mismatch not rate-limited after window expires", () => {
    const spy = makeSpyReputation();
    const rateLimitMs = 5000;
    const det = makeDetector(spy, { geohashRateLimitMs: rateLimitMs });

    const t0 = 1_000_000;
    det.observeGeohashClaim(ALICE, "abcd1234", "wxyz5678", t0);
    // Within rate limit — suppressed
    det.observeGeohashClaim(ALICE, "abcd1234", "wxyz5678", t0 + 1000);
    // After rate limit has elapsed — emits again
    det.observeGeohashClaim(ALICE, "abcd1234", "wxyz5678", t0 + rateLimitMs + 1);

    assert.equal(
      spy.calls.signatureFailure.length,
      2,
      "should have emitted two signals (before and after rate limit window)"
    );
  });

  it("geohash mismatch is per-node — different nodes have independent rate limits", () => {
    const spy = makeSpyReputation();
    const det = makeDetector(spy, { geohashRateLimitMs: Infinity });

    const now = 1_000_000;
    det.observeGeohashClaim(ALICE, "aaaa1111", "bbbb2222", now);
    det.observeGeohashClaim(BOB,   "cccc3333", "dddd4444", now);

    assert.equal(spy.calls.signatureFailure.length, 2);
    assert.ok(spy.calls.signatureFailure.includes(ALICE));
    assert.ok(spy.calls.signatureFailure.includes(BOB));
  });
});

// ── SPK sig failure passthrough ───────────────────────────────────────────────

describe("BehavioralAnomalyDetector — SPK sig failure", () => {
  it("single call passes through to recordSignatureFailure", () => {
    const spy = makeSpyReputation();
    const det = makeDetector(spy);

    det.observeSpkSigFailure(ALICE);

    assert.deepEqual(spy.calls.signatureFailure, [ALICE]);
  });

  it("three calls produce three signals (no dedup or rate-limiting)", () => {
    const spy = makeSpyReputation();
    const det = makeDetector(spy);

    det.observeSpkSigFailure(ALICE);
    det.observeSpkSigFailure(ALICE);
    det.observeSpkSigFailure(ALICE);

    assert.equal(spy.calls.signatureFailure.length, 3);
    assert.ok(
      spy.calls.signatureFailure.every((u) => u === ALICE),
      "all signals should be for ALICE"
    );
  });
});

// ── Cross-contamination ───────────────────────────────────────────────────────

describe("BehavioralAnomalyDetector — no cross-contamination", () => {
  it("signals for ALICE do not affect BOB's call counts", () => {
    const spy = makeSpyReputation();
    const det = makeDetector(spy, {
      volumeWindowMs: 1000,
      volumeSpikeMultiplier: 2,
      ewmaAlpha: 1.0,
      scatterWindowMs: 60_000,
      scatterThreshold: 3,
    });

    const now = 1_000_000;

    // Trigger scatter on ALICE (4 unique dests > threshold 3)
    for (let i = 0; i < 4; i++) det.observePacket(ALICE, `dest-${i}`, now + i);

    // SPK failure for ALICE
    det.observeSpkSigFailure(ALICE);

    // BOB should have no signals at all
    const aliceFlood = spy.calls.rreqFloodAttempt.filter((u) => u === ALICE).length;
    const bobFlood   = spy.calls.rreqFloodAttempt.filter((u) => u === BOB).length;
    const aliceSig   = spy.calls.signatureFailure.filter((u) => u === ALICE).length;
    const bobSig     = spy.calls.signatureFailure.filter((u) => u === BOB).length;

    assert.ok(aliceFlood >= 1, "ALICE should have flood signals");
    assert.equal(bobFlood, 0,  "BOB should have no flood signals");
    assert.equal(aliceSig, 1,  "ALICE should have one sig-failure");
    assert.equal(bobSig,   0,  "BOB should have no sig-failures");
  });
});
