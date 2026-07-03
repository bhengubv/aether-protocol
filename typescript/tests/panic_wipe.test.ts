/**
 * Panic-wipe parity tests (TypeScript).
 *
 * Drives the duress-defence core through the shared vectors at
 * `fixtures/panicwipe/vectors.json`: for every duress_pin_hash vector,
 * toHex(duressPinHash(pin)) == sha256 AND verifyDuressPin(pin, hash) is true
 * while a mutated PIN is false — byte-for-byte. IDENTITY_KEY_NAMES, MAX_PRE_KEYS
 * and the pre-key name patterns must equal the fixture. secureErase zeroes a
 * buffer and a wrong-length hash never verifies. Every AetherNet language SDK
 * drives the SAME vectors and MUST reproduce them.
 *
 * Run with: tsx --test typescript/tests/panic_wipe.test.ts
 * SPDX-License-Identifier: MIT
 */
import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import {
  MAX_PRE_KEYS,
  IDENTITY_KEY_NAMES,
  preKeyName,
  signedPreKeyName,
  duressPinHash,
  verifyDuressPin,
  secureErase,
} from "../src/security/PanicWipe.js";

interface PinVector {
  pin: string;
  sha256: string;
}
interface NameVector {
  index: number;
  expected: string;
}
interface Corpus {
  max_prekeys: number;
  identity_key_names: string[];
  prekey_name: NameVector;
  signed_prekey_name: NameVector;
  duress_pin_hashes: PinVector[];
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

/** Walk up from this file to the repo root and load fixtures/panicwipe/vectors.json. */
function loadCorpus(): Corpus {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (;;) {
    const candidate = join(dir, "fixtures", "panicwipe", "vectors.json");
    if (existsSync(candidate)) {
      return JSON.parse(readFileSync(candidate, "utf8")) as Corpus;
    }
    const parent = dirname(dir);
    if (parent === dir) {
      throw new Error(
        "fixtures/panicwipe/vectors.json not found walking up from " +
          dirname(fileURLToPath(import.meta.url)),
      );
    }
    dir = parent;
  }
}

// ── Shared vectors — the cross-language parity gate ───────────────────────────

describe("Panic-wipe — duress PIN parity vectors", () => {
  const corpus = loadCorpus();

  assert.equal(
    corpus.duress_pin_hashes.length,
    5,
    "expected 5 duress-pin parity vectors",
  );

  for (const v of corpus.duress_pin_hashes) {
    it(`duressPinHash(${JSON.stringify(v.pin)}) == ${v.sha256}`, () => {
      assert.equal(toHex(duressPinHash(v.pin)), v.sha256);
    });

    it(`verifyDuressPin recognises the stored hash for ${JSON.stringify(v.pin)}`, () => {
      const hash = hexToBytes(v.sha256);
      assert.equal(verifyDuressPin(v.pin, hash), true);
    });

    it(`verifyDuressPin rejects a mutated PIN for ${JSON.stringify(v.pin)}`, () => {
      const hash = hexToBytes(v.sha256);
      assert.equal(verifyDuressPin(v.pin + "x", hash), false);
    });
  }
});

// ── Identity manifest — the canonical set a wipe destroys ─────────────────────

describe("Panic-wipe — identity manifest", () => {
  const corpus = loadCorpus();

  it("IDENTITY_KEY_NAMES matches the fixture (order-sensitive)", () => {
    assert.deepEqual([...IDENTITY_KEY_NAMES], corpus.identity_key_names);
  });

  it("MAX_PRE_KEYS matches the fixture", () => {
    assert.equal(MAX_PRE_KEYS, corpus.max_prekeys);
    assert.equal(MAX_PRE_KEYS, 200);
  });

  it("preKeyName matches the fixture pattern", () => {
    assert.equal(
      preKeyName(corpus.prekey_name.index),
      corpus.prekey_name.expected,
    );
  });

  it("signedPreKeyName matches the fixture pattern", () => {
    assert.equal(
      signedPreKeyName(corpus.signed_prekey_name.index),
      corpus.signed_prekey_name.expected,
    );
  });
});

// ── Secure erase + reject-paths ───────────────────────────────────────────────

describe("Panic-wipe — secure erase & reject-paths", () => {
  it("secureErase zeroes a buffer", () => {
    const buf = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]);
    secureErase(buf);
    assert.ok(
      buf.every((b) => b === 0),
      "buffer should be all zero after secureErase",
    );
  });

  it("verifyDuressPin returns false for a 16-byte hash", () => {
    assert.equal(verifyDuressPin("0000", new Uint8Array(16)), false);
  });
});
