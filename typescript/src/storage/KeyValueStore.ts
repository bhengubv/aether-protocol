/**
 * Generic byte-array-keyed-by-string persistence primitive used as the
 * foundation for every Aether store that needs to survive a process restart.
 * Implementations are responsible for atomicity and durability guarantees;
 * the protocol layer just reads and writes opaque bytes.
 *
 * Two reference implementations ship with this package:
 *   - {@link InMemoryKeyValueStore} (volatile, process-local)
 *   - {@link FileSystemKeyValueStore} (one file per key, atomic via temp+rename)
 *
 * Hosts that need richer guarantees (transactions, encrypted-at-rest,
 * network-attached) supply their own implementation. The
 * {@link EncryptedKeyValueStore} wrapper composes with any of them.
 *
 * Mirrors the C# {@code IKeyValueStore} interface in
 * src/AetherNet.Storage/IKeyValueStore.cs.
 *
 * SPDX-License-Identifier: MIT
 */
export interface KeyValueStore {
  /** Returns the bytes stored under {@code key}, or null if absent. */
  get(key: string): Promise<Uint8Array | null>;

  /** Inserts or replaces the bytes stored under {@code key}. */
  put(key: string, value: Uint8Array): Promise<void>;

  /**
   * Removes the entry under {@code key}, if present. Returns true if a
   * value was removed.
   */
  remove(key: string): Promise<boolean>;

  /** Returns true iff a value exists under {@code key}. */
  contains(key: string): Promise<boolean>;

  /**
   * Returns every key currently held by the store. If {@code prefix} is
   * supplied only keys beginning with it are returned. Order is
   * implementation-defined.
   */
  listKeys(prefix?: string): Promise<string[]>;
}
