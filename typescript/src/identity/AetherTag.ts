/**
 * AetherTag — human-readable identity address derived from an Ed25519 public key.
 *
 * Algorithm:
 *   SHA-256(publicKey) → extract first 50 bits → encode as 10 Crockford base-32 chars
 *   → format as "XXXXX-XXXXX"
 *
 * Crockford base-32 alphabet: "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
 * (standard base-32 minus I, L, O, U)
 *
 * SPDX-License-Identifier: MIT
 */

import { createHash } from "node:crypto";

// ── Crockford base-32 ─────────────────────────────────────────────────────────

const ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
const ALPHABET_SET = new Set(ALPHABET.split(""));

// Normalise lowercase and common look-alike substitutions per the Crockford spec.
function normalise(ch: string): string {
  const u = ch.toUpperCase();
  if (u === "I" || u === "1") return "1";
  if (u === "L") return "1";
  if (u === "O" || u === "0") return "0";
  if (u === "U") return "V";
  return u;
}

// ── Tag pattern ───────────────────────────────────────────────────────────────

/** Matches the canonical "XXXXX-XXXXX" format (after upper-casing). */
const TAG_RE = /^[0-9A-HJKMNP-TV-Z]{5}-[0-9A-HJKMNP-TV-Z]{5}$/;

/**
 * Strict Crockford base-32 character check (no look-alikes accepted in
 * round-trip/verify paths — those are already normalised away by parse).
 */
function isValidAlphabetChar(ch: string): boolean {
  return ALPHABET_SET.has(ch);
}

// ── Core encoding ─────────────────────────────────────────────────────────────

/**
 * Derive the 11-character tag string (including dash) from a 32-byte Ed25519
 * public key.
 *
 * Bit layout (50 bits from the SHA-256 digest):
 *   bits[49:42] = hash[0]
 *   bits[41:34] = hash[1]
 *   bits[33:26] = hash[2]
 *   bits[25:18] = hash[3]
 *   bits[17:10] = hash[4]
 *   bits[9:2]   = hash[5]
 *   bits[1:0]   = hash[6] >> 6
 */
function encode(publicKey: Uint8Array): string {
  const hash = createHash("sha256").update(publicKey).digest();

  const bits =
    (BigInt(hash[0]) << 42n) |
    (BigInt(hash[1]) << 34n) |
    (BigInt(hash[2]) << 26n) |
    (BigInt(hash[3]) << 18n) |
    (BigInt(hash[4]) << 10n) |
    (BigInt(hash[5]) << 2n) |
    BigInt(hash[6] >> 6);

  // Extract 10 × 5-bit groups (most-significant first).
  const chars: string[] = [];
  for (let i = 9; i >= 0; i--) {
    const idx = Number((bits >> BigInt(i * 5)) & 0x1fn);
    chars.push(ALPHABET[idx]);
  }

  return `${chars.slice(0, 5).join("")}-${chars.slice(5).join("")}`;
}

// ── AetherTag class ───────────────────────────────────────────────────────────

/**
 * A validated AetherTag identity address.
 *
 * The canonical form is always uppercase with a dash separator, e.g. "KXJB7-MN2P4".
 */
export class AetherTag {
  /** The canonical tag string, always uppercase with dash ("XXXXX-XXXXX"). */
  readonly value: string;

  private constructor(value: string) {
    this.value = value;
  }

  // ── Factory methods ──────────────────────────────────────────────────────────

  /**
   * Derive an AetherTag from a 32-byte Ed25519 public key.
   * @throws {Error} if publicKey is not exactly 32 bytes.
   */
  static fromPublicKey(publicKey: Uint8Array): AetherTag {
    if (publicKey.length !== 32) {
      throw new Error(
        `AetherTag: publicKey must be 32 bytes, got ${publicKey.length}`,
      );
    }
    return new AetherTag(encode(publicKey));
  }

  /**
   * Parse a tag string into an AetherTag.
   *
   * Accepts:
   *   - Canonical:  "KXJB7-MN2P4"
   *   - No dash:    "KXJB7MN2P4"
   *   - Lowercase:  "kxjb7-mn2p4"
   *   - Mixed case: "kXjB7-mN2p4"
   *
   * @throws {Error} if the string is not a valid tag.
   */
  static parse(tag: string): AetherTag {
    const result = AetherTag.tryParse(tag);
    if (result === null) {
      throw new Error(`AetherTag: invalid tag "${tag}"`);
    }
    return result;
  }

  /**
   * Try to parse a tag string, returning null if invalid.
   */
  static tryParse(tag: string): AetherTag | null {
    if (!tag || typeof tag !== "string") return null;

    // Strip optional dash separator.
    const stripped = tag.replace("-", "");

    if (stripped.length !== 10) return null;

    // Normalise each character (handles lowercase + look-alikes).
    const normalised = stripped
      .split("")
      .map(normalise)
      .join("");

    // Validate every character is in the Crockford alphabet.
    for (const ch of normalised) {
      if (!isValidAlphabetChar(ch)) return null;
    }

    const canonical = `${normalised.slice(0, 5)}-${normalised.slice(5)}`;

    if (!TAG_RE.test(canonical)) return null;

    return new AetherTag(canonical);
  }

  /**
   * Verify that a tag string matches the given public key.
   */
  static verify(tag: string, publicKey: Uint8Array): boolean {
    try {
      const parsed = AetherTag.tryParse(tag);
      if (parsed === null) return false;
      const expected = AetherTag.fromPublicKey(publicKey);
      return parsed.value === expected.value;
    } catch {
      return false;
    }
  }

  // ── Instance members ─────────────────────────────────────────────────────────

  /** Always true — construction is only possible via validated factory methods. */
  get isValid(): boolean {
    return true;
  }

  toString(): string {
    return this.value;
  }

  equals(other: AetherTag): boolean {
    return this.value === other.value;
  }
}
