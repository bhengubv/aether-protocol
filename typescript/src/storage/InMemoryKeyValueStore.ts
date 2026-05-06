/**
 * Process-local, volatile {@link KeyValueStore} backed by a {@link Map}.
 * Suitable for tests and demos. Loses everything on process exit.
 *
 * Mirrors the C# {@code InMemoryKeyValueStore} in
 * src/Aether.Storage/InMemoryKeyValueStore.cs.
 *
 * SPDX-License-Identifier: MIT
 */
import { KeyValueStore } from "./KeyValueStore.js";

export class InMemoryKeyValueStore implements KeyValueStore {
  private readonly entries: Map<string, Uint8Array> = new Map();

  async get(key: string): Promise<Uint8Array | null> {
    if (!key) throw new Error("key cannot be empty");
    const value = this.entries.get(key);
    if (value === undefined) return null;
    // Defensive copy on the way out — caller can't mutate stored bytes.
    return new Uint8Array(value);
  }

  async put(key: string, value: Uint8Array): Promise<void> {
    if (!key) throw new Error("key cannot be empty");
    if (value === null || value === undefined) {
      throw new Error("value cannot be null/undefined");
    }
    // Defensive copy on the way in — caller can't subsequently mutate
    // the stored bytes.
    this.entries.set(key, new Uint8Array(value));
  }

  async remove(key: string): Promise<boolean> {
    if (!key) throw new Error("key cannot be empty");
    return this.entries.delete(key);
  }

  async contains(key: string): Promise<boolean> {
    if (!key) throw new Error("key cannot be empty");
    return this.entries.has(key);
  }

  async listKeys(prefix?: string): Promise<string[]> {
    const all = Array.from(this.entries.keys());
    if (!prefix) return all;
    return all.filter((k) => k.startsWith(prefix));
  }
}
