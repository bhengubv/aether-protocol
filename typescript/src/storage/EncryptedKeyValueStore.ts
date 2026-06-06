/**
 * Transparent encryption-at-rest wrapper for an arbitrary
 * {@link KeyValueStore}. Encrypts every value on the way down and
 * decrypts on the way up using AES-256-GCM with a per-write random
 * nonce. Keys are passed through unchanged so list/range queries
 * continue to work.
 *
 * <b>Threat model:</b> protects persisted bytes from an attacker who
 * recovers the underlying medium (stolen disk, recycled SD card, leaked
 * backup) without compromising the master-key material that the host
 * hands to the {@link DataAtRestKeyProvider}. The wrapper does NOT hide
 * write patterns, key names, or value sizes. It does NOT defend against
 * in-process memory disclosure — values are plaintext while the
 * application holds them.
 *
 * <b>Wire format (per stored blob):</b>
 *
 *   {@code keyVersion (1 byte) || nonce (12 bytes) || ciphertext (N bytes) || tag (16 bytes)}
 *
 * The {@code keyVersion} byte names which key in the provider was used;
 * the wrapper looks it up on read, so hosts can run a rotation window
 * with both old and new keys loaded. Tampering with any byte fails GCM
 * authentication and the read returns null (treated as "not present" by
 * callers).
 *
 * Mirrors the C# {@code EncryptedKeyValueStore} in
 * src/AetherNet.Storage/EncryptedKeyValueStore.cs.
 *
 * SPDX-License-Identifier: MIT
 */
import { createCipheriv, createDecipheriv, randomBytes } from "node:crypto";
import { KeyValueStore } from "./KeyValueStore.js";
import { DataAtRestKeyProvider } from "./DataAtRestKeyProvider.js";

/** AES-256 key length in bytes. */
export const ENCRYPTED_KV_KEY_SIZE = 32;
/** AES-GCM nonce length in bytes. */
export const ENCRYPTED_KV_NONCE_SIZE = 12;
/** AES-GCM authentication tag length in bytes. */
export const ENCRYPTED_KV_TAG_SIZE = 16;
/** Length of the version-byte header at the start of every blob. */
export const ENCRYPTED_KV_VERSION_HEADER_SIZE = 1;
/** Minimum byte count for any well-formed encrypted blob. */
export const ENCRYPTED_KV_MIN_BLOB_SIZE =
  ENCRYPTED_KV_VERSION_HEADER_SIZE + ENCRYPTED_KV_NONCE_SIZE + ENCRYPTED_KV_TAG_SIZE;

/**
 * Wrap an inner {@link KeyValueStore} with transparent AES-256-GCM
 * encryption. The wrapper composes naturally with every other adapter
 * in this package (e.g. {@code KeyValueSignalSessionStore}) — they
 * accept any {@link KeyValueStore} so wrapping is a one-line composition:
 *
 * @example
 * ```ts
 * const inner = new FileSystemKeyValueStore(rootDir);
 * const provider = StaticDataAtRestKeyProvider.withSingleKey(masterKey);
 * const secure = new EncryptedKeyValueStore(inner, provider);
 * const sessions = new KeyValueSignalSessionStore(secure);
 * ```
 */
export class EncryptedKeyValueStore implements KeyValueStore {
  private readonly inner: KeyValueStore;
  private readonly keyProvider: DataAtRestKeyProvider;
  /** Optional logger callback. Receives a single message string. */
  private readonly warn: (message: string) => void;

  constructor(
    inner: KeyValueStore,
    keyProvider: DataAtRestKeyProvider,
    warn?: (message: string) => void
  ) {
    if (!inner) throw new Error("inner cannot be null");
    if (!keyProvider) throw new Error("keyProvider cannot be null");
    this.inner = inner;
    this.keyProvider = keyProvider;
    this.warn = warn ?? (() => undefined);
  }

