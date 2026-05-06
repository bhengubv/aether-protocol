/**
 * Supplies the AES-256 master key(s) used by {@link EncryptedKeyValueStore}
 * to encrypt and decrypt persisted values at rest.
 *
 * Two responsibilities:
 *   - {@link DataAtRestKeyProvider.currentVersion} tells the wrapper which
 *     key version to stamp onto every newly written blob. Hosts increment
 *     this to roll the key.
 *   - {@link DataAtRestKeyProvider.getKey} hands back the 32-byte AES-256
 *     key for a given version on read. During a key-rotation window, the
 *     provider keeps both the old and new key so previously written blobs
 *     continue to decrypt.
 *
 * Hosts derive these bytes however they like — from a passphrase via a
 * KDF (see {@link DerivedDataAtRestKeyProvider}), from the OS keychain
 * (DPAPI / Keychain / Android Keystore), from a hardware enclave, or
 * from a remote KMS. The wrapper never sees the source.
 *
 * All keys returned by {@link DataAtRestKeyProvider.getKey} MUST be
 * exactly 32 bytes (AES-256).
 *
 * Mirrors the C# {@code IDataAtRestKeyProvider} interface in
 * src/Aether.Storage/IDataAtRestKeyProvider.cs.
 *
 * SPDX-License-Identifier: MIT
 */
export interface DataAtRestKeyProvider {
  /**
   * The key version stamped onto every blob written via this provider.
   * Must be in the range [1, 255] so it fits in the single-byte version
   * header of the encrypted blob format.
   */
  readonly currentVersion: number;

  /**
   * Returns the 32-byte AES-256 key for the given version, or null if
   * the provider has no key for that version (the blob was written under
   * a key that has since been retired).
   */
  getKey(version: number): Uint8Array | null;
}

/**
 * Simple {@link DataAtRestKeyProvider} backed by one or more pre-derived
 * 32-byte AES-256 keys that the host supplies directly. Useful for tests,
 * demos, and deployments that derive their key material out of band
 * (e.g. from the OS keychain, a hardware enclave, or a remote KMS) and
 * just need to inject the resulting bytes into the wrapper.
 *
 * The simplest construction takes a single 32-byte key and assigns it
 * version 1. Hosts that rotate pass the multi-version constructor with
 * both the previous and current versions so that values written under
 * the old key keep decrypting during the rotation window.
 */
export class StaticDataAtRestKeyProvider implements DataAtRestKeyProvider {
  private readonly keys: Map<number, Uint8Array>;
  readonly currentVersion: number;

  /**
   * Single-version provider where {@code key} is the AES-256 master key
   * and the current version defaults to 1.
   */
  static withSingleKey(key: Uint8Array): StaticDataAtRestKeyProvider {
    const map = new Map<number, Uint8Array>();
    map.set(1, validateKey(key));
    return new StaticDataAtRestKeyProvider(map, 1);
  }

  /**
   * Multi-version provider for key-rotation deployments. Every value in
   * {@code keysByVersion} must be 32 bytes; {@code currentVersion} must
   * reference a key present in the map and must be in [1, 255].
   */
  constructor(keysByVersion: Map<number, Uint8Array>, currentVersion: number) {
    if (!keysByVersion) throw new Error("keysByVersion cannot be null");
    if (!Number.isInteger(currentVersion) || currentVersion < 1 || currentVersion > 255) {
      throw new Error(`currentVersion must be in [1, 255] (got ${currentVersion}).`);
    }
    if (!keysByVersion.has(currentVersion)) {
      throw new Error(
        `keysByVersion does not contain an entry for currentVersion=${currentVersion}.`
      );
    }

    this.keys = new Map();
    for (const [version, key] of keysByVersion.entries()) {
      if (!Number.isInteger(version) || version < 1 || version > 255) {
        throw new Error(`Key version ${version} is outside [1, 255].`);
      }
      this.keys.set(version, validateKey(key));
    }
    this.currentVersion = currentVersion;
  }

  getKey(version: number): Uint8Array | null {
    const k = this.keys.get(version);
    return k === undefined ? null : k;
  }
}

function validateKey(key: Uint8Array): Uint8Array {
  if (!key) throw new Error("key cannot be null");
  if (key.length !== 32) {
    throw new Error(`Data-at-rest key must be exactly 32 bytes (AES-256); got ${key.length}.`);
  }
  // Defensive copy — caller can't subsequently zero our key buffer.
  return new Uint8Array(key);
}
