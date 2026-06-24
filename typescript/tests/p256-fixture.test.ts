/**
 * Cross-language P-256 ECDSA verify fixture runner (TypeScript).
 *
 * Drives Ed25519Service.verifyWithFallback through the shared corpus at
 * `tests/cross-language/p256-fixtures.json` — DER SubjectPublicKeyInfo public key +
 * ASN.1 DER ECDSA signature + SHA-256, per PROTOCOL_SPEC.md §7.5. Every AetherNet SDK
 * drives the SAME vectors and MUST accept valid:true and reject valid:false.
 *
 * Run with: tsx --test typescript/tests/p256-fixture.test.ts
 * SPDX-License-Identifier: MIT
 */
import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { Ed25519Service } from "../src/security/Ed25519Service.js";

interface Vector {
  name: string;
  public_key_der: string;
  message: string;
  signature_der: string;
  valid: boolean;
}
interface Corpus {
  vectors: Vector[];
}

function loadCorpus(): Corpus {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (;;) {
    const candidate = join(dir, "tests", "cross-language", "p256-fixtures.json");
    if (existsSync(candidate)) {
      return JSON.parse(readFileSync(candidate, "utf8")) as Corpus;
    }
    const parent = dirname(dir);
    if (parent === dir) {
      throw new Error(
        "p256-fixtures.json not found walking up from " +
          dirname(fileURLToPath(import.meta.url)),
      );
    }
    dir = parent;
  }
}

describe("P-256 ECDSA verify — cross-language fixture", () => {
  const corpus = loadCorpus();
  assert.ok(corpus.vectors.length > 0, "no vectors");

  for (const v of corpus.vectors) {
    it(v.name, () => {
      const pub = Buffer.from(v.public_key_der, "hex");
      const msg = Buffer.from(v.message, "hex");
      const sig = Buffer.from(v.signature_der, "hex");
      // A >32-byte key forces the P-256 branch; a regression to the old
      // "return false" stub would reject the valid vector and fail here.
      assert.ok(pub.length > 32, "P-256 SPKI key must be > 32 bytes");
      assert.equal(Ed25519Service.verifyWithFallback(pub, msg, sig), v.valid);
    });
  }
});
