// SPDX-License-Identifier: MIT

/**
 * Panic-wipe: the identity-erasure core of an AetherNet node's duress defence.
 * A duress PIN (or panic button) irreversibly destroys the node's key material,
 * so a seized device reveals nothing and looks like a fresh install.
 *
 * This module is the protocol-level core — deterministic and portable across
 * every AetherNet SDK:
 *
 *   - {@link duressPinHash} / {@link verifyDuressPin} — recognise the duress PIN
 *     (SHA-256, constant-time compare); the PIN itself is never stored.
 *   - {@link secureErase} — best-effort in-memory erase of key material
 *     (overwrite with random, then zero).
 *   - {@link IDENTITY_KEY_NAMES} + {@link preKeyName} / {@link signedPreKeyName} —
 *     the canonical set of key-store entries a wipe must destroy.
 *
 * Destroying the hosting app's local database, platform keychain entries and any
 * decoy store is the app's job — it owns that storage. This module gives the app
 * the crypto trigger, the secure-erase primitive, and the manifest of what to
 * remove, so every app wipes the same identity material the same way.
 *
 * The deterministic parts are byte-identical across every AetherNet SDK
 * (verified against fixtures/panicwipe/vectors.json).
 */

import { createHash, timingSafeEqual, randomFillSync } from "crypto";

/** Number of one-time / signed pre-key slots a wipe sweeps (0..N-1). */
export const MAX_PRE_KEYS = 200;

/**
 * The key-store entry names that together constitute an AetherNet identity —
 * everything a panic-wipe must destroy, besides the numbered pre-keys.
 */
export const IDENTITY_KEY_NAMES: readonly string[] = [
  "aether_identity_pub",
  "aether_identity_priv",
  "aether_identity_generated",
  "aether_device_salt",
  "aether_drk",
  "aether_ble_rotation_key",
  "aether_ble_irk",
];

/** Key-store name of the i-th one-time pre-key. */
export function preKeyName(index: number): string {
  return `prekey_${index}`;
}

/** Key-store name of the i-th signed pre-key. */
export function signedPreKeyName(index: number): string {
  return `signed_prekey_${index}`;
}

/**
 * The duress-PIN hash: SHA-256 of the UTF-8 PIN (32 bytes). Stored at setup and
 * compared on unlock — the PIN is only ever kept as this hash.
 */
export function duressPinHash(pin: string): Uint8Array {
  return new Uint8Array(createHash("sha256").update(pin, "utf8").digest());
}

/**
 * Constant-time check of whether `pin` matches a stored {@link duressPinHash} —
 * i.e. whether unlocking should trigger a wipe. Returns false for any
 * `storedHash` that is not exactly 32 bytes.
 */
export function verifyDuressPin(pin: string, storedHash: Uint8Array): boolean {
  if (storedHash.length !== 32) return false;
  return timingSafeEqual(duressPinHash(pin), storedHash);
}

/**
 * Best-effort secure erase of in-memory key material: overwrite with random
 * bytes, then zero. Call on every buffer holding a secret before releasing it.
 * Defence in depth — the runtime or OS may still hold copies, but this removes
 * the obvious one and leaves no plaintext secret in the buffer.
 */
export function secureErase(buffer: Uint8Array): void {
  if (buffer.length === 0) return;
  randomFillSync(buffer);
  buffer.fill(0);
}