  async get(key: string): Promise<Uint8Array | null> {
    if (!key) throw new Error("key cannot be empty");

    const blob = await this.inner.get(key);
    if (blob === null) return null;

    if (blob.length < ENCRYPTED_KV_MIN_BLOB_SIZE) {
      this.warn(
        `Encrypted blob under key='${key}' is smaller than the minimum ` +
          `${ENCRYPTED_KV_MIN_BLOB_SIZE} bytes — treating as tampered/missing.`
      );
      return null;
    }

    const version = blob[0];
    const keyBytes = this.keyProvider.getKey(version);
    if (keyBytes === null) {
      this.warn(
        `No data-at-rest key registered for version=${version} under key='${key}' — cannot decrypt.`
      );
      return null;
    }
    if (keyBytes.length !== ENCRYPTED_KV_KEY_SIZE) {
      throw new Error(
        `Provider returned a ${keyBytes.length}-byte key for version=${version}; AES-256 requires ${ENCRYPTED_KV_KEY_SIZE} bytes.`
      );
    }

    const nonce = blob.subarray(
      ENCRYPTED_KV_VERSION_HEADER_SIZE,
      ENCRYPTED_KV_VERSION_HEADER_SIZE + ENCRYPTED_KV_NONCE_SIZE
    );
    const tagOffset = blob.length - ENCRYPTED_KV_TAG_SIZE;
    const ciphertext = blob.subarray(
      ENCRYPTED_KV_VERSION_HEADER_SIZE + ENCRYPTED_KV_NONCE_SIZE,
      tagOffset
    );
    const tag = blob.subarray(tagOffset);

    try {
      const decipher = createDecipheriv("aes-256-gcm", keyBytes, nonce);
      decipher.setAuthTag(tag);
      const part1 = decipher.update(ciphertext);
      const part2 = decipher.final();
      return new Uint8Array(Buffer.concat([part1, part2]));
    } catch (err) {
      // GCM authentication failed: caller treats absent rather than raising.
      this.warn(
        `AES-GCM authentication failed reading key='${key}' (version=${version}). ` +
          "Either the wrong key is configured or the blob has been tampered with."
      );
      return null;
    }
  }

  async put(key: string, value: Uint8Array): Promise<void> {
    if (!key) throw new Error("key cannot be empty");
    if (value === null || value === undefined) {
      throw new Error("value cannot be null/undefined");
    }

    const version = this.keyProvider.currentVersion;
    if (!Number.isInteger(version) || version < 1 || version > 255) {
      throw new Error(
        `DataAtRestKeyProvider.currentVersion=${version} is outside the supported [1, 255] range.`
      );
    }

    const keyBytes = this.keyProvider.getKey(version);
    if (keyBytes === null) {
      throw new Error(
        `DataAtRestKeyProvider returned null for its own currentVersion=${version}.`
      );
    }
    if (keyBytes.length !== ENCRYPTED_KV_KEY_SIZE) {
      throw new Error(
        `DataAtRestKeyProvider returned a ${keyBytes.length}-byte key; AES-256 requires ${ENCRYPTED_KV_KEY_SIZE} bytes.`
      );
    }

    const nonce = randomBytes(ENCRYPTED_KV_NONCE_SIZE);
    const cipher = createCipheriv("aes-256-gcm", keyBytes, nonce);
    const ct1 = cipher.update(value);
    const ct2 = cipher.final();
    const tag = cipher.getAuthTag();

    const blob = new Uint8Array(
      ENCRYPTED_KV_VERSION_HEADER_SIZE + ENCRYPTED_KV_NONCE_SIZE + ct1.length + ct2.length + ENCRYPTED_KV_TAG_SIZE
    );
    blob[0] = version;
    blob.set(nonce, ENCRYPTED_KV_VERSION_HEADER_SIZE);
    blob.set(ct1, ENCRYPTED_KV_VERSION_HEADER_SIZE + ENCRYPTED_KV_NONCE_SIZE);
    blob.set(ct2, ENCRYPTED_KV_VERSION_HEADER_SIZE + ENCRYPTED_KV_NONCE_SIZE + ct1.length);
    blob.set(tag, blob.length - ENCRYPTED_KV_TAG_SIZE);

    await this.inner.put(key, blob);
  }

  async remove(key: string): Promise<boolean> {
    if (!key) throw new Error("key cannot be empty");
    return this.inner.remove(key);
  }

  async contains(key: string): Promise<boolean> {
    if (!key) throw new Error("key cannot be empty");
    return this.inner.contains(key);
  }

  async listKeys(prefix?: string): Promise<string[]> {
    return this.inner.listKeys(prefix);
  }

  /**
   * Re-encrypts every value in the underlying store under the provider's
   * current key version. Use during a key-rotation window after the
   * provider has been swapped out for one that holds both the old and
   * new keys — values written under the old version stay readable, and
   * after the rewrap completes every blob is on the new version so the
   * host can retire the old key on the next deploy.
   *
   * @returns The number of values successfully rewrapped.
   */
  async rewrap(): Promise<number> {
    const keys = await this.inner.listKeys();
    let rewrapped = 0;
    for (const k of keys) {
      const plaintext = await this.get(k);
      if (plaintext === null) {
        this.warn(
          `Skipping rewrap of key='${k}' — value could not be decrypted under any registered key version.`
        );
        continue;
      }
      await this.put(k, plaintext);
      rewrapped++;
    }
    return rewrapped;
  }
}
