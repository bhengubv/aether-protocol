/**
 * Unit tests for ReputationGossipService (Item 27).
 *
 * Run with: tsx --test typescript/tests/gossip.test.ts
 *
 * SPDX-License-Identifier: MIT
 */

import { describe, it, beforeEach } from "node:test";
import { strict as assert } from "node:assert";

import {
  ReputationGossipService,
  REPUTATION_UPDATE_TYPE,
  type Packet,
  type MeshSender,
  type PacketSigner,
  type ReputationUpdatePayload,
} from "../src/gossip.js";
import { NodeReputationService } from "../src/reputation.js";

// ── Fake MeshSender ────────────────────────────────────────────────────────────

class FakeMeshSender implements MeshSender {
  readonly localUhid: string;
  readonly sent: Packet[] = [];

  constructor(uhid: string) {
    this.localUhid = uhid;
  }

  broadcast(packet: Packet): number {
    this.sent.push(packet);
    return 1; // simulates delivery to one peer
  }
}

// ── Fake PacketSigner ──────────────────────────────────────────────────────────

class FakePacketSigner implements PacketSigner {
  /** When false the next verifyPacket call returns false. */
  verifyResult = true;

  signPacket(packet: Packet): Packet {
    // Attach a deterministic fake signature so tests can verify it was called
    return { ...packet, signature: "fake-sig" };
  }

  verifyPacket(_packet: Packet, _senderPublicKey: Uint8Array): boolean {
    return this.verifyResult;
  }
}

// ── Helpers ───────────────────────────────────────────────────────────────────

const LOCAL_UHID    = "local-node";
const REPORTER_UHID = "reporter-node";
const TARGET_UHID   = "target-node";
const DUMMY_KEY     = new Uint8Array(32);

function makeServices(localUhid = LOCAL_UHID) {
  const sender     = new FakeMeshSender(localUhid);
  const signing    = new FakePacketSigner();
  const reputation = new NodeReputationService();
  const gossip     = new ReputationGossipService(sender, signing, reputation);
  return { sender, signing, reputation, gossip };
}

/** Build a valid gossip packet as seen coming from REPORTER_UHID. */
function buildGossipPacket(
  overrides: Partial<ReputationUpdatePayload> = {},
  packetOverrides: Partial<Packet> = {}
): Packet {
  const payload: ReputationUpdatePayload = {
    reporter_uhid: REPORTER_UHID,
    target_uhid:   TARGET_UHID,
    score_delta:   -0.1,
    timestamp_ms:  Date.now(),
    reason:        "test",
    ...overrides,
  };
  const base: Packet = {
    type:             REPUTATION_UPDATE_TYPE,
    source_uhid:      REPORTER_UHID,
    destination_uhid: "*",
    ttl:              3,
    payload:          JSON.stringify(payload),
    timestamp_ms:     Date.now(),
    signature:        "fake-sig",
    ...packetOverrides,
  };
  return base;
}

// ─────────────────────────────────────────────────────────────────────────────
// 1. broadcastReputationUpdate sends one packet with correct type
// ─────────────────────────────────────────────────────────────────────────────

