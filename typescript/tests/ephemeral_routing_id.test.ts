/**
 * Tests for the Ephemeral Routing Id (ERID) identity primitive.
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/ephemeral_routing_id.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import {
  deriveRoutingKey,
  deriveForEpoch,
  derive,
  epochFor,
  DEFAULT_LENGTH,
} from "../src/identity/EphemeralRoutingId.js";

// ── Canonical cross-language parity vectors ─────────────────────────────────────
//
// GROUND TRUTH, derived from the C# reference
// (src/AetherNet.Core/Identity/EphemeralRoutingId.cs). Every language port MUST
// reproduce these byte-for-byte. Do not edit without regenerating from C#.

const ROUTING_KEY_VECTORS: Record<string, string> = {
  "node-secret-A": "206f67e52afa8de0624fd3a2efc5bd68c65879ab623141811c996f0d416345e3",
  "node-B": "b071f5176536876b74a8927a242decea37aba390df06ec0019b711122c05384b",
  n: "44874ed0e4e94dc12ea647a9460644feb1495f7dd348e583fcd3c5399388819a",
};

const ERID_VECTORS: Array<[string, number, string]> = [
  ["node-secret-A", 0, "Q3AN7RWEGZBPZ5WM"],
  ["node-secret-A", 1, "N1HGBC2VC72W0A7E"],
  ["node-secret-A", 100, "KYF9JXYE3XJGFK26"],
  ["node-secret-A", 12345, "ZFM5AZMY6K0TGEK0"],
  ["node-secret-A", 1371, "N080TN3W537B27ZE"],
  ["node-B", 0, "61V5RVS7BVEBTV39"],
  ["node-B", 1, "6NQ731EA0HNGAN3C"],
  ["node-B", 100, "PDEMCT481QBWQN9P"],
  ["node-B", 12345, "H2D11G5JJY5EQ0PW"],
  ["node-B", 1371, "003WA1T3KDQVSDET"],
  ["n", 0, "GGY1T8FKNWCFXS71"],
  ["n", 1, "76AA5GEDFJ669RQS"],
  ["n", 100, "CFSM7DAP0Z1QT2KT"],
  ["n", 12345, "MJT2C0EYGYVRF4KN"],
  ["n", 1371, "39MYY8R0ZA292MPD"],
];

const CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

function key(secret: string): Uint8Array {
  return deriveRoutingKey(new TextEncoder().encode(secret));
}

function hex(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("hex");
}

describe("EphemeralRoutingId — canonical parity vectors", () => {
  it("routing key matches canonical vectors", () => {
    for (const [secret, want] of Object.entries(ROUTING_KEY_VECTORS)) {
      assert.equal(hex(key(secret)), want, `routing key for ${secret}`);
    }
  });

  it("ERID matches canonical vectors", () => {
    for (const [secret, epoch, want] of ERID_VECTORS) {
      assert.equal(deriveForEpoch(key(secret), epoch), want, `ERID for (${secret}, ${epoch})`);
    }
  });
});

describe("EphemeralRoutingId — behaviour", () => {
  it("is deterministic for the same key and epoch", () => {
    const k = key("node-secret-A");
    assert.equal(deriveForEpoch(k, 12345), deriveForEpoch(k, 12345));
  });

  it("rotates across consecutive epochs", () => {
    const k = key("node-secret-A");
    assert.notEqual(deriveForEpoch(k, 100), deriveForEpoch(k, 101));
  });

  it("differs by node in the same epoch", () => {
    assert.notEqual(deriveForEpoch(key("node-A"), 7), deriveForEpoch(key("node-B"), 7));
  });

  it("has the expected length and uses the Crockford alphabet only", () => {
    const id = deriveForEpoch(key("n"), 1);
    assert.equal(id.length, DEFAULT_LENGTH);
    for (const c of id) assert.ok(CROCKFORD.includes(c), `char ${c} not in alphabet`);
  });

  it("computes the window index (epochFor)", () => {
    const cases: Array<[number, number, bigint]> = [
      [0, 900, 0n],
      [899, 900, 0n],
      [900, 900, 1n],
      [1800, 900, 2n],
      [1234567, 900, 1371n],
      [-50, 900, 0n], // negative clamps to 0
    ];
    for (const [u, e, want] of cases) {
      assert.equal(epochFor(u, e), want, `epochFor(${u}, ${e})`);
    }
  });

  it("is stable within a window but changes at the boundary", () => {
    const k = key("n");
    assert.equal(derive(k, 1000), derive(k, 1500));
    assert.notEqual(derive(k, 1000), derive(k, 2000));
  });

  it("derives a deterministic 256-bit key distinct from the seed", () => {
    const seed = new TextEncoder().encode("ed25519-private-key-material-seed");
    const k1 = deriveRoutingKey(seed);
    const k2 = deriveRoutingKey(seed);
    assert.deepEqual(k1, k2);
    assert.equal(k1.length, 32);
    assert.notDeepEqual(k1, seed);
    assert.notDeepEqual(deriveRoutingKey(new TextEncoder().encode("a-different-identity")), k1);
  });

  it("rejects empty inputs", () => {
    assert.throws(() => deriveRoutingKey(new Uint8Array(0)));
    assert.throws(() => deriveForEpoch(new Uint8Array(0), 1));
  });
});
