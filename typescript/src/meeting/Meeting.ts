// SPDX-License-Identifier: MIT

/**
 * Rendezvous derivation: two phones agreeing where to meet from their tags alone, before either
 * radio has done anything. Port of the C# reference `AetherNet.Rendezvous`
 * (src/AetherNet.Core/Rendezvous/). Verified byte-for-byte against
 * `fixtures/meeting/meeting_basic.json`.
 */

import { hkdf } from "@noble/hashes/hkdf";
import { sha256 } from "@noble/hashes/sha256";

/** Ties this derivation to this purpose, so the same tags used elsewhere yield nothing here. */
const INFO = new TextEncoder().encode("aether-meeting-v1");

/** Crockford's alphabet: no I, L, O or U, so it cannot be misread down a phone line. */
const ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

/** How many characters a rendezvous carries — longer than the widest radio needs. */
export const LENGTH = 25;

/**
 * Whether `myTag` hosts the group it would share with `theirTag`: order the two tags and the
 * ordinally-lower one hosts. A missing tag hosts nothing.
 */
export function hostsTheGroup(myTag: string, theirTag: string): boolean {
  if (!myTag || !theirTag) return false;
  return myTag < theirTag;
}

/** A meeting point derived from two tags: who you are meeting, where, and which of you opens. */
export class Meeting {
  private constructor(
    readonly peerTag: string,
    readonly rendezvous: string,
    readonly iStart: boolean,
  ) {}

  /**
   * Works out where two phones meet, from their tags alone. Returns `null` when either tag is
   * missing or blank, or they are the same phone (tags are case-insensitive, so two case-variants
   * are one identity and do not meet).
   */
  static with(myTag: string, theirTag: string): Meeting | null {
    if (!myTag || !myTag.trim() || !theirTag || !theirTag.trim()) return null;
    if (myTag.toUpperCase() === theirTag.toUpperCase()) return null;

    // Ordered, so both phones feed the derivation the same bytes in the same order.
    const [first, second] = myTag < theirTag ? [myTag, theirTag] : [theirTag, myTag];

    // undefined salt matches the C# reference's ReadOnlySpan<byte>.Empty — the same choice the erid
    // port makes; empty and absent salt are equivalent in HKDF.
    const derived = hkdf(sha256, new TextEncoder().encode(`${first}\n${second}`), undefined, INFO, 16);

    return new Meeting(theirTag, encode(derived).slice(0, LENGTH), hostsTheGroup(myTag, theirTag));
  }

  /** As much of the rendezvous as a radio can use, from the front (C# `Where`). */
  prefix(characters: number): string {
    if (characters <= 0) return "";
    return characters >= this.rendezvous.length ? this.rendezvous : this.rendezvous.slice(0, characters);
  }

  /**
   * The meeting UUID as .NET's `Guid.ToByteArray()` bytes — the raw hash with the version/variant
   * set. (.NET stores the first three groups little-endian; this is exactly those 16 bytes.)
   */
  uuidBytes(): Uint8Array {
    const h = sha256(new TextEncoder().encode(`aether-meeting-v1-uuid\n${this.rendezvous}`)).slice(0, 16);
    const b = new Uint8Array(h);
    b[7] = (b[7] & 0x0f) | 0x40; // version 4
    b[8] = (b[8] & 0x3f) | 0x80; // variant 1
    return b;
  }

  /** The meeting UUID as .NET's `Guid.ToString("D")` — the mixed-endian display of the same bytes. */
  uuidString(): string {
    const b = this.uuidBytes();
    const h = (i: number): string => b[i].toString(16).padStart(2, "0");
    return (
      `${h(3)}${h(2)}${h(1)}${h(0)}-${h(5)}${h(4)}-${h(7)}${h(6)}-` +
      `${h(8)}${h(9)}-${h(10)}${h(11)}${h(12)}${h(13)}${h(14)}${h(15)}`
    );
  }

  /** The meeting as a small number, for a radio whose address space is tiny (`bits` in 1..32). */
  address(bits: number): number {
    if (bits < 1 || bits > 32) throw new RangeError("bits must be between 1 and 32");
    const h = sha256(new TextEncoder().encode(`aether-meeting-v1-addr\n${this.rendezvous}`));
    const whole = ((h[0] << 24) | (h[1] << 16) | (h[2] << 8) | h[3]) >>> 0;
    if (bits === 32) return whole;
    return (whole & ((1 << bits) - 1)) >>> 0;
  }
}

/** Renders bytes as Crockford base32, five bits at a time — the same bit walk as the reference. */
function encode(data: Uint8Array): string {
  const total = Math.floor((data.length * 8) / 5);
  let out = "";
  let bit = 0;
  for (let i = 0; i < total; i++) {
    let value = 0;
    for (let j = 0; j < 5; j++) {
      const source = data[Math.floor(bit / 8)];
      const taken = (source >> (7 - (bit % 8))) & 1;
      value = (value << 1) | taken;
      bit++;
    }
    out += ALPHABET[value];
  }
  return out;
}
