/**
 * Unit tests for Ed25519Service and PacketSigning (signPacket, verifyPacket,
 * PacketDeduplicator).
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/security.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { Ed25519Service } from "../src/security/Ed25519Service.js";
import {
  signPacket,
  verifyPacket,
  PacketDeduplicator,
} from "../src/security/PacketSigning.js";
import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";

// ── Helpers ───────────────────────────────────────────────────────────────────

function makeDataPacket(from = "alice"): MeshPacket {
  const p = new MeshPacket();
  p.type = PacketType.Data;
  p.sourceUhid = from;
  p.payload = new TextEncoder().encode("hello aether");
  p.timestampMs = BigInt(Date.now());
  return p;
}

function allZero(buf: Uint8Array): boolean {
  return buf.every((b) => b === 0);
}

// ── Ed25519Service — generateKeyPair ─────────────────────────────────────────

describe("Ed25519Service — generateKeyPair", () => {
  it("returns 32-byte privateKey and 32-byte publicKey", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    assert.equal(privateKey.length, 32);
    assert.equal(publicKey.length, 32);
  });

  it("private key is not all-zero", () => {
    const { privateKey } = Ed25519Service.generateKeyPair();
    assert.ok(!allZero(privateKey), "privateKey should not be all-zero");
  });

  it("public key is not all-zero", () => {
    const { publicKey } = Ed25519Service.generateKeyPair();
    assert.ok(!allZero(publicKey), "publicKey should not be all-zero");
  });

  it("two calls produce different key pairs", () => {
    const kp1 = Ed25519Service.generateKeyPair();
    const kp2 = Ed25519Service.generateKeyPair();
    assert.notDeepEqual(kp1.privateKey, kp2.privateKey);
    assert.notDeepEqual(kp1.publicKey, kp2.publicKey);
  });
});

// ── Ed25519Service — sign ─────────────────────────────────────────────────────

describe("Ed25519Service — sign", () => {
  it("returns 64-byte signature", () => {
    const { privateKey } = Ed25519Service.generateKeyPair();
    const data = new TextEncoder().encode("test message");
    const sig = Ed25519Service.sign(privateKey, data);
    assert.equal(sig.length, 64);
  });

  it("is deterministic for the same key and data", () => {
    const { privateKey } = Ed25519Service.generateKeyPair();
    const data = new TextEncoder().encode("deterministic");
    const sig1 = Ed25519Service.sign(privateKey, data);
    const sig2 = Ed25519Service.sign(privateKey, data);
    assert.deepEqual(sig1, sig2);
  });

  it("produces different signatures for different data", () => {
    const { privateKey } = Ed25519Service.generateKeyPair();
    const sig1 = Ed25519Service.sign(privateKey, new TextEncoder().encode("msg1"));
    const sig2 = Ed25519Service.sign(privateKey, new TextEncoder().encode("msg2"));
    assert.notDeepEqual(sig1, sig2);
  });

  it("throws for private key with wrong length", () => {
    const badKey = new Uint8Array(16); // too short
    assert.throws(() => Ed25519Service.sign(badKey, new Uint8Array(4)));
  });
});

// ── Ed25519Service — verify ───────────────────────────────────────────────────

describe("Ed25519Service — verify", () => {
  it("returns true for a valid signature", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const data = new TextEncoder().encode("valid payload");
    const sig = Ed25519Service.sign(privateKey, data);
    const ok = Ed25519Service.verify(publicKey, data, sig);
    assert.equal(ok, true);
  });

  it("returns false for a tampered signature", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const data = new TextEncoder().encode("valid payload");
    const sig = Ed25519Service.sign(privateKey, data);
    sig[0] ^= 0xFF; // flip bits in first byte
    const ok = Ed25519Service.verify(publicKey, data, sig);
    assert.equal(ok, false);
  });

  it("returns false when data has been tampered", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const data = new TextEncoder().encode("original");
    const sig = Ed25519Service.sign(privateKey, data);
    const tampered = new TextEncoder().encode("tampered");
    const ok = Ed25519Service.verify(publicKey, tampered, sig);
    assert.equal(ok, false);
  });

  it("returns false for wrong public key", () => {
    const kp1 = Ed25519Service.generateKeyPair();
    const kp2 = Ed25519Service.generateKeyPair();
    const data = new TextEncoder().encode("test");
    const sig = Ed25519Service.sign(kp1.privateKey, data);
    const ok = Ed25519Service.verify(kp2.publicKey, data, sig);
    assert.equal(ok, false);
  });

  it("returns false for public key with wrong length", () => {
    const { privateKey } = Ed25519Service.generateKeyPair();
    const data = new TextEncoder().encode("test");
    const sig = Ed25519Service.sign(privateKey, data);
    const badPub = new Uint8Array(16);
    const ok = Ed25519Service.verify(badPub, data, sig);
    assert.equal(ok, false);
  });

  it("returns false for signature with wrong length", () => {
    const { publicKey } = Ed25519Service.generateKeyPair();
    const data = new TextEncoder().encode("test");
    const shortSig = new Uint8Array(16);
    const ok = Ed25519Service.verify(publicKey, data, shortSig);
    assert.equal(ok, false);
  });
});

// ── Ed25519Service — verifyWithFallback ───────────────────────────────────────

describe("Ed25519Service — verifyWithFallback", () => {
  it("returns true for a valid 32-byte public key", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const data = new TextEncoder().encode("fallback test");
    const sig = Ed25519Service.sign(privateKey, data);
    const ok = Ed25519Service.verifyWithFallback(publicKey, data, sig);
    assert.equal(ok, true);
  });

  it("returns false for a non-32-byte public key (graceful fallback)", () => {
    const { privateKey } = Ed25519Service.generateKeyPair();
    const data = new TextEncoder().encode("test");
    const sig = Ed25519Service.sign(privateKey, data);
    const oversizedKey = new Uint8Array(64).fill(0xAB);
    const ok = Ed25519Service.verifyWithFallback(oversizedKey, data, sig);
    assert.equal(ok, false);
  });

  it("returns false for tampered sig even with fallback path", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const data = new TextEncoder().encode("fallback tamper");
    const sig = Ed25519Service.sign(privateKey, data);
    sig[5] ^= 0x01;
    const ok = Ed25519Service.verifyWithFallback(publicKey, data, sig);
    assert.equal(ok, false);
  });
});

// ── signPacket / verifyPacket — round-trip ────────────────────────────────────

describe("signPacket / verifyPacket — round-trip", () => {
  it("signs and verifies a fresh packet", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const pkt = makeDataPacket("alice");
    signPacket(pkt, privateKey);
    assert.ok(pkt.signature.length > 0, "signature must be set after signPacket");
    assert.ok(pkt.packetNonce.length > 0, "nonce must be set after signPacket");
    const ok = verifyPacket(pkt, publicKey);
    assert.equal(ok, true);
  });

  it("sets an 8-byte nonce on the packet", () => {
    const { privateKey } = Ed25519Service.generateKeyPair();
    const pkt = makeDataPacket("alice");
    signPacket(pkt, privateKey);
    assert.equal(pkt.packetNonce.length, 8);
  });

  it("verifyPacket returns false for tampered payload", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const pkt = makeDataPacket("bob");
    signPacket(pkt, privateKey);
    // Flip first payload byte to simulate tampering.
    pkt.payload[0] ^= 0xFF;
    const ok = verifyPacket(pkt, publicKey);
    assert.equal(ok, false);
  });

  it("verifyPacket returns false for tampered signature", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const pkt = makeDataPacket("carol");
    signPacket(pkt, privateKey);
    pkt.signature[0] ^= 0x01;
    const ok = verifyPacket(pkt, publicKey);
    assert.equal(ok, false);
  });

  it("verifyPacket returns false for wrong public key", () => {
    const kp1 = Ed25519Service.generateKeyPair();
    const kp2 = Ed25519Service.generateKeyPair();
    const pkt = makeDataPacket("dave");
    signPacket(pkt, kp1.privateKey);
    const ok = verifyPacket(pkt, kp2.publicKey);
    assert.equal(ok, false);
  });

  it("verifyPacket returns false for an expired packet", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();
    const pkt = makeDataPacket("eve");
    // Back-date timestamp by 10 minutes (well past the default 300s window).
    pkt.timestampMs = BigInt(Date.now()) - BigInt(10 * 60 * 1000);
    signPacket(pkt, privateKey);
    const ok = verifyPacket(pkt, publicKey);
    assert.equal(ok, false, "expired packet must not verify");
  });
});

// ── PacketDeduplicator ────────────────────────────────────────────────────────
//
// API: isSeen(senderUhid: string, nonce: Uint8Array): boolean
//      mark(senderUhid: string, nonce: Uint8Array): void
//      checkAndMark(senderUhid: string, nonce: Uint8Array): boolean
//      (composite key is "senderUhid:hex(nonce)")

function makeNonce(): Uint8Array {
  const n = new Uint8Array(8);
  crypto.getRandomValues(n);
  return n;
}

describe("PacketDeduplicator — isSeen / mark", () => {
  it("returns false for a never-seen entry", () => {
    const dedup = new PacketDeduplicator();
    const nonce = makeNonce();
    assert.equal(dedup.isSeen("alice", nonce), false);
  });

  it("returns true after mark", () => {
    const dedup = new PacketDeduplicator();
    const nonce = makeNonce();
    dedup.mark("alice", nonce);
    assert.equal(dedup.isSeen("alice", nonce), true);
  });

  it("size increments on each new distinct entry", () => {
    const dedup = new PacketDeduplicator();

    dedup.mark("alice", makeNonce());
    assert.equal(dedup.size, 1);
    dedup.mark("bob", makeNonce());
    assert.equal(dedup.size, 2);
  });

  it("size does not grow on duplicate mark", () => {
    const dedup = new PacketDeduplicator();
    const nonce = makeNonce();
    dedup.mark("alice", nonce);
    dedup.mark("alice", nonce); // duplicate
    assert.equal(dedup.size, 1);
  });
});

describe("PacketDeduplicator — checkAndMark", () => {
  it("returns true first time (not seen), false on repeat (already seen)", () => {
    const dedup = new PacketDeduplicator();
    const nonce = makeNonce();

    // NOTE: checkAndMark returns true when the nonce is FRESH (not yet seen),
    // and false when it's already been recorded.
    const first = dedup.checkAndMark("alice", nonce);
    assert.equal(first, true, "first call should return true (fresh nonce)");

    const second = dedup.checkAndMark("alice", nonce);
    assert.equal(second, false, "second call should return false (already seen)");
  });
});

describe("PacketDeduplicator — composite key uses senderUhid + nonce", () => {
  it("same nonce bytes from different senders are distinct entries", () => {
    const dedup = new PacketDeduplicator();
    const sharedNonce = makeNonce();

    dedup.mark("alice", sharedNonce);
    // bob uses the SAME nonce bytes — but different sender, so it's a different key.
    assert.equal(
      dedup.isSeen("bob", sharedNonce),
      false,
      "different sender with same nonce should be a distinct entry"
    );
  });

  it("same sender with different nonces are distinct entries", () => {
    const dedup = new PacketDeduplicator();
    const nonce1 = makeNonce();
    const nonce2 = makeNonce();

    dedup.mark("alice", nonce1);
    assert.equal(dedup.isSeen("alice", nonce2), false);
  });
});

describe("PacketDeduplicator — clear", () => {
  it("resets size to zero and clears all seen entries", () => {
    const dedup = new PacketDeduplicator();
    const nonce = makeNonce();
    dedup.mark("alice", nonce);
    assert.equal(dedup.size, 1);

    dedup.clear();
    assert.equal(dedup.size, 0);
    assert.equal(dedup.isSeen("alice", nonce), false, "entry must be gone after clear");
  });
});
