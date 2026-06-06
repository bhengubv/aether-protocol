/**
 * {@link DataAtRestKeyProvider} that derives a 32-byte AES-256 key from a
 * passphrase and a salt using {@code scrypt} (Node BCL). The derived key
 * is cached for the lifetime of the provider so the (relatively
 * expensive) derivation runs exactly once per passphrase/version pair.
 *
 * <b>Production cost: N=2^14 (16384), r=8, p=1.</b> This matches the
 * OWASP 2023 guidance for scrypt and is the default if no cost is
 * supplied. Tests pass a smaller cost to keep the suite fast — never
 * lower the default in production code.
 *
 * The salt is required, must be at least 16 bytes, and SHOULD be unique
 * to this device. Reusing the same passphrase + salt across devices
 * would let an attacker who recovered the salt from one device decrypt
 * blobs from another — domain-separate by appending an install-id,
 * hardware-id, or randomly generated per-device value.
 *
 * Rotation: call {@link DerivedDataAtRestKeyProvider.withRotation} to
 * obtain a new provider that adds a freshly-derived key under a new
 * version while keeping every existing version available for
 * decryption.
 *
 * Mirrors the C# {@code DerivedDataAtRestKeyProvider} in
 * src/AetherNet.Storage/DerivedDataAtRestKeyProvider.cs (which uses PBKDF2
 * 600k — chosen because the .NET BCL ships PBKDF2 but not scrypt).
 * TypeScript uses scrypt because Node's BCL ships scrypt but only ships
 * PBKDF2 via the much-slower software path; both reach the OWASP
 * recommendation for their respective KDF.
 *
 * SPDX-License-Identifier: MIT
 */
import { scrypt } from "node:crypto";
import { DataAtRestKeyProvider } from "./DataAtRestKeyProvider.js";

/**
 * OWASP 2023 recommendation for scrypt: N=2^14, r=8, p=1. The cost
 * parameter is just N here; r and p are pinned to the OWASP-recommended
 * defaults inside the provider.
 */
export const DEFAULT_DERIVED_KEY_COST = 16384;

const KEY_BYTE_LENGTH = 32; // AES-256
const MINIMUM_SALT_LENGTH = 16;
const SCRYPT_R = 8;
const SCRYPT_P = 1;

/**
 * Per-version derivation parameters captured at construction time.
 */
interface VersionParams {
  passphrase: string;
  salt: Uint8Array;
  cost: number;
}

export class DerivedDataAtRestKeyProvider implements DataAtRestKeyProvider {
  /**
   * Resolved keys, keyed by version. Filled lazily on first
   * {@link getKey} for that version (or eagerly via {@link prime}).
   */
  private readonly cachedKeys: Map<number, Uint8Array> = new Map();
  private readonly versionParams: Map<number, VersionParams>;
  readonly currentVersion: number;
  /** The default cost the provider was constructed with. */
  readonly cost: number;

  /**
   * Construct a single-version provider that derives version 1 from the
   * supplied passphrase and salt. The first {@link getKey} call (or
   * {@link prime}) actually performs the scrypt computation; subsequent
   * calls hit the cache.
   */
  static async create(
    passphrase: string,
    salt: Uint8Array,
    cost: number = DEFAULT_DERIVED_KEY_COST
  ): Promise<DerivedDataAtRestKeyProvider> {
    validateInputs(passphrase, salt, cost);
    const params = new Map<number, VersionParams>();
    params.set(1, { passphrase, salt: new Uint8Array(salt), cost });
    const provider = new DerivedDataAtRestKeyProvider(params, 1, cost);
    await provider.prime(1);
    return provider;
  }

