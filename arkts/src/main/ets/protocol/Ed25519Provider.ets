/*
 * Deterministic Ed25519 provider. ArkTS (HarmonyOS) port.
 *
 * Crypto backend is @noble/ed25519 — a pure-JavaScript, byte-stable Ed25519
 * implementation that runs unmodified under BOTH the HarmonyOS ArkTS runtime
 * (declared as an oh-package.json5 dependency) and Node (the parity harness).
 * Because the same deterministic library is used in both places, the ArkTS port
 * reproduces the C# reference fixture signatures byte-for-byte — this is the
 * concrete advantage over a platform that can only verify (not regenerate) the
 * vectors.
 *
 * @noble/ed25519 v2 exposes synchronous sign/verify only once a SHA-512
 * implementation is wired into `etc.sha512Sync`. SHA-512 is supplied by
 * @noble/hashes, the companion pure-JS hash library (also ArkTS-compatible). The
 * wiring is performed exactly once at module load.
 *
 * This is the ONLY source file in the port that depends on an external crypto
 * package; everything else (canonical byte builders, Reed-Solomon, packet models)
 * is dependency-free. Services depend on the small `Ed25519Signer` interface
 * below, never on @noble directly, so the backend is swappable (e.g. for
 * @ohos.security.cryptoFramework on a device build) without touching protocol
 * logic.
 *
 * SPDX-License-Identifier: MIT
 */

import { getPublicKey, sign, verify, etc } from '@noble/ed25519';
import { sha512 } from '@noble/hashes/sha2.js';

// ── Wire SHA-512 for synchronous Ed25519 (one-time, idempotent) ────────────────
// `etc.sha512Sync` must be set before any synchronous sign/verify call. Once set,
// @noble/ed25519 produces RFC 8032 deterministic signatures.
type Sha512SyncFn = (...messages: Uint8Array[]) => Uint8Array;
interface NobleEtc {
  sha512Sync?: Sha512SyncFn;
  concatBytes(...arrays: Uint8Array[]): Uint8Array;
}
const nobleEtc: NobleEtc = etc as NobleEtc;
if (nobleEtc.sha512Sync === undefined) {
  nobleEtc.sha512Sync = (...messages: Uint8Array[]): Uint8Array =>
    sha512(nobleEtc.concatBytes(...messages));
}

/** 32-byte Ed25519 private seed. */
export const ED25519_SEED_LENGTH: number = 32;
/** 32-byte Ed25519 public key. */
export const ED25519_PUBLIC_KEY_LENGTH: number = 32;
/** 64-byte Ed25519 signature. */
export const ED25519_SIGNATURE_LENGTH: number = 64;

/**
 * Signs and verifies canonical byte arrays with a node's Ed25519 identity key.
 * The protocol services (tipping, PoV exchange) depend only on this interface.
 */
export interface Ed25519Signer {
  /** Produces a 64-byte deterministic Ed25519 signature over `data`. */
  signData(data: Uint8Array): Uint8Array;
  /** Verifies `sig` over `data` against `publicKey`. */
  verifySignature(publicKey: Uint8Array, data: Uint8Array, sig: Uint8Array): boolean;
}

/**
 * A concrete Ed25519 identity backed by a 32-byte seed. Deterministic: the same
 * (seed, message) pair always yields the same signature, matching the C#
 * reference and every other language port.
 */
export class NobleEd25519Signer implements Ed25519Signer {
  private readonly seed: Uint8Array;
  private readonly publicKey: Uint8Array;

  /** Wraps a 32-byte seed; derives and caches the public key. */
  constructor(seed: Uint8Array) {
    if (seed.length !== ED25519_SEED_LENGTH) {
      throw new Error(`Ed25519 seed must be ${ED25519_SEED_LENGTH} bytes (got ${seed.length})`);
    }
    this.seed = seed.slice();
    this.publicKey = getPublicKey(this.seed);
  }

  /** The 32-byte public key derived from this signer's seed. */
  getPublicKey(): Uint8Array {
    return this.publicKey.slice();
  }

  signData(data: Uint8Array): Uint8Array {
    return sign(data, this.seed);
  }

  verifySignature(publicKey: Uint8Array, data: Uint8Array, sig: Uint8Array): boolean {
    if (sig.length !== ED25519_SIGNATURE_LENGTH) {
      return false;
    }
    try {
      return verify(sig, data, publicKey);
    } catch (_e) {
      // A malformed key/point/signature must verify false, never throw.
      return false;
    }
  }
}

/** Derives the 32-byte Ed25519 public key for a 32-byte seed. */
export function publicKeyFromSeed(seed: Uint8Array): Uint8Array {
  return getPublicKey(seed);
}

/** Stand-alone deterministic Ed25519 signature over `data` for a seed. */
export function signWithSeed(seed: Uint8Array, data: Uint8Array): Uint8Array {
  return sign(data, seed);
}

/** Stand-alone Ed25519 verification (false on any malformed input, never throws). */
export function verifyEd25519(publicKey: Uint8Array, data: Uint8Array, sig: Uint8Array): boolean {
  if (sig.length !== ED25519_SIGNATURE_LENGTH) {
    return false;
  }
  try {
    return verify(sig, data, publicKey);
  } catch (_e) {
    return false;
  }
}
