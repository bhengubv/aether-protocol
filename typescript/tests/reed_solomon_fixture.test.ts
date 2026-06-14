/**
 * Cross-language vault parity: TS must reproduce the C# reference vectors
 * (fixtures/vault/reed_solomon_basic.json) byte-for-byte — every systematic data
 * shard, every Cauchy parity shard, and every K-of-N recovery. K-1 survivors must
 * fail. Mirrors the Go fixture test.
 *
 * Run with: tsx --test typescript/tests/reed_solomon_fixture.test.ts
 * SPDX-License-Identifier: MIT
 */

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { ReedSolomonCodec } from "../src/vault/ReedSolomonCodec.js";
import { encodeData, reconstructData } from "../src/vault/VaultCodec.js";

interface RsVectors {
  field: { primitive_polynomial: string; alpha: number; gf_bits: number };
  k: number;
  m: number;
  n: number;
  input_size: number;
  shard_size: number;
  input: string;
  shards: { index: number; hex: string }[];
  recovery: { note: string; survivor_indices: number[]; recovered: string }[];
  should_fail: { note: string; survivor_indices: number[] };
}

const vectorsPath = fileURLToPath(
  new URL("../../fixtures/vault/reed_solomon_basic.json", import.meta.url),
);
const V = JSON.parse(readFileSync(vectorsPath, "utf8")) as RsVectors;

const hex = (b: Uint8Array): string => Buffer.from(b).toString("hex");
const fromHex = (s: string): Uint8Array => new Uint8Array(Buffer.from(s, "hex"));

function buildShards(): Uint8Array[] {
  const codec = new ReedSolomonCodec(V.k, V.m);
  return encodeData(codec, fromHex(V.input));
}

test("rs: fixture params are the canonical K=10/M=4/N=14 over GF(2⁸) 0x11D α=2", () => {
  assert.equal(V.k, 10);
  assert.equal(V.m, 4);
  assert.equal(V.n, 14);
  assert.equal(V.field.primitive_polynomial, "0x11D");
  assert.equal(V.field.alpha, 2);
  assert.equal(V.field.gf_bits, 8);
});

test("rs: encoder reproduces every C# shard (systematic data + Cauchy parity) byte-for-byte", () => {
  const input = fromHex(V.input);
  assert.equal(input.length, V.input_size, "input size");

  const codec = new ReedSolomonCodec(V.k, V.m);
  const shards = encodeData(codec, input);

  assert.equal(shards.length, V.n, "shard count");
  assert.equal(shards[0].length, V.shard_size, "shard size");

  for (const want of V.shards) {
    assert.equal(hex(shards[want.index]), want.hex, `shard ${want.index}`);
  }
});

test("rs: every recovery subset decodes to the fixture input byte-for-byte", () => {
  const input = fromHex(V.input);
  const codec = new ReedSolomonCodec(V.k, V.m);
  const shards = encodeData(codec, input);

  for (const rec of V.recovery) {
    const available = new Map<number, Uint8Array>();
    for (const idx of rec.survivor_indices) {
      available.set(idx, shards[idx]);
    }

    const recovered = reconstructData(codec, available, V.input_size);

    assert.equal(hex(recovered), rec.recovered, `recovery: ${rec.note}`);
    // The recovered blob must equal the original input.
    assert.deepEqual(
      Array.from(recovered),
      Array.from(input),
      `recovery "${rec.note}" reproduces the original input`,
    );
  }
});

test("rs: only K-1 survivors is unrecoverable (the fixture should_fail case)", () => {
  const codec = new ReedSolomonCodec(V.k, V.m);
  const shards = encodeData(codec, fromHex(V.input));

  assert.equal(
    V.should_fail.survivor_indices.length,
    V.k - 1,
    "should_fail must carry K-1 survivors",
  );

  const available = new Map<number, Uint8Array>();
  for (const idx of V.should_fail.survivor_indices) {
    available.set(idx, shards[idx]);
  }

  assert.throws(
    () => reconstructData(codec, available, V.input_size),
    /fewer than K shards/,
    "K-1 survivors must FAIL decoding",
  );
});

test("rs: recovery from JUST the M parity shards + enough data shards to reach K (matrix inversion path)", () => {
  const input = fromHex(V.input);
  const codec = new ReedSolomonCodec(V.k, V.m);
  const shards = buildShards();

  // Drop the first M data shards; survive on data[M..K-1] + all M parity = K total.
  const available = new Map<number, Uint8Array>();
  for (let i = V.m; i < V.k; i++) {
    available.set(i, shards[i]);
  }
  for (let i = V.k; i < V.n; i++) {
    available.set(i, shards[i]);
  }

  const recovered = reconstructData(codec, available, V.input_size);
  assert.deepEqual(
    Array.from(recovered),
    Array.from(input),
    "parity-assisted recovery reproduces the original input",
  );
});
