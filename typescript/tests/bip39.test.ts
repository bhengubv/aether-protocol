/**
 * BIP-39 recovery-phrase parity + AetherNet identity backup tests (TypeScript).
 *
 * Drives the codec through the official Trezor vectors at
 * `fixtures/bip39/vectors.json` (passphrase "TREZOR"): for all 24 vectors,
 * entropyToMnemonic == mnemonic, mnemonicToEntropy == entropy, and
 * mnemonicToSeed(mnemonic,"TREZOR") == seed, byte-for-byte. Plus the AetherNet
 * identity round-trip and BIP-39 checksum reject-paths. Every AetherNet SDK
 * drives the SAME vectors and MUST reproduce all three columns.
 *
 * Run with: tsx --test typescript/tests/bip39.test.ts
 * SPDX-License-Identifier: MIT
 */
import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import {
  entropyToMnemonic,
  mnemonicToEntropy,
  mnemonicToSeed,
  isValidMnemonic,
  toRecoveryPhrase,
  fromRecoveryPhrase,
} from "../src/security/Bip39.js";
import { Ed25519Service } from "../src/security/Ed25519Service.js";

interface Vector {
  entropy: string;
  mnemonic: string;
  seed: string;
}
interface Corpus {
  passphrase: string;
  vectors: Vector[];
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

/** Walk up from this file to the repo root and load fixtures/bip39/vectors.json. */
function loadCorpus(): Corpus {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (;;) {
    const candidate = join(dir, "fixtures", "bip39", "vectors.json");
    if (existsSync(candidate)) {
      return JSON.parse(readFileSync(candidate, "utf8")) as Corpus;
    }
    const parent = dirname(dir);
    if (parent === dir) {
      throw new Error(
        "fixtures/bip39/vectors.json not found walking up from " +
          dirname(fileURLToPath(import.meta.url)),
      );
    }
    dir = parent;
  }
}

// ── Official Trezor vectors — the cross-language parity gate ──────────────────

describe("BIP-39 — official Trezor vectors (passphrase TREZOR)", () => {
  const corpus = loadCorpus();
  assert.equal(corpus.passphrase, "TREZOR");
  assert.equal(corpus.vectors.length, 24, "expected 24 official vectors");

  for (const v of corpus.vectors) {
    it(`entropy ${v.entropy.slice(0, 12)}… → mnemonic`, () => {
      assert.equal(entropyToMnemonic(hexToBytes(v.entropy)), v.mnemonic);
    });

    it(`mnemonic → entropy ${v.entropy.slice(0, 12)}…`, () => {
      assert.equal(toHex(mnemonicToEntropy(v.mnemonic)), v.entropy);
    });

    it(`mnemonic → seed ${v.seed.slice(0, 12)}…`, () => {
      assert.equal(toHex(mnemonicToSeed(v.mnemonic, corpus.passphrase)), v.seed);
    });
  }
});

// ── AetherNet identity — fixed 24-word vector ────────────────────────────────

describe("AetherNet identity — recovery phrase (fixed vector)", () => {
  const entropyHex =
    "f585c11aec520db57dd353c69554b21a89b20fb0650966fa0a9d6f74fd989d8f";
  const expectedPhrase =
    "void come effort suffer camp survey warrior heavy shoot primary clutch " +
    "crush open amazing screen patrol group space point ten exist slush " +
    "involve unfold";

  it("toRecoveryPhrase produces the expected 24 words", () => {
    assert.equal(toRecoveryPhrase(hexToBytes(entropyHex)), expectedPhrase);
  });

  it("fromRecoveryPhrase recovers the exact private seed", () => {
    const { privateKey } = fromRecoveryPhrase(expectedPhrase);
    assert.equal(toHex(privateKey), entropyHex);
  });

  it("recovered public key matches Ed25519Service key derivation", () => {
    const { publicKey } = fromRecoveryPhrase(expectedPhrase);
    // Independently derive the public key from the same seed via the SDK's
    // signing path (sign+verify only accepts the matching public key).
    const seed = hexToBytes(entropyHex);
    const data = new TextEncoder().encode("identity check");
    const sig = Ed25519Service.sign(seed, data);
    assert.equal(Ed25519Service.verify(publicKey, data, sig), true);
    assert.equal(publicKey.length, 32);
  });
});

// ── AetherNet identity — random seed backup/restore round-trip ───────────────

describe("AetherNet identity — random seed round-trip", () => {
  it("phrase → restore yields equal private + public, and the key signs/verifies", () => {
    const { privateKey, publicKey } = Ed25519Service.generateKeyPair();

    const phrase = toRecoveryPhrase(privateKey);
    assert.equal(phrase.split(" ").length, 24, "24-word phrase expected");
    assert.ok(isValidMnemonic(phrase), "generated phrase must be valid");

    const restored = fromRecoveryPhrase(phrase);
    assert.deepEqual(
      Array.from(restored.privateKey),
      Array.from(privateKey),
      "restored private key must equal original",
    );
    assert.deepEqual(
      Array.from(restored.publicKey),
      Array.from(publicKey),
      "restored public key must equal original",
    );

    // The restored key actually works end-to-end.
    const data = new TextEncoder().encode("round-trip signature");
    const sig = Ed25519Service.sign(restored.privateKey, data);
    assert.equal(Ed25519Service.verify(restored.publicKey, data, sig), true);
  });
});

// ── Reject-paths — a mistyped phrase must throw, never silently wrong ─────────

describe("BIP-39 — reject-paths", () => {
  it("rejects 24×'abandon' (bad checksum)", () => {
    const bad = new Array(24).fill("abandon").join(" ");
    assert.throws(() => mnemonicToEntropy(bad), /checksum/i);
    assert.equal(isValidMnemonic(bad), false);
    assert.throws(() => fromRecoveryPhrase(bad));
  });

  it("rejects an unknown word", () => {
    // Valid 24-word phrase with one word swapped for a non-wordlist token.
    const good =
      "void come effort suffer camp survey warrior heavy shoot primary clutch " +
      "crush open amazing screen patrol group space point ten exist slush " +
      "involve unfold";
    const words = good.split(" ");
    words[0] = "notaword";
    const bad = words.join(" ");
    assert.throws(() => mnemonicToEntropy(bad), /unknown/i);
    assert.equal(isValidMnemonic(bad), false);
  });

  it("rejects a 3-word phrase (wrong word count)", () => {
    const bad = "abandon ability able";
    assert.throws(() => mnemonicToEntropy(bad), /12, 15, 18, 21, or 24/);
    assert.equal(isValidMnemonic(bad), false);
  });

  it("rejects a wrong-length seed passed to toRecoveryPhrase", () => {
    assert.throws(() => toRecoveryPhrase(new Uint8Array(16)), /32 bytes/);
  });

  it("rejects a valid-but-short (12-word) phrase as an identity seed", () => {
    // 12 words is a well-formed BIP-39 phrase but only 128-bit entropy, not a
    // 256-bit identity seed — fromRecoveryPhrase must reject it.
    const twelve =
      "abandon abandon abandon abandon abandon abandon abandon abandon " +
      "abandon abandon abandon about";
    assert.ok(isValidMnemonic(twelve), "12-word phrase is itself valid BIP-39");
    assert.throws(() => fromRecoveryPhrase(twelve), /24 words/);
  });
});
