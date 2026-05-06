/**
 * Nonce-deduplication tests — confirms the dedup key is (senderUhid, nonce)
 * not nonce-alone. Pre-2026-05-05 the C# reference used nonce-alone, which
 * had two failure modes:
 *
 *   1. Cross-sender nonce collision dropped the legitimate sender's first
 *      packet (8-byte random collision is rare but real over a long-lived
 *      window).
 *   2. An attacker pre-registering a chosen nonce against the recipient
 *      could block a legitimate sender's first packet.
 *
 * Both go away with (source, nonce) keying. This file locks in that
 * behaviour for the TS port.
 *
 * SPDX-License-Identifier: MIT
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { PacketDeduplicator } from "../src/security/PacketSigning.js";

const ALICE = "alice";
const BOB = "bob";

function nonce(...bytes: number[]): Uint8Array {
  return new Uint8Array(bytes.length === 8 ? bytes : [0, 0, 0, 0, 0, 0, 0, ...bytes].slice(-8));
}

describe("PacketDeduplicator — keying by (sender, nonce)", () => {
  it("isSeen returns false on a fresh entry", () => {
    const dedup = new PacketDeduplicator();
    assert.equal(dedup.isSeen(ALICE, nonce(1)), false);
  });

  it("mark + isSeen detects a duplicate from the same sender", () => {
    const dedup = new PacketDeduplicator();
    const n = nonce(1, 2, 3, 4, 5, 6, 7, 8);
    dedup.mark(ALICE, n);
    assert.equal(dedup.isSeen(ALICE, n), true);
  });

  it("the SAME nonce from a DIFFERENT sender is NOT a duplicate (regression)", () => {
    // The defining property of (sender, nonce) keying. With nonce-alone,
    // this test would fail and a legitimate Bob packet would be dropped.
    const dedup = new PacketDeduplicator();
    const n = nonce(1, 2, 3, 4, 5, 6, 7, 8);
    dedup.mark(ALICE, n);
    assert.equal(dedup.isSeen(BOB, n), false);
  });

  it("attacker pre-registering a chosen nonce does not block the legitimate sender", () => {
    // Equivalent to test 3 but framed as the second failure mode: a pre-
    // registered (attacker, n) does not poison (alice, n).
    const dedup = new PacketDeduplicator();
    const attackerSpoofed = "spoofed-as-alice";
    const n = nonce(0xff, 0xff, 0, 0, 0, 0, 0, 0);
    dedup.mark(attackerSpoofed, n);
    assert.equal(dedup.isSeen(ALICE, n), false);
  });

  it("checkAndMark is atomic — first call accepts, second rejects", () => {
    const dedup = new PacketDeduplicator();
    const n = nonce(9);
    assert.equal(dedup.checkAndMark(ALICE, n), true);
    assert.equal(dedup.checkAndMark(ALICE, n), false);
  });

  it("checkAndMark accepts the same nonce from a different sender", () => {
    const dedup = new PacketDeduplicator();
    const n = nonce(0x42);
    assert.equal(dedup.checkAndMark(ALICE, n), true);
    assert.equal(dedup.checkAndMark(BOB, n), true);
  });

  it("clear() drops all entries", () => {
    const dedup = new PacketDeduplicator();
    dedup.mark(ALICE, nonce(1));
    dedup.mark(BOB, nonce(2));
    assert.equal(dedup.size, 2);
    dedup.clear();
    assert.equal(dedup.size, 0);
    assert.equal(dedup.isSeen(ALICE, nonce(1)), false);
  });

  it("size grows with distinct (sender, nonce) pairs", () => {
    const dedup = new PacketDeduplicator();
    dedup.mark(ALICE, nonce(1));
    dedup.mark(ALICE, nonce(2));
    dedup.mark(BOB, nonce(1)); // same nonce, different sender — distinct entry
    assert.equal(dedup.size, 3);
  });

  it("re-marking an existing pair does not bloat the map", () => {
    const dedup = new PacketDeduplicator();
    const n = nonce(7);
    dedup.mark(ALICE, n);
    dedup.mark(ALICE, n);
    dedup.mark(ALICE, n);
    assert.equal(dedup.size, 1);
  });

  it("uses bytewise comparison — different nonce contents are distinct", () => {
    const dedup = new PacketDeduplicator();
    dedup.mark(ALICE, new Uint8Array([0, 0, 0, 0, 0, 0, 0, 1]));
    assert.equal(dedup.isSeen(ALICE, new Uint8Array([0, 0, 0, 0, 0, 0, 0, 2])), false);
    assert.equal(dedup.isSeen(ALICE, new Uint8Array([0, 0, 0, 0, 0, 0, 0, 1])), true);
  });
});
