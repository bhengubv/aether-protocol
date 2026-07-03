/**
 * BLE tracking-protection parity tests (TypeScript).
 *
 * Drives the rotating Service UUID + IRK-based RPA through the shared vectors at
 * `fixtures/bleprivacy/vectors.json`: for every uuid vector,
 * serviceUuid(rotationKey, window) == uuid; for every rpa vector,
 * toHex(resolvableAddress(irk, window)) == rpa AND resolveAddress(irk, rpa) is
 * true while resolveAddress(wrongIrk, rpa) is false — byte-for-byte. Every
 * AetherNet language SDK drives the SAME vectors and MUST reproduce them.
 *
 * Run with: tsx --test typescript/tests/ble_privacy.test.ts
 * SPDX-License-Identifier: MIT
 */
import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import {
  ROTATION_SECONDS,
  windowFor,
  serviceUuid,
  resolvableAddress,
  resolveAddress,
} from "../src/security/BlePrivacy.js";

interface UuidVector {
  window: number;
  uuid: string;
}
interface RpaVector {
  window: number;
  rpa: string;
}
interface Corpus {
  rotation_seconds: number;
  rotation_key: string;
  irk: string;
  wrong_irk: string;
  uuid_vectors: UuidVector[];
  rpa_vectors: RpaVector[];
}

function hexToBytes(s: string): Uint8Array {
  const out = new Uint8Array(s.length / 2);
  for (let i = 0; i < out.length; i++) {
    out[i] = parseInt(s.substring(i * 2, i * 2 + 2), 16);
  }
  return out;
}

function toHex(b: Uint8Array): string {
  return Buffer.from(b).toString("hex");
}

/** Walk up from this file to the repo root and load fixtures/bleprivacy/vectors.json. */
function loadCorpus(): Corpus {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (;;) {
    const candidate = join(dir, "fixtures", "bleprivacy", "vectors.json");
    if (existsSync(candidate)) {
      return JSON.parse(readFileSync(candidate, "utf8")) as Corpus;
    }
    const parent = dirname(dir);
    if (parent === dir) {
      throw new Error(
        "fixtures/bleprivacy/vectors.json not found walking up from " +
          dirname(fileURLToPath(import.meta.url)),
      );
    }
    dir = parent;
  }
}

// ── Shared vectors — the cross-language parity gate ───────────────────────────

describe("BLE privacy — shared parity vectors", () => {
  const corpus = loadCorpus();
  const rotationKey = hexToBytes(corpus.rotation_key);
  const irk = hexToBytes(corpus.irk);
  const wrongIrk = hexToBytes(corpus.wrong_irk);

  assert.equal(
    corpus.uuid_vectors.length,
    4,
    "expected 4 uuid parity vectors",
  );
  assert.equal(corpus.rpa_vectors.length, 4, "expected 4 rpa parity vectors");

  for (const v of corpus.uuid_vectors) {
    it(`serviceUuid(window=${v.window}) == ${v.uuid}`, () => {
      assert.equal(serviceUuid(rotationKey, v.window), v.uuid);
    });
  }

  for (const v of corpus.rpa_vectors) {
    it(`resolvableAddress(window=${v.window}) == ${v.rpa}`, () => {
      assert.equal(toHex(resolvableAddress(irk, v.window)), v.rpa);
    });

    it(`resolveAddress recognises the RPA for window=${v.window}`, () => {
      const rpa = hexToBytes(v.rpa);
      assert.equal(resolveAddress(irk, rpa), true);
    });

    it(`resolveAddress rejects the wrong IRK for window=${v.window}`, () => {
      const rpa = hexToBytes(v.rpa);
      assert.equal(resolveAddress(wrongIrk, rpa), false);
    });
  }
});

// ── Rotation window arithmetic ────────────────────────────────────────────────

describe("BLE privacy — rotation window", () => {
  const corpus = loadCorpus();

  it("ROTATION_SECONDS matches the fixture", () => {
    assert.equal(ROTATION_SECONDS, corpus.rotation_seconds);
    assert.equal(ROTATION_SECONDS, 900);
  });

  it("windowFor is the floor of unixSeconds / 900", () => {
    assert.equal(windowFor(0), 0);
    assert.equal(windowFor(899), 0);
    assert.equal(windowFor(900), 1);
    assert.equal(windowFor(1799), 1);
    assert.equal(windowFor(1800), 2);
  });
});

// ── Reject-paths — a malformed IRK must not resolve or generate ───────────────

describe("BLE privacy — reject-paths", () => {
  it("resolvableAddress rejects a 15-byte IRK", () => {
    assert.throws(() => resolvableAddress(new Uint8Array(15), 0), /16 bytes/);
  });

  it("resolveAddress returns false for a 15-byte IRK", () => {
    const rpa = hexToBytes("be0b5b46b0c5");
    assert.equal(resolveAddress(new Uint8Array(15), rpa), false);
  });

  it("resolveAddress returns false for a 5-byte RPA", () => {
    assert.equal(resolveAddress(new Uint8Array(16), new Uint8Array(5)), false);
  });
});