  /**
   * Synchronous variant that defers the scrypt cost to the first
   * {@link getKey} call. Useful if you can't await at construction time
   * — but be aware {@link getKey} returns null until {@link prime} has
   * been awaited at least once for the relevant version.
   */
  static lazy(
    passphrase: string,
    salt: Uint8Array,
    cost: number = DEFAULT_DERIVED_KEY_COST
  ): DerivedDataAtRestKeyProvider {
    validateInputs(passphrase, salt, cost);
    const params = new Map<number, VersionParams>();
    params.set(1, { passphrase, salt: new Uint8Array(salt), cost });
    return new DerivedDataAtRestKeyProvider(params, 1, cost);
  }

  private constructor(
    versionParams: Map<number, VersionParams>,
    currentVersion: number,
    cost: number
  ) {
    this.versionParams = versionParams;
    this.currentVersion = currentVersion;
    this.cost = cost;
  }

  /**
   * Forces derivation of the key for {@code version}, populating the
   * internal cache. Idempotent.
   */
  async prime(version: number): Promise<void> {
    if (this.cachedKeys.has(version)) return;
    const params = this.versionParams.get(version);
    if (!params) {
      throw new Error(`No derivation parameters registered for version ${version}.`);
    }
    const derived = await derive(params.passphrase, params.salt, params.cost);
    this.cachedKeys.set(version, derived);
  }

  getKey(version: number): Uint8Array | null {
    return this.cachedKeys.get(version) ?? null;
  }

  /**
   * Returns a new provider that adds a freshly-derived key under
   * {@code newVersion} (which becomes the new current version) while
   * keeping every existing version available for decryption. The new
   * key is primed before the promise resolves.
   */
  async withRotation(
    newVersion: number,
    newPassphrase: string,
    newSalt: Uint8Array,
    cost?: number
  ): Promise<DerivedDataAtRestKeyProvider> {
    if (!Number.isInteger(newVersion) || newVersion < 1 || newVersion > 255) {
      throw new Error(`newVersion must be in [1, 255] (got ${newVersion}).`);
    }
    if (this.versionParams.has(newVersion)) {
      throw new Error(`Version ${newVersion} already exists in this provider.`);
    }
    const effectiveCost = cost ?? this.cost;
    validateInputs(newPassphrase, newSalt, effectiveCost);

    const params = new Map<number, VersionParams>(this.versionParams);
    params.set(newVersion, {
      passphrase: newPassphrase,
      salt: new Uint8Array(newSalt),
      cost: effectiveCost,
    });

    const next = new DerivedDataAtRestKeyProvider(params, newVersion, effectiveCost);
    // Carry over already-derived keys so we don't redo the scrypt work.
    for (const [v, k] of this.cachedKeys.entries()) {
      next.cachedKeys.set(v, k);
    }
    await next.prime(newVersion);
    return next;
  }
}

function validateInputs(passphrase: string, salt: Uint8Array, cost: number): void {
  if (!passphrase) throw new Error("passphrase cannot be empty");
  if (!salt) throw new Error("salt cannot be null");
  if (salt.length < MINIMUM_SALT_LENGTH) {
    throw new Error(`Salt must be at least ${MINIMUM_SALT_LENGTH} bytes.`);
  }
  if (!Number.isInteger(cost) || cost < 2) {
    throw new Error("cost must be an integer >= 2.");
  }
  // scrypt N must be a power of 2.
  if ((cost & (cost - 1)) !== 0) {
    throw new Error(`cost must be a power of 2 (got ${cost}).`);
  }
}

function derive(
  passphrase: string,
  salt: Uint8Array,
  cost: number
): Promise<Uint8Array> {
  // Node's scrypt has a default maxmem cap that 2^14 fits comfortably
  // under (~16 MiB at r=8). For higher costs callers can raise this —
  // for the OWASP-recommended cost of 2^14 the default is fine.
  return new Promise((resolve, reject) => {
    scrypt(
      passphrase,
      salt,
      KEY_BYTE_LENGTH,
      { N: cost, r: SCRYPT_R, p: SCRYPT_P },
      (err, derivedKey) => {
        if (err) reject(err);
        else resolve(new Uint8Array(derivedKey));
      }
    );
  });
}
