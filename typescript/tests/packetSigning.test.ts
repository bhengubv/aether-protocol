/**
 * Unit tests for PacketSigningService — Item 21 reputation hooks.
 *
 * Covers:
 *   - Replay (duplicate nonce) fires recordReplayAttempt hook
 *   - Fresh nonce does NOT fire the replay hook
 *   - Bad signature fires recordSignatureFailure hook (via notifySignatureFailure)
 *   - No reputation attached → no error on replay
 *   - No reputation attached → no error on signature failure
 *   - notifySignatureFailure helper forwards to reputation directly
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/packetSigning.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { Ed25519Service } from "../src/security/Ed25519Service.js";
import { PacketSigningService } from "../src/security/PacketSigning.js";
import { NodeReputationService } from "../src/reputation.js";
import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";

// ── Test doubles ──────────────────────────────────────────────────────────────

/**
 * Minimal fake that records every call to the two hooks under test.
 * Mirrors the FakeReputation pattern from routing.test.ts.
 */
class FakeReputation {
  replayCalls: string[] = [];
  sigFailCalls: string[] = [];

  recordReplayAttempt(uhid: string): void     { this.replayCalls.push(uhid); }
  recordSignatureFailure(uhid: string): void  { this.sigFailCalls.push(uhid); }

  // Remaining interface methods — no-ops for this suite
  recordRreqFloodAttempt(_: string): void {}
  recordCustodyRefusal(_: string): void {}
  recordDeliverySuccess(_: string, __: number): void {}
  recordDeliveryFailure(_: string): void {}
  getReputationScore(_: string): number { return 1.0; }
  getAllScores(): Map<string, number> { return new Map(); }
  applyWeightedDelta(_: string, __: number): void {}
}

// ── Helpers ───────────────────────────────────────────────────────────────────

function makeSignedPacket(
  sourceUhid: string,
  privateKey: Uint8Array
): MeshPacket {
  const p = new MeshPacket();
  p.type = PacketType.Data;
  p.sourceUhid = sourceUhid;
  p.destinationUhid = "dest";
  p.payload = new TextEncoder().encode("hello");
  p.timestampMs = BigInt(Date.now());
  const svc = new PacketSigningService();
  svc.sign(p, privateKey);
  return p;
}

// ── Replay fires hook ─────────────────────────────────────────────────────────

describe("PacketSigningService — replay fires recordReplayAttempt", () => {
  it("fires once on the second presentation of the same (source, nonce) pair", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const svc = new PacketSigningService();
    const rep = new FakeReputation();
    svc.setReputation(rep as unknown as NodeReputationService);

    const pkt = makeSignedPacket("alice", privateKey);

    // First presentation — fresh nonce, hook must NOT fire.
    const first = svc.verifyAndDedup(pkt, publicKey);
    assert.equal(first, true, "first presentation must pass");
    assert.deepStrictEqual(rep.replayCalls, [], "no replay hook on first presentation");

    // Second presentation of the identical packet — duplicate nonce, hook MUST fire.
    const second = svc.verifyAndDedup(pkt, publicKey);
    assert.equal(second, false, "duplicate presentation must be rejected");
    assert.deepStrictEqual(rep.replayCalls, ["alice"], "replay hook must fire with sourceUhid");
  });

  it("fires with correct sourceUhid for each replaying sender independently", () => {
    const kp1 = Ed25519Service.generateKeyPair();
    const kp2 = Ed25519Service.generateKeyPair();
    const svc = new PacketSigningService();
    const rep = new FakeReputation();
    svc.setReputation(rep as unknown as NodeReputationService);

    const pktAlice = makeSignedPacket("alice", kp1.privateKey);
    const pktBob   = makeSignedPacket("bob",   kp2.privateKey);

    // First presentations — fresh.
    svc.verifyAndDedup(pktAlice, kp1.publicKey);
    svc.verifyAndDedup(pktBob,   kp2.publicKey);
    assert.deepStrictEqual(rep.replayCalls, []);

    // Replay both.
    svc.verifyAndDedup(pktAlice, kp1.publicKey);
    svc.verifyAndDedup(pktBob,   kp2.publicKey);
    assert.deepStrictEqual(rep.replayCalls.sort(), ["alice", "bob"]);
  });
});

// ── Fresh nonce does NOT fire replay hook ─────────────────────────────────────

