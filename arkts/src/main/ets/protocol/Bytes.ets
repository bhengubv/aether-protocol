/*
 * Byte / hex / UTF-8 utilities used by the canonical-byte builders. ArkTS
 * (HarmonyOS) port. Implemented without `Buffer` (which does not exist in ArkTS)
 * and without relying on the global `TextEncoder` so the same source compiles
 * under the HarmonyOS ArkTS toolchain and runs unmodified under Node for the
 * parity harness. Little-endian integer encoding uses the standard `DataView`,
 * which is available in both runtimes.
 *
 * SPDX-License-Identifier: MIT
 */

/**
 * Encodes a string to its UTF-8 bytes. A hand-rolled encoder is used (rather than
 * `TextEncoder`) so the function is identical across ArkTS and Node and never
 * depends on a host-provided global. Surrogate pairs are handled; lone surrogates
 * are emitted as U+FFFD, matching standard UTF-8 encoders.
 */
export function utf8Encode(text: string): Uint8Array {
  const out: number[] = [];
  for (let i = 0; i < text.length; i++) {
    let code: number = text.charCodeAt(i);

    // High surrogate followed by a low surrogate → combine into a code point.
    if (code >= 0xd800 && code <= 0xdbff && i + 1 < text.length) {
      const next: number = text.charCodeAt(i + 1);
      if (next >= 0xdc00 && next <= 0xdfff) {
        code = 0x10000 + ((code - 0xd800) << 10) + (next - 0xdc00);
        i++;
      } else {
        code = 0xfffd; // unpaired high surrogate
      }
    } else if (code >= 0xd800 && code <= 0xdfff) {
      code = 0xfffd; // unpaired (high without follower, or a low) surrogate
    }

    if (code < 0x80) {
      out.push(code);
    } else if (code < 0x800) {
      out.push(0xc0 | (code >> 6));
      out.push(0x80 | (code & 0x3f));
    } else if (code < 0x10000) {
      out.push(0xe0 | (code >> 12));
      out.push(0x80 | ((code >> 6) & 0x3f));
      out.push(0x80 | (code & 0x3f));
    } else {
      out.push(0xf0 | (code >> 18));
      out.push(0x80 | ((code >> 12) & 0x3f));
      out.push(0x80 | ((code >> 6) & 0x3f));
      out.push(0x80 | (code & 0x3f));
    }
  }
  return Uint8Array.from(out);
}

/**
 * Decodes UTF-8 bytes to a string. Hand-rolled to avoid depending on
 * `TextDecoder`. Malformed sequences yield U+FFFD.
 */
export function utf8Decode(bytes: Uint8Array): string {
  let result: string = '';
  let i: number = 0;
  while (i < bytes.length) {
    const b0: number = bytes[i];
    if (b0 < 0x80) {
      result += String.fromCharCode(b0);
      i += 1;
    } else if (b0 >= 0xc0 && b0 < 0xe0 && i + 1 < bytes.length) {
      const cp: number = ((b0 & 0x1f) << 6) | (bytes[i + 1] & 0x3f);
      result += String.fromCharCode(cp);
      i += 2;
    } else if (b0 >= 0xe0 && b0 < 0xf0 && i + 2 < bytes.length) {
      const cp: number =
        ((b0 & 0x0f) << 12) | ((bytes[i + 1] & 0x3f) << 6) | (bytes[i + 2] & 0x3f);
      result += String.fromCharCode(cp);
      i += 3;
    } else if (b0 >= 0xf0 && i + 3 < bytes.length) {
      const cp: number =
        ((b0 & 0x07) << 18) |
        ((bytes[i + 1] & 0x3f) << 12) |
        ((bytes[i + 2] & 0x3f) << 6) |
        (bytes[i + 3] & 0x3f);
      const u: number = cp - 0x10000;
      result += String.fromCharCode(0xd800 + (u >> 10));
      result += String.fromCharCode(0xdc00 + (u & 0x3ff));
      i += 4;
    } else {
      result += String.fromCharCode(0xfffd);
      i += 1;
    }
  }
  return result;
}

/** Lowercase hex string for `bytes` (no separators). */
export function toHex(bytes: Uint8Array): string {
  let s: string = '';
  for (let i = 0; i < bytes.length; i++) {
    const h: string = bytes[i].toString(16);
    s += h.length === 1 ? '0' + h : h;
  }
  return s;
}