describe("ReputationGossipService — broadcastReputationUpdate", () => {
  it("sends one packet with correct type", () => {
    const { sender, gossip } = makeServices();

    gossip.broadcastReputationUpdate(TARGET_UHID, -0.1, "flood");

    assert.equal(sender.sent.length, 1);
    assert.equal(sender.sent[0].type, REPUTATION_UPDATE_TYPE);
  });

  // 2. broadcastReputationUpdate payload has correct fields
  it("payload has correct fields", () => {
    const { sender, gossip } = makeServices();

    gossip.broadcastReputationUpdate(TARGET_UHID, -0.2, "replay");

    const packet = sender.sent[0];
    const p = JSON.parse(packet.payload) as ReputationUpdatePayload;

    assert.equal(p.reporter_uhid, LOCAL_UHID);
    assert.equal(p.target_uhid,   TARGET_UHID);
    assert.equal(p.reason,        "replay");
    assert.ok(typeof p.timestamp_ms === "number" && p.timestamp_ms > 0);
    assert.ok(Math.abs(p.score_delta - (-0.2)) < 1e-9);
  });

  // 3. broadcastReputationUpdate clamps delta above 1
  it("clamps delta above 1", () => {
    const { sender, gossip } = makeServices();

    gossip.broadcastReputationUpdate(TARGET_UHID, 99.9, "boost");

    const p = JSON.parse(sender.sent[0].payload) as ReputationUpdatePayload;
    assert.ok(
      Math.abs(p.score_delta - 1.0) < 1e-9,
      `expected delta clamped to 1.0, got ${p.score_delta}`
    );
  });

  // 4. broadcastReputationUpdate clamps delta below -1
  it("clamps delta below -1", () => {
    const { sender, gossip } = makeServices();

    gossip.broadcastReputationUpdate(TARGET_UHID, -99.9, "penalise");

    const p = JSON.parse(sender.sent[0].payload) as ReputationUpdatePayload;
    assert.ok(
      Math.abs(p.score_delta - (-1.0)) < 1e-9,
      `expected delta clamped to -1.0, got ${p.score_delta}`
    );
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// handleGossipPacket
// ─────────────────────────────────────────────────────────────────────────────

describe("ReputationGossipService — handleGossipPacket", () => {
  // 5. returns false on invalid signature
  it("returns false on invalid signature", () => {
    const { signing, gossip } = makeServices();
    signing.verifyResult = false;

    const packet = buildGossipPacket();
    const result = gossip.handleGossipPacket(packet, DUMMY_KEY);

    assert.equal(result, false);
  });

  // 6. returns false on wrong type
  it("returns false on wrong type", () => {
    const { gossip } = makeServices();

    const packet = buildGossipPacket({}, { type: 99 });
    const result = gossip.handleGossipPacket(packet, DUMMY_KEY);

    assert.equal(result, false);
  });

  // 7. returns false on stale timestamp
  it("returns false on stale timestamp", () => {
    const { gossip } = makeServices();

    const staleMs = Date.now() - 10 * 60 * 1000; // 10 minutes ago — outside 5 min window
    const packet  = buildGossipPacket({ timestamp_ms: staleMs });
    const result  = gossip.handleGossipPacket(packet, DUMMY_KEY);

    assert.equal(result, false);
  });

  // 8. returns false on missing reporter
  it("returns false on missing reporter", () => {
    const { gossip } = makeServices();

    const packet = buildGossipPacket({ reporter_uhid: "" });
    const result = gossip.handleGossipPacket(packet, DUMMY_KEY);

    assert.equal(result, false);
  });

  // 9. returns false on own gossip
  it("returns false on own gossip (reporter === localUhid)", () => {
    const { gossip } = makeServices();

    // reporter is ourselves — should be rejected
    const packet = buildGossipPacket({ reporter_uhid: LOCAL_UHID });
    const result = gossip.handleGossipPacket(packet, DUMMY_KEY);

    assert.equal(result, false);
  });

  // 10. applies full delta for unknown reporter (R=1.0)
  it("applies full delta for unknown reporter (R=1.0)", () => {
    const { gossip, reputation } = makeServices();

    // Reporter has never been seen → R = 1.0
    // claimed delta = -0.2, effective = -0.2 × 1.0 = -0.2
    const packet = buildGossipPacket({ score_delta: -0.2 });
    const result = gossip.handleGossipPacket(packet, DUMMY_KEY);

    assert.equal(result, true);
    const score = reputation.getReputationScore(TARGET_UHID);
    assert.ok(
      Math.abs(score - 0.8) < 1e-9,
      `expected target score ~0.80, got ${score}`
    );
  });

  // 11. applies weighted delta for degraded reporter
  it("applies weighted delta for degraded reporter (R≈0.50)", () => {
    const { gossip, reputation } = makeServices();

    // Degrade REPORTER_UHID to R=0.50 via recordRreqFloodAttempt × 10
    // Each call applies −0.05, so 10 × −0.05 = −0.50 → score = 0.50
    for (let i = 0; i < 10; i++) {
      reputation.recordRreqFloodAttempt(REPORTER_UHID);
    }

    const R = reputation.getReputationScore(REPORTER_UHID);
    assert.ok(Math.abs(R - 0.50) < 1e-9, `expected reporter R=0.50, got ${R}`);

    // claimed delta = -0.4, effective = -0.4 × 0.50 = -0.20
    const packet = buildGossipPacket({ score_delta: -0.4 });
    const result = gossip.handleGossipPacket(packet, DUMMY_KEY);

    assert.equal(result, true);

    const targetScore = reputation.getReputationScore(TARGET_UHID);
    const expected    = 1.0 + (-0.4 * 0.50); // 1.0 - 0.20 = 0.80
    assert.ok(
      Math.abs(targetScore - expected) < 1e-9,
      `expected target score ~${expected}, got ${targetScore}`
    );
  });

  // 12. applies positive delta to improve target
  it("applies positive delta to improve target", () => {
    const { gossip, reputation } = makeServices();

    // First degrade the target so there is room to improve
    reputation.recordSignatureFailure(TARGET_UHID); // 1.0 - 0.20 = 0.80

    const before = reputation.getReputationScore(TARGET_UHID);
    assert.ok(Math.abs(before - 0.80) < 1e-9, `expected 0.80, got ${before}`);

    // Gossip a positive delta; reporter unknown → R=1.0
    const packet = buildGossipPacket({ score_delta: 0.1 });
    const result = gossip.handleGossipPacket(packet, DUMMY_KEY);

    assert.equal(result, true);

    const after = reputation.getReputationScore(TARGET_UHID);
    assert.ok(
      Math.abs(after - 0.90) < 1e-9,
      `expected target score ~0.90 after positive gossip, got ${after}`
    );
  });
});