describe("PacketSigningService — fresh nonce does NOT fire replay hook", () => {
  it("accepts a new packet without firing the replay hook", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const svc = new PacketSigningService();
    const rep = new FakeReputation();
    svc.setReputation(rep as unknown as NodeReputationService);

    // Each call to makeSignedPacket generates a fresh random nonce.
    const ok = svc.verifyAndDedup(makeSignedPacket("carol", privateKey), publicKey);
    assert.equal(ok, true);
    assert.deepStrictEqual(rep.replayCalls, []);
  });

  it("multiple distinct packets from the same sender do not fire the hook", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const svc = new PacketSigningService();
    const rep = new FakeReputation();
    svc.setReputation(rep as unknown as NodeReputationService);

    for (let i = 0; i < 5; i++) {
      const ok = svc.verifyAndDedup(makeSignedPacket("dave", privateKey), publicKey);
      assert.equal(ok, true, `packet ${i} should pass`);
    }
    assert.deepStrictEqual(rep.replayCalls, []);
  });
});

// ── Signature failure fires hook ──────────────────────────────────────────────

describe("PacketSigningService — signature failure fires recordSignatureFailure", () => {
  it("fires when the signature is tampered", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const svc = new PacketSigningService();
    const rep = new FakeReputation();
    svc.setReputation(rep as unknown as NodeReputationService);

    const pkt = makeSignedPacket("eve", privateKey);
    // Corrupt the signature — Ed25519 verify will return false.
    pkt.signature[0] ^= 0xFF;

    const ok = svc.verifyAndDedup(pkt, publicKey);
    assert.equal(ok, false, "tampered signature must be rejected");
    assert.deepStrictEqual(rep.sigFailCalls, ["eve"], "sig-failure hook must fire with sourceUhid");
    // Replay hook must NOT fire for a signature failure (different code path).
    assert.deepStrictEqual(rep.replayCalls, []);
  });

  it("fires when the wrong public key is used", () => {
    const kp1 = Ed25519Service.generateKeyPair();
    const kp2 = Ed25519Service.generateKeyPair();
    const svc = new PacketSigningService();
    const rep = new FakeReputation();
    svc.setReputation(rep as unknown as NodeReputationService);

    const pkt = makeSignedPacket("frank", kp1.privateKey);
    // Verify with a completely unrelated public key.
    const ok = svc.verifyAndDedup(pkt, kp2.publicKey);
    assert.equal(ok, false);
    assert.deepStrictEqual(rep.sigFailCalls, ["frank"]);
  });

  it("notifySignatureFailure helper fires the hook directly", () => {
    const svc = new PacketSigningService();
    const rep = new FakeReputation();
    svc.setReputation(rep as unknown as NodeReputationService);

    svc.notifySignatureFailure("grace");
    assert.deepStrictEqual(rep.sigFailCalls, ["grace"]);
    assert.deepStrictEqual(rep.replayCalls, []);
  });
});

// ── No reputation = no error ──────────────────────────────────────────────────

describe("PacketSigningService — no reputation service attached", () => {
  it("replay does not throw when reputation is null", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const svc = new PacketSigningService(); // reputation starts as null

    const pkt = makeSignedPacket("henry", privateKey);
    svc.verifyAndDedup(pkt, publicKey); // first — OK
    // Second presentation triggers replay path, but reputation is null — must not throw.
    assert.doesNotThrow(() => svc.verifyAndDedup(pkt, publicKey));
  });

  it("signature failure does not throw when reputation is null", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const svc = new PacketSigningService(); // reputation starts as null

    const pkt = makeSignedPacket("iris", privateKey);
    pkt.signature[0] ^= 0xFF; // tamper
    assert.doesNotThrow(() => svc.verifyAndDedup(pkt, publicKey));
  });

  it("notifySignatureFailure does not throw when reputation is null", () => {
    const svc = new PacketSigningService();
    assert.doesNotThrow(() => svc.notifySignatureFailure("jack"));
  });

  it("setReputation(null) detaches an existing reputation without error", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const svc = new PacketSigningService();
    const rep = new FakeReputation();
    svc.setReputation(rep as unknown as NodeReputationService);

    // Detach.
    svc.setReputation(null);

    const pkt = makeSignedPacket("karen", privateKey);
    svc.verifyAndDedup(pkt, publicKey);
    // Replay — reputation is now null, must not throw.
    assert.doesNotThrow(() => svc.verifyAndDedup(pkt, publicKey));
    // No calls recorded since reputation was detached before the replay.
    assert.deepStrictEqual(rep.replayCalls, []);
  });
});