/** Parses an even-length lowercase/uppercase hex string into bytes. */
export function fromHex(hex: string): Uint8Array {
  if ((hex.length & 1) !== 0) {
    throw new Error(`fromHex: odd-length hex string (len=${hex.length})`);
  }
  const out = new Uint8Array(hex.length / 2);
  for (let i = 0; i < out.length; i++) {
    const byte: number = parseInt(hex.substring(i * 2, i * 2 + 2), 16);
    if (Number.isNaN(byte)) {
      throw new Error(`fromHex: invalid hex at byte ${i}`);
    }
    out[i] = byte;
  }
  return out;
}

/**
 * Base64-encodes `bytes` (standard alphabet, '=' padding). Used for the JSON wire
 * form of signatures; the canonical signed bytes never go through base64.
 */
export function toBase64(bytes: Uint8Array): string {
  const ALPHABET: string = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
  let out: string = '';
  let i: number = 0;
  for (; i + 2 < bytes.length; i += 3) {
    const n: number = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
    out += ALPHABET.charAt((n >> 18) & 0x3f);
    out += ALPHABET.charAt((n >> 12) & 0x3f);
    out += ALPHABET.charAt((n >> 6) & 0x3f);
    out += ALPHABET.charAt(n & 0x3f);
  }
  const rem: number = bytes.length - i;
  if (rem === 1) {
    const n: number = bytes[i] << 16;
    out += ALPHABET.charAt((n >> 18) & 0x3f);
    out += ALPHABET.charAt((n >> 12) & 0x3f);
    out += '==';
  } else if (rem === 2) {
    const n: number = (bytes[i] << 16) | (bytes[i + 1] << 8);
    out += ALPHABET.charAt((n >> 18) & 0x3f);
    out += ALPHABET.charAt((n >> 12) & 0x3f);
    out += ALPHABET.charAt((n >> 6) & 0x3f);
    out += '=';
  }
  return out;
}

/** Base64-decodes a standard-alphabet string into bytes. */
export function fromBase64(b64: string): Uint8Array {
  const ALPHABET: string = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
  const lookup = new Int16Array(128).fill(-1);
  for (let i = 0; i < ALPHABET.length; i++) {
    lookup[ALPHABET.charCodeAt(i)] = i;
  }

  let clean: string = '';
  for (let i = 0; i < b64.length; i++) {
    const c: string = b64.charAt(i);
    if (c !== '=' && c !== '\n' && c !== '\r' && c !== ' ') {
      clean += c;
    }
  }

  const fullGroups: number = Math.floor(clean.length / 4);
  const remainder: number = clean.length - fullGroups * 4;
  let outLen: number = fullGroups * 3;
  if (remainder === 2) {
    outLen += 1;
  } else if (remainder === 3) {
    outLen += 2;
  }

  const out = new Uint8Array(outLen);
  let oi: number = 0;
  let si: number = 0;
  for (let g = 0; g < fullGroups; g++) {
    const a: number = lookup[clean.charCodeAt(si)];
    const b: number = lookup[clean.charCodeAt(si + 1)];
    const c: number = lookup[clean.charCodeAt(si + 2)];
    const d: number = lookup[clean.charCodeAt(si + 3)];
    si += 4;
    const n: number = (a << 18) | (b << 12) | (c << 6) | d;
    out[oi++] = (n >> 16) & 0xff;
    out[oi++] = (n >> 8) & 0xff;
    out[oi++] = n & 0xff;
  }
  if (remainder === 2) {
    const a: number = lookup[clean.charCodeAt(si)];
    const b: number = lookup[clean.charCodeAt(si + 1)];
    const n: number = (a << 18) | (b << 12);
    out[oi++] = (n >> 16) & 0xff;
  } else if (remainder === 3) {
    const a: number = lookup[clean.charCodeAt(si)];
    const b: number = lookup[clean.charCodeAt(si + 1)];
    const c: number = lookup[clean.charCodeAt(si + 2)];
    const n: number = (a << 18) | (b << 12) | (c << 6);
    out[oi++] = (n >> 16) & 0xff;
    out[oi++] = (n >> 8) & 0xff;
  }
  return out;
}

/** Byte-for-byte equality of two arrays. */
export function bytesEqual(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) {
    return false;
  }
  for (let i = 0; i < a.length; i++) {
    if (a[i] !== b[i]) {
      return false;
    }
  }
  return true;
}
