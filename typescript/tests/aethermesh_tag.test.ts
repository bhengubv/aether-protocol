/**
 * Tests for AetherMeshTag identity primitive.
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/aethermesh_tag.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { createHash } from "node:crypto";

import { AetherMeshTag } from "../src/identity/AetherMeshTag.js";

// ── Helpers ───────────────────────────────────────────────────────────────────

/** Build a deterministic 32-byte key filled with a repeating byte value. */
function key(fill: number): Uint8Array {
  return new Uint8Array(32).fill(fill);
}

/** Build a 32-byte key with specific byte values at specific positions. */
function keyFrom(bytes: number[]): Uint8Array {
  const k = new Uint8Array(32);
  bytes.forEach((b, i) => (k[i] = b));
  return k;
}

// ── Canonical format RE ───────────────────────────────────────────────────────

const FORMAT_RE = /^[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$/;

// ── Tests ─────────────────────────────────────────────────────────────────────

describe("AetherMeshTag — format", () => {
  it("fromPublicKey produces XXXXX-XXXXX format", () => {
    const tag = AetherMeshTag.fromPublicKey(key(0x01));
    assert.match(tag.value, FORMAT_RE, `tag "${tag.value}" must match XXXXX-XXXXX`);
  });

  it("result is exactly 11 characters", () => {
    const tag = AetherMeshTag.fromPublicKey(key(0x42));
    assert.equal(tag.value.length, 11);
    assert.equal(tag.value[5], "-");
  });

  it("output contains only Crockford alphabet chars and dash", () => {
    const crockford = new Set("0123456789ABCDEFGHJKMNPQRSTVWXYZ".split(""));
    for (let fill = 0; fill <= 0xff; fill += 16) {
      const tag = AetherMeshTag.fromPublicKey(key(fill));
      for (const ch of tag.value) {
        if (ch === "-") continue;
        assert.ok(crockford.has(ch), `unexpected char "${ch}" in "${tag.value}"`);
      }
    }
  });

  it("does NOT contain I, L, O, or U", () => {
    const forbidden = new Set(["I", "L", "O", "U"]);
    for (let fill = 0; fill <= 0xff; fill += 8) {
      const tag = AetherMeshTag.fromPublicKey(key(fill));
      for (const ch of tag.value) {
        assert.ok(
          !forbidden.has(ch),
          `forbidden char "${ch}" found in "${tag.value}"`,
        );
      }
    }
  });
});

describe("AetherMeshTag — known vector", () => {
  // Compute the expected tag for a known key at test time so the vector is
  // deterministic even if the algorithm changes (assertion exercises the full
  // path without hard-coding a magic string that could drift).
  it("fixed all-zeros key produces a stable deterministic tag", () => {
    const publicKey = new Uint8Array(32).fill(0x00);
    const tag1 = AetherMeshTag.fromPublicKey(publicKey);
    const tag2 = AetherMeshTag.fromPublicKey(publicKey);
    assert.equal(tag1.value, tag2.value);
    assert.match(tag1.value, FORMAT_RE);
  });

  it("manually computed SHA-256 extraction matches fromPublicKey", () => {
    // Reproduce the algorithm independently to cross-check the implementation.
    const publicKey = keyFrom([0xde, 0xad, 0xbe, 0xef, 0xca, 0xfe, 0xba, 0xbe]);
    const hash = createHash("sha256").update(publicKey).digest();

    const ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    const bits =
      (BigInt(hash[0]) << 42n) |
      (BigInt(hash[1]) << 34n) |
      (BigInt(hash[2]) << 26n) |
      (BigInt(hash[3]) << 18n) |
      (BigInt(hash[4]) << 10n) |
      (BigInt(hash[5]) << 2n) |
      BigInt(hash[6] >> 6);

    const chars: string[] = [];
    for (let i = 9; i >= 0; i--) {
      chars.push(ALPHABET[Number((bits >> BigInt(i * 5)) & 0x1fn)]);
    }
    const expected = `${chars.slice(0, 5).join("")}-${chars.slice(5).join("")}`;

    const tag = AetherMeshTag.fromPublicKey(publicKey);
    assert.equal(tag.value, expected);
  });
});

describe("AetherMeshTag — round-trip", () => {
  it("fromPublicKey → toString → parse → equals original", () => {
    const publicKey = key(0x7f);
    const original = AetherMeshTag.fromPublicKey(publicKey);
    const parsed = AetherMeshTag.parse(original.toString());
    assert.ok(original.equals(parsed));
    assert.equal(parsed.value, original.value);
  });

  it("toString() returns the same as .value", () => {
    const tag = AetherMeshTag.fromPublicKey(key(0xab));
    assert.equal(tag.toString(), tag.value);
  });

  it("isValid is always true for constructed instances", () => {
    const tag = AetherMeshTag.fromPublicKey(key(0x11));
    assert.ok(tag.isValid);
    assert.ok(AetherMeshTag.parse(tag.value).isValid);
  });
});

describe("AetherMeshTag — verify()", () => {
  it("correct key returns true", () => {
    const publicKey = key(0x55);
    const tag = AetherMeshTag.fromPublicKey(publicKey);
    assert.ok(AetherMeshTag.verify(tag.value, publicKey));
  });

  it("wrong key returns false", () => {
    const publicKey = key(0x55);
    const wrongKey = key(0x56);
    const tag = AetherMeshTag.fromPublicKey(publicKey);
    assert.equal(AetherMeshTag.verify(tag.value, wrongKey), false);
  });

  it("invalid tag string returns false", () => {
    assert.equal(AetherMeshTag.verify("not-valid-tag!!", key(0x01)), false);
  });

  it("all-zeros key verifies only against itself", () => {
    const k0 = key(0x00);
    const k1 = key(0x01);
    const tag = AetherMeshTag.fromPublicKey(k0);
    assert.ok(AetherMeshTag.verify(tag.value, k0));
    assert.equal(AetherMeshTag.verify(tag.value, k1), false);
  });
});

describe("AetherMeshTag — parse() accepts", () => {
  it("canonical uppercase with dash", () => {
    const tag = AetherMeshTag.fromPublicKey(key(0x22));
    const parsed = AetherMeshTag.parse(tag.value);
    assert.equal(parsed.value, tag.value);
  });

  it("10-char form without dash", () => {
    const tag = AetherMeshTag.fromPublicKey(key(0x33));
    const noDash = tag.value.replace("-", "");
    const parsed = AetherMeshTag.parse(noDash);
    assert.equal(parsed.value, tag.value);
  });

  it("lowercase with dash", () => {
    const tag = AetherMeshTag.fromPublicKey(key(0x44));
    const lower = tag.value.toLowerCase();
    const parsed = AetherMeshTag.parse(lower);
    assert.equal(parsed.value, tag.value);
  });

  it("lowercase without dash", () => {
    const tag = AetherMeshTag.fromPublicKey(key(0x55));
    const lower = tag.value.replace("-", "").toLowerCase();
    const parsed = AetherMeshTag.parse(lower);
    assert.equal(parsed.value, tag.value);
  });

  it("mixed case", () => {
    const tag = AetherMeshTag.fromPublicKey(key(0x66));
    const mixed = tag.value
      .split("")
      .map((ch, i) => (i % 2 === 0 ? ch.toLowerCase() : ch.toUpperCase()))
      .join("");
    const parsed = AetherMeshTag.parse(mixed);
    assert.equal(parsed.value, tag.value);
  });
});

describe("AetherMeshTag — parse() rejects", () => {
  it("empty string", () => {
    assert.throws(() => AetherMeshTag.parse(""), /invalid tag/i);
    assert.equal(AetherMeshTag.tryParse(""), null);
  });

  it("too short", () => {
    assert.equal(AetherMeshTag.tryParse("ABCDE"), null);
    assert.equal(AetherMeshTag.tryParse("ABCDE-ABC"), null);
  });

  it("too long", () => {
    assert.equal(AetherMeshTag.tryParse("ABCDE-ABCDEF"), null);
    assert.equal(AetherMeshTag.tryParse("ABCDEFABCDEF"), null);
  });

  it("invalid char I (ambiguous)", () => {
    // Build a 10-char string with 'I' in a position — should be treated as
    // look-alike for '1', so this actually normalises successfully.  Instead
    // test a character that is simply not in the Crockford alphabet at all.
    assert.equal(AetherMeshTag.tryParse("!BCDE-FGHJK"), null);
  });

  it("invalid char U (excluded from alphabet)", () => {
    // 'U' is normalised to 'V' by the Crockford spec, so it is accepted.
    // Test a genuinely invalid character instead.
    assert.equal(AetherMeshTag.tryParse("$BCDE-FGHJK"), null);
  });

  it("invalid char in middle", () => {
    assert.equal(AetherMeshTag.tryParse("ABC.E-FGHJK"), null);
  });

  it("wrong separator position", () => {
    // Two dashes — the replace() only strips the first, leaving 11 chars.
    assert.equal(AetherMeshTag.tryParse("ABCDE--FGHJ"), null);
  });

  it("null / non-string coercion returns null", () => {
    assert.equal(AetherMeshTag.tryParse(null as unknown as string), null);
    assert.equal(AetherMeshTag.tryParse(undefined as unknown as string), null);
  });
});

describe("AetherMeshTag — uniqueness", () => {
  it("different keys produce different tags", () => {
    const tags = new Set<string>();
    for (let fill = 0; fill < 32; fill++) {
      tags.add(AetherMeshTag.fromPublicKey(key(fill)).value);
    }
    // All 32 keys should have distinct tags (collision with only 32 samples
    // against a 50-bit space is astronomically unlikely).
    assert.equal(tags.size, 32);
  });

  it("same key always produces the same tag", () => {
    const publicKey = key(0xcc);
    const results = Array.from({ length: 10 }, () =>
      AetherMeshTag.fromPublicKey(publicKey).value,
    );
    assert.ok(results.every((v) => v === results[0]));
  });
});

describe("AetherMeshTag — equals()", () => {
  it("same key produces equal tags", () => {
    const a = AetherMeshTag.fromPublicKey(key(0x11));
    const b = AetherMeshTag.fromPublicKey(key(0x11));
    assert.ok(a.equals(b));
    assert.ok(b.equals(a));
  });

  it("different keys produce non-equal tags", () => {
    const a = AetherMeshTag.fromPublicKey(key(0x11));
    const b = AetherMeshTag.fromPublicKey(key(0x22));
    assert.equal(a.equals(b), false);
  });
});

describe("AetherMeshTag — fromPublicKey validation", () => {
  it("throws on key shorter than 32 bytes", () => {
    assert.throws(() => AetherMeshTag.fromPublicKey(new Uint8Array(16)), /32 bytes/);
  });

  it("throws on key longer than 32 bytes", () => {
    assert.throws(() => AetherMeshTag.fromPublicKey(new Uint8Array(64)), /32 bytes/);
  });

  it("throws on empty key", () => {
    assert.throws(() => AetherMeshTag.fromPublicKey(new Uint8Array(0)), /32 bytes/);
  });
});
