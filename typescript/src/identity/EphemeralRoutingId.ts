/**
 * Ephemeral Routing Id (ERID) — a rotating, key-derived wire address designed to
 * replace the stable, phone-derived UHID on the public wire.
 *
 * ## The problem it solves
 * A node's UHID is `SHA-256(phone : deviceId : publicKey)` — stable for the life of
 * the install and carried in cleartext on every packet. A passive observer who never
 * breaks any encryption can therefore (a) follow any node indefinitely across time and
 * place, and (b) — because the value is phone-derived — attempt to confirm a suspected
 * phone number by recomputing the hash. That is a surveillance and targeting primitive,
 * independent of the fact that message contents are end-to-end encrypted.
 *
 * ## The design
 *   ERID(epoch) = base32( HMAC-SHA256(routingKey, epoch) )[0..length]
 * - `routingKey` is SECRET — derived from the node's identity secret via
 *   {@link deriveRoutingKey}. It is NEVER derived from the public key.
 * - `epoch = floor(unixSeconds / epochSeconds)` — a 15-minute window by default.
 * - Two ERIDs from the same node in different epochs are cryptographically uncorrelated
 *   to an outside observer — no cross-time linkage, no phone recovery.
 *
 * The epoch is encoded big-endian (8-byte signed int64) so every language port produces
 * byte-identical input to the HMAC.
 *
 * SPDX-License-Identifier: MIT
 */

import { createHmac } from "node:crypto";
import { hkdf } from "@noble/hashes/hkdf";
import { sha256 } from "@noble/hashes/sha256";

// Same Crockford base-32 alphabet as AetherNetTag (no I/L/O/U — visually unambiguous).
const ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

// HKDF domain-separation label. Must match the C# reference (and every other port).
const ROUTING_KEY_INFO = new TextEncoder().encode("aether-erid-routing-key-v1");

/** Default rotation window: 15 minutes, expressed in seconds. */
export const DEFAULT_EPOCH_SECONDS = 900;

/** Default ERID length in base-32 characters (16 chars × 5 bits = 80 bits of entropy). */
export const DEFAULT_LENGTH = 16;

/**
 * Derive the 32-byte SECRET routing key from a node's identity secret (e.g. its Ed25519
 * private-key bytes). Domain-separated via HKDF-SHA256 (RFC 5869, no salt). MUST be fed
 * a secret — never a public value, or the rotation schedule becomes computable by anyone.
 *
 * @throws {Error} if `identitySecret` is empty.
 */
export function deriveRoutingKey(identitySecret: Uint8Array): Uint8Array {
  if (identitySecret.length === 0) {
    throw new Error("ERID: identitySecret cannot be empty");
  }
  return new Uint8Array(hkdf(sha256, identitySecret, undefined, ROUTING_KEY_INFO, 32));
}

/**
 * The epoch (rotation-window index) that contains the given Unix time. Negative
 * `unixSeconds` clamp to 0.
 *
 * @throws {Error} if `epochSeconds` is not positive.
 */
export function epochFor(unixSeconds: number, epochSeconds = DEFAULT_EPOCH_SECONDS): bigint {
  if (epochSeconds <= 0) {
    throw new Error("ERID: epochSeconds must be positive");
  }
  let u = BigInt(Math.trunc(unixSeconds));
  if (u < 0n) u = 0n;
  return u / BigInt(epochSeconds);
}

/** Derive the ERID for the epoch that contains `unixSeconds`. */
export function derive(
  routingKey: Uint8Array,
  unixSeconds: number,
  epochSeconds = DEFAULT_EPOCH_SECONDS,
  length = DEFAULT_LENGTH,
): string {
  return deriveForEpoch(routingKey, epochFor(unixSeconds, epochSeconds), length);
}

/**
 * Derive the ERID for an explicit epoch number. The epoch is encoded big-endian so every
 * language port produces byte-identical input to the HMAC.
 *
 * @throws {Error} if `routingKey` is empty or `length` is outside 1..51.
 */
export function deriveForEpoch(
  routingKey: Uint8Array,
  epoch: bigint | number,
  length = DEFAULT_LENGTH,
): string {
  if (routingKey.length === 0) {
    throw new Error("ERID: routingKey cannot be empty");
  }
  if (length < 1 || length > 51) {
    throw new Error("ERID: length must be 1..51 (SHA-256 is 256 bits = 51 base-32 chars)");
  }

  const epochBytes = Buffer.alloc(8);
  epochBytes.writeBigInt64BE(BigInt(epoch)); // 8-byte big-endian signed int64

  const mac = createHmac("sha256", Buffer.from(routingKey)).update(epochBytes).digest();
  return base32(mac, length);
}

/** Encode the first `length * 5` bits of `data` as Crockford base-32, MSB first. */
function base32(data: Uint8Array, length: number): string {
  let out = "";
  let bitPos = 0;
  for (let i = 0; i < length; i++) {
    const byteIndex = bitPos >> 3;
    const bitOffset = bitPos & 7;
    const hi = data[byteIndex];
    const lo = byteIndex + 1 < data.length ? data[byteIndex + 1] : 0;
    const window = (hi << 8) | lo;
    const val = (window >> (11 - bitOffset)) & 0x1f;
    out += ALPHABET[val];
    bitPos += 5;
  }
  return out;
}
