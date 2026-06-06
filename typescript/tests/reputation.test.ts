/**
 * Unit tests for NodeReputationService.
 *
 * Mirrors the C# NodeReputationServiceTests scenarios in
 * tests/AetherMesh.Core.Tests/NodeReputationServiceTests.cs.
 *
 * Run with: tsx --test typescript/tests/reputation.test.ts
 *
 * SPDX-License-Identifier: MIT
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { NodeReputationService } from "../src/reputation.js";

const ALICE = "alice-uhid";
const BOB   = "bob-uhid";

function newSvc(): NodeReputationService {
  return new NodeReputationService();
}

// ── Default score ─────────────────────────────────────────────────────────────

describe("NodeReputationService — unknown peer", () => {
  it("returns 1.0 for a peer that has never been seen", () => {
    const svc = newSvc();
    assert.equal(svc.getReputationScore("nobody"), 1.0);
  });
});

// ── Negative signals ──────────────────────────────────────────────────────────

describe("NodeReputationService — negative signals", () => {
  it("RREQ flood reduces score by 0.05", () => {
    const svc = newSvc();
    svc.recordRreqFloodAttempt(ALICE);
    assert.equal(svc.getReputationScore(ALICE), 0.95);
  });

  it("replay attempt reduces score by 0.15", () => {
    const svc = newSvc();
    svc.recordReplayAttempt(ALICE);
    assert.equal(svc.getReputationScore(ALICE), 0.85);
  });

  it("signature failure reduces score by 0.20", () => {
    const svc = newSvc();
    svc.recordSignatureFailure(ALICE);
    assert.equal(svc.getReputationScore(ALICE), 0.80);
  });

  it("custody refusal reduces score by 0.05", () => {
    const svc = newSvc();
    svc.recordCustodyRefusal(ALICE);
    assert.equal(svc.getReputationScore(ALICE), 0.95);
  });

  it("delivery failure reduces score by 0.02", () => {
    const svc = newSvc();
    svc.recordDeliveryFailure(ALICE);
    assert.equal(svc.getReputationScore(ALICE), 0.98);
  });
});

// ── Positive signals ──────────────────────────────────────────────────────────

describe("NodeReputationService — positive signals", () => {
  it("delivery success raises score by 0.01 from a degraded state", () => {
    const svc = newSvc();
    // Drop to 0.80 first, then recover one tick.
    svc.recordSignatureFailure(ALICE);              // 0.80
    svc.recordDeliverySuccess(ALICE, 50);           // 0.81
    const score = svc.getReputationScore(ALICE);
    assert.ok(Math.abs(score - 0.81) < 1e-9, `expected ~0.81, got ${score}`);
  });
});

// ── Clamping ──────────────────────────────────────────────────────────────────

describe("NodeReputationService — clamping", () => {
  it("repeated signature failures clamp to exactly 0.0", () => {
    const svc = newSvc();
    // 5 × −0.20 = −1.0 → floors at 0.0
    for (let i = 0; i < 5; i++) svc.recordSignatureFailure(ALICE);
    assert.equal(svc.getReputationScore(ALICE), 0.0);
  });

  it("repeated delivery successes clamp to exactly 1.0", () => {
    const svc = newSvc();
    // 10 × +0.01 from 1.0 → still capped at 1.0
    for (let i = 0; i < 10; i++) svc.recordDeliverySuccess(ALICE, 30);
    assert.equal(svc.getReputationScore(ALICE), 1.0);
  });

  it("score never goes below 0.0", () => {
    const svc = newSvc();
    for (let i = 0; i < 100; i++) svc.recordSignatureFailure(ALICE);
    assert.equal(svc.getReputationScore(ALICE), 0.0);
  });

  it("score never goes above 1.0", () => {
    const svc = newSvc();
    for (let i = 0; i < 100; i++) svc.recordDeliverySuccess(ALICE, 10);
    assert.equal(svc.getReputationScore(ALICE), 1.0);
  });
});

// ── Multiple peers ────────────────────────────────────────────────────────────

describe("NodeReputationService — per-peer isolation", () => {
  it("signals do not cross-contaminate peers", () => {
    const svc = newSvc();
    svc.recordSignatureFailure(ALICE);
    svc.recordSignatureFailure(ALICE);

    const alice = svc.getReputationScore(ALICE);
    const bob   = svc.getReputationScore(BOB);

    assert.ok(alice < 1.0, `alice should be < 1.0, got ${alice}`);
    assert.equal(bob, 1.0); // Bob untouched
  });
});

// ── GetAllScores ──────────────────────────────────────────────────────────────

describe("NodeReputationService — getAllScores", () => {
  it("returns a snapshot containing all modified peers", () => {
    const svc = newSvc();
    svc.recordRreqFloodAttempt(ALICE);
    svc.recordReplayAttempt(BOB);

    const all = svc.getAllScores();
    assert.equal(all.size, 2);
    assert.ok(all.has(ALICE));
    assert.ok(all.has(BOB));
    assert.ok(all.get(ALICE)! < 1.0);
    assert.ok(all.get(BOB)! < 1.0);
  });

  it("returned map is a copy — mutations do not affect internal state", () => {
    const svc = newSvc();
    svc.recordRreqFloodAttempt(ALICE);

    const snap = svc.getAllScores();
    snap.set(ALICE, 0.0); // mutate the copy

    // Internal state must be unchanged
    assert.ok(svc.getReputationScore(ALICE) > 0.0);
  });

  it("returns empty map when no signals have been recorded", () => {
    const svc = newSvc();
    assert.equal(svc.getAllScores().size, 0);
  });
});

// ── Compound signals ──────────────────────────────────────────────────────────

describe("NodeReputationService — compound signals", () => {
  it("accumulates deltas correctly: flood + replay + sig-fail = 0.60", () => {
    const svc = newSvc();
    svc.recordRreqFloodAttempt(ALICE);   // −0.05 → 0.95
    svc.recordReplayAttempt(ALICE);      // −0.15 → 0.80
    svc.recordSignatureFailure(ALICE);   // −0.20 → 0.60

    const score = svc.getReputationScore(ALICE);
    assert.ok(Math.abs(score - 0.60) < 1e-9, `expected 0.60, got ${score}`);
  });

  it("all five negative signals from 1.0 produce correct score", () => {
    const svc = newSvc();
    svc.recordRreqFloodAttempt(ALICE);   // −0.05 → 0.95
    svc.recordReplayAttempt(ALICE);      // −0.15 → 0.80
    svc.recordSignatureFailure(ALICE);   // −0.20 → 0.60
    svc.recordCustodyRefusal(ALICE);     // −0.05 → 0.55
    svc.recordDeliveryFailure(ALICE);    // −0.02 → 0.53

    const score = svc.getReputationScore(ALICE);
    assert.ok(Math.abs(score - 0.53) < 1e-9, `expected 0.53, got ${score}`);
  });
});
