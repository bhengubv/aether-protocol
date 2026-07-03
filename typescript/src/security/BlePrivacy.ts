// SPDX-License-Identifier: MIT

/**
 * Bluetooth-LE tracking protection: a rotating Service UUID and IRK-based
 * Resolvable Private Addresses (RPA), so a mesh node is discoverable by its
 * peers without exposing a stable, trackable Bluetooth fingerprint on the air.
 *
 *   - The Service UUID rotates every 15 minutes, HMAC-SHA256-derived from a
 *     shared rotation key and the current time window. Every node in the same
 *     window derives the same UUID, so peers still find each other — but a
 *     passive scanner sees an identifier that changes and cannot be linked over
 *     time.
 *   - The node's stable id is removed from the advertisement; a peer that holds
 *     the node's 128-bit Identity Resolving Key (IRK) resolves its rotating
 *     6-byte RPA instead (the BLE "ah" function).
 *
 * The window-based operations are deterministic and byte-identical across every
 * AetherNet SDK (verified against fixtures/bleprivacy/vectors.json). The time
 * window is encoded as a little-endian int64.
 */

import { createHmac, createCipheriv } from "crypto";

/** Rotation period in seconds (15 minutes). */
export const ROTATION_SECONDS = 900;

/** The rotation window index for a Unix-seconds timestamp. */
export function windowFor(unixSeconds: number): number {
  return Math.floor(unixSeconds / ROTATION_SECONDS);
}

/**
 * The rotating BLE Service UUID for a rotation key and time window. Every node
 * sharing the rotation key derives the same UUID within the window, enabling
 * mutual discovery with no static identifier on the air.
 */
export function serviceUuid(
  rotationKey: Uint8Array,
  window: number | bigint,
): string {
  const mac = hmacSha256(rotationKey, windowBytes(window));
  return formatUuid(mac.subarray(0, 16));
}

/**
 * A 6-byte Resolvable Private Address for a 16-byte IRK and time window:
 * `hash(3) || prand(3)`, where prand is HMAC-derived (with the RPA address-type
 * bits set) and hash = AES-128(IRK, prand-block). Rotates every window; only a
 * peer holding the IRK can link successive addresses.
 */
export function resolvableAddress(
  irk: Uint8Array,
  window: number | bigint,
): Uint8Array {
  if (irk.length !== 16) {
    throw new Error("IRK must be 16 bytes.");
  }

  const prand = hmacSha256(irk, windowBytes(window)).subarray(0, 3);
  prand[0] = (prand[0] & 0x3f) | 0x40; // RPA address-type bits (0b01)

  const hash = ah(irk, prand);

  const rpa = new Uint8Array(6);
  rpa.set(hash.subarray(0, 3), 0);
  rpa.set(prand.subarray(0, 3), 3);
  return rpa;
}

/**
 * True if `rpa` was generated from `irk` — i.e. this node recognises the peer
 * behind the rotating address.
 */
export function resolveAddress(irk: Uint8Array, rpa: Uint8Array): boolean {
  if (irk.length !== 16 || rpa.length !== 6) return false;

  const prand = rpa.subarray(3, 6);
  const hash = ah(irk, prand);
  return (
    hash[0] === rpa[0] && hash[1] === rpa[1] && hash[2] === rpa[2]
  );
}

// BLE "ah" hash: AES-128-ECB(irk, 0^13 || prand), keep the first 3 bytes.
function ah(irk: Uint8Array, prand: Uint8Array): Uint8Array {
  const block = new Uint8Array(16);
  block.set(prand.subarray(0, 3), 13);

  const cipher = createCipheriv("aes-128-ecb", irk, null);
  cipher.setAutoPadding(false);
  const ct = Buffer.concat([cipher.update(block), cipher.final()]);
  return new Uint8Array(ct.subarray(0, 3));
}

function hmacSha256(key: Uint8Array, data: Uint8Array): Uint8Array {
  return new Uint8Array(createHmac("sha256", key).update(data).digest());
}

/** The time window as a little-endian int64 (8 bytes). */
function windowBytes(window: number | bigint): Uint8Array {
  const b = Buffer.alloc(8);
  b.writeBigInt64LE(BigInt(window));
  return new Uint8Array(b);
}

function formatUuid(b: Uint8Array): string {
  const hex = Buffer.from(b).toString("hex");
  return (
    `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-` +
    `${hex.slice(16, 20)}-${hex.slice(20, 32)}`
  );
}
