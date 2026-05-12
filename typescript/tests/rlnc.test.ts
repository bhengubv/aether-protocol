/**
 * Unit tests for the RLNC engine (GF(2⁸), encoder, decoder, codec).
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/rlnc.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { RlncCodec, RlncDecoder, RlncEncoder } from "../src/transport/rlnc.js";

// Re-export the internal GF(256) functions for white-box testing by importing
// the module and accessing the compiled IIFE side-effect tables via the codec's
// encode/decode round-trips (behavioural approach — tables are module-private).

// ── Helpers ────────────────────────────────────────────────────────────────────

function encode(s: string): Uint8Array {
  return new TextEncoder().encode(s);
}

function splitPackets(buf: Uint8Array, count: number): Uint8Array[] {
  const pktSize = buf.length / count;
  const out: Uint8Array[] = [];
  for (let i = 0; i < count; i++) {
    out.push(buf.subarray(i * pktSize, (i + 1) * pktSize));
  }
  return out;
}

// ── RlncCodec: basic round-trips ───────────────────────────────────────────────

describe("RlncCodec — round-trip", () => {
  it("decodes with exactly K systematic packets", () => {
    const source = encode("aether-rlnc-typescript-test");
    const codec = new RlncCodec(4);
    const encoded = codec.encode(source, 4);
    const pkts = splitPackets(encoded, 4);
    const decoded = codec.tryDecode(pkts, 4);
    assert.ok(decoded, "tryDecode returned null");
    assert.deepEqual(
      decoded!.subarray(0, source.length),
      source,
      "decoded payload mismatch"
    );
  });

  it("decodes with K systematic + 2 repair packets (6 total, use first 4)", () => {
    const source = encode("hello from RLNC with repair");
    const codec = new RlncCodec(4);
    const encoded = codec.encode(source, 6);
    const pkts = splitPackets(encoded, 6).slice(0, 4); // first 4 systematic
    const decoded = codec.tryDecode(pkts, 4);
    assert.ok(decoded);
    assert.deepEqual(decoded!.subarray(0, source.length), source);
  });

  it("decodes using only repair packets (skip systematic)", () => {
    const source = encode("repair-only round-trip test payload");
    const codec = new RlncCodec(4);
    const encoded = codec.encode(source, 8); // 4 systematic + 4 repair
    const pkts = splitPackets(encoded, 8).slice(4); // skip systematic
    const decoded = codec.tryDecode(pkts, 4);
    assert.ok(decoded, "repair-only decode failed");
    assert.deepEqual(decoded!.subarray(0, source.length), source);
  });

  it("K=1 single-symbol round-trip", () => {
    const source = encode("x");
    const codec = new RlncCodec(1);
    const encoded = codec.encode(source, 2);
    const pkts = splitPackets(encoded, 2).slice(0, 1);
    const decoded = codec.tryDecode(pkts, 1);
    assert.ok(decoded);
    assert.equal(decoded![0], "x".charCodeAt(0));
  });

  it("K=16 large-payload round-trip", () => {
    const source = new Uint8Array(1024);
    for (let i = 0; i < 1024; i++) source[i] = i & 0xff;
    const codec = new RlncCodec(16);
    const encoded = codec.encode(source, 20);
    const pkts = splitPackets(encoded, 20);
    const decoded = codec.tryDecode(pkts, 16);
    assert.ok(decoded);
    assert.deepEqual(decoded!.subarray(0, source.length), source);
  });

  it("returns null when no packets supplied", () => {
    const codec = new RlncCodec(4);
    assert.equal(codec.tryDecode([], 4), null);
  });
});

// ── RlncDecoder: low-level API ─────────────────────────────────────────────────

describe("RlncDecoder — low-level", () => {
  it("starts at rank 0, not complete", () => {
    const dec = new RlncDecoder(4, 8);
    assert.equal(dec.rank, 0);
    assert.equal(dec.isComplete, false);
  });

  it("linearly dependent packet does not increase rank", () => {
    const dec = new RlncDecoder(3, 4);
    const coeff = new Uint8Array([1, 0, 0]);
    const data = new Uint8Array([10, 20, 30, 40]);
    assert.equal(dec.addPacket(coeff, data), true);
    assert.equal(dec.addPacket(coeff, data), false, "duplicate should be rejected");
    assert.equal(dec.rank, 1);
  });

  it("reaches isComplete after K independent packets", () => {
    const k = 3, s = 2;
    const dec = new RlncDecoder(k, s);
    for (let i = 0; i < k; i++) {
      const coeff = new Uint8Array(k);
      coeff[i] = 1;
      const data = new Uint8Array([i + 1, i + 100]);
      dec.addPacket(coeff, data);
    }
    assert.equal(dec.isComplete, true);
  });

  it("tryDecode returns null when incomplete", () => {
    const dec = new RlncDecoder(4, 4);
    assert.equal(dec.tryDecode(), null);
  });

  it("tryDecode reconstructs correct symbol ordering", () => {
    const k = 3, s = 2;
    const dec = new RlncDecoder(k, s);
    const sources = [[0xAA, 0xBB], [0xCC, 0xDD], [0xEE, 0xFF]];
    for (let i = 0; i < k; i++) {
      const coeff = new Uint8Array(k);
      coeff[i] = 1;
      dec.addPacket(coeff, new Uint8Array(sources[i]));
    }
    const result = dec.tryDecode()!;
    for (let i = 0; i < k; i++) {
      assert.equal(result[i * s],     sources[i][0], `symbol[${i}][0] mismatch`);
      assert.equal(result[i * s + 1], sources[i][1], `symbol[${i}][1] mismatch`);
    }
  });
});

// ── RlncEncoder: systematic mode ──────────────────────────────────────────────

describe("RlncEncoder — systematic", () => {
  it("first K packets are systematic (identity coefficients + exact data)", () => {
    const k = 4, s = 3;
    const syms: Uint8Array[] = [];
    for (let i = 0; i < k; i++) {
      syms.push(new Uint8Array([i + 1, i + 10, i + 100]));
    }
    const enc = new RlncEncoder(syms, true);
    for (let i = 0; i < k; i++) {
      const { coefficients, encodedSymbol } = enc.nextPacket();
      // Coefficient vector must be e_i.
      for (let j = 0; j < k; j++) {
        assert.equal(coefficients[j], j === i ? 1 : 0, `pkt ${i} coeff[${j}]`);
      }
      // Data must match source.
      assert.deepEqual(encodedSymbol, syms[i], `pkt ${i} data mismatch`);
    }
  });

  it("repair packets always have at least one non-zero coefficient", () => {
    const k = 3;
    const syms: Uint8Array[] = [
      new Uint8Array([1, 2, 3]),
      new Uint8Array([4, 5, 6]),
      new Uint8Array([7, 8, 9]),
    ];
    const enc = new RlncEncoder(syms, false); // non-systematic → all repair
    for (let i = 0; i < 20; i++) {
      const { coefficients } = enc.nextPacket();
      const allZero = Array.from(coefficients).every(c => c === 0);
      assert.equal(allZero, false, `repair pkt ${i} has all-zero coefficients`);
    }
  });
});

// ── Codec metadata ─────────────────────────────────────────────────────────────

describe("RlncCodec — metadata", () => {
  it("codecName is RLNC-GF256", () => {
    assert.equal(new RlncCodec(4).codecName, "RLNC-GF256");
  });
  it("overheadFraction is 0.05", () => {
    assert.equal(new RlncCodec(4).overheadFraction, 0.05);
  });
  it("rejects generation_size = 0", () => {
    assert.throws(() => new RlncCodec(0), RangeError);
  });
  it("rejects generation_size = 256", () => {
    assert.throws(() => new RlncCodec(256), RangeError);
  });
});
