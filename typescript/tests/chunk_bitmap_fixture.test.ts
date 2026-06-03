// SPDX-License-Identifier: MIT
/**
 * Cross-language ChunkBitmap wire-format fixture verifier — TypeScript runner.
 *
 * Reads fixtures/content/chunk_bitmap_vectors.json and verifies that this
 * implementation produces bit-identical bitsets and JSON payloads for each
 * pinned test vector.
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

// ── Inline implementations (spec compliance — no src/ imports) ────────────────

function bitsetEncode(chunkCount: number, haveIndices: readonly number[]): Uint8Array {
  if (chunkCount <= 0) return new Uint8Array(0);
  const bytes = new Uint8Array(Math.ceil(chunkCount / 8));
  for (const i of haveIndices) bytes[i >> 3] |= 1 << (i & 7);
  return bytes;
}

function bitsetDecode(bitset: Uint8Array, chunkCount: number): number[] {
  const result: number[] = [];
  const limit = Math.min(chunkCount, bitset.length * 8);
  for (let i = 0; i < limit; i++)
    if (bitset[i >> 3] & (1 << (i & 7))) result.push(i);
  return result;
}

function marshalJson(rootHash: string, chunkCount: number, haveBitset: Uint8Array, generation: number): string {
  const b64 = Buffer.from(haveBitset).toString("base64");
  return `{"root_hash":${JSON.stringify(rootHash)},"chunk_count":${chunkCount},"have_bitset":${JSON.stringify(b64)},"generation":${generation}}`;
}

// ── Fixture loader ─────────────────────────────────────────────────────────────

interface ChunkBitmapVector {
  name: string;
  description: string;
  root_hash: string;
  chunk_count: number;
  have_indices: number[];
  have_bitset_hex: string;
  have_bitset_base64: string;
  generation: number;
  expected_json: string;
}

function findFixtures(): string {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (let i = 0; i < 10; i++) {
    const candidate = resolve(dir, "fixtures", "content", "chunk_bitmap_vectors.json");
    try { readFileSync(candidate); return candidate; } catch {}
    const parent = dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  throw new Error("Could not locate fixtures/content/chunk_bitmap_vectors.json");
}

const vectors: ChunkBitmapVector[] = JSON.parse(readFileSync(findFixtures(), "utf8"));

// ── Tests ──────────────────────────────────────────────────────────────────────

describe("ChunkBitmap — cross-language fixtures", () => {
  describe("encode produces correct bitset", () => {
    for (const v of vectors) {
      it(v.name, () => {
        const bitset = bitsetEncode(v.chunk_count, v.have_indices);
        assert.equal(Buffer.from(bitset).toString("hex"), v.have_bitset_hex.toLowerCase());
        assert.equal(Buffer.from(bitset).toString("base64"), v.have_bitset_base64);
      });
    }
  });

  describe("decode recovers correct indices", () => {
    for (const v of vectors) {
      it(v.name, () => {
        const bitset = new Uint8Array(Buffer.from(v.have_bitset_base64, "base64"));
        const recovered = bitsetDecode(bitset, v.chunk_count);
        assert.deepEqual([...recovered].sort((a,b)=>a-b), [...v.have_indices].sort((a,b)=>a-b));
      });
    }
  });

  describe("JSON serialization matches expected", () => {
    for (const v of vectors) {
      it(v.name, () => {
        const bitset = bitsetEncode(v.chunk_count, v.have_indices);
        const actual = marshalJson(v.root_hash, v.chunk_count, bitset, v.generation);
        assert.equal(actual, v.expected_json);
      });
    }
  });

  describe("bitset length is ceil(chunkCount/8)", () => {
    for (const v of vectors) {
      it(v.name, () => {
        const bitset = bitsetEncode(v.chunk_count, v.have_indices);
        assert.equal(bitset.length, Math.ceil(v.chunk_count / 8));
      });
    }
  });

  describe("trailing bits are zero", () => {
    for (const v of vectors) {
      it(v.name, () => {
        const bitset = bitsetEncode(v.chunk_count, v.have_indices);
        if (bitset.length === 0) return;
        const trailingBits = v.chunk_count % 8;
        if (trailingBits === 0) return;
        const lastByte = bitset[bitset.length - 1];
        const validMask = (1 << trailingBits) - 1;
        assert.equal(lastByte & ~validMask, 0);
      });
    }
  });
});
