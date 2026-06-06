/**
 * Durable {@link KeyValueStore} backed by one file per entry in a
 * configurable root directory. Writes are atomic on the local file system:
 * bytes go to a temp file inside the same directory and are then renamed
 * over the target. Keys are sanitised to a hex SHA-256 hash (with the
 * original key recoverable from a sidecar manifest) so arbitrary key
 * strings — including paths, slashes, and Unicode — round-trip safely on
 * every host OS.
 *
 * This is a simple reference impl, not a database: it doesn't compact,
 * doesn't transact across multiple keys, and has no encryption-at-rest.
 * Hosts that need any of those wrap the inner store with
 * {@link EncryptedKeyValueStore} or supply their own implementation.
 *
 * Mirrors the C# {@code FileSystemKeyValueStore} in
 * src/AetherMesh.Storage/FileSystemKeyValueStore.cs.
 *
 * SPDX-License-Identifier: MIT
 */
import { createHash } from "node:crypto";
import { mkdir, readdir, readFile, rename, rm, stat, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { KeyValueStore } from "./KeyValueStore.js";

const ENTRY_SUFFIX = ".kv";
const TEMP_SUFFIX = ".tmp";
const KEY_MANIFEST_SUFFIX = ".key";

export class FileSystemKeyValueStore implements KeyValueStore {
  private readonly root: string;
  /** Lazily-resolved promise that the root directory exists. */
  private readyPromise: Promise<void> | null = null;

  /**
   * Create a store rooted at {@code rootDirectory}. The directory is
   * created on the first read/write if it does not exist. Multiple stores
   * can share a root with disjoint {@code namespace} values.
   */
  constructor(rootDirectory: string, namespace?: string) {
    if (!rootDirectory) throw new Error("rootDirectory cannot be empty");
    this.root = namespace ? join(rootDirectory, namespace) : rootDirectory;
  }

  private async ensureRoot(): Promise<void> {
    if (this.readyPromise === null) {
      this.readyPromise = mkdir(this.root, { recursive: true }).then(() => undefined);
    }
    return this.readyPromise;
  }

  async get(key: string): Promise<Uint8Array | null> {
    if (!key) throw new Error("key cannot be empty");
    await this.ensureRoot();
    const path = this.entryPath(key);
    try {
      const buf = await readFile(path);
      return new Uint8Array(buf);
    } catch (err: any) {
      if (err && err.code === "ENOENT") return null;
      throw err;
    }
  }

  async put(key: string, value: Uint8Array): Promise<void> {
    if (!key) throw new Error("key cannot be empty");
    if (value === null || value === undefined) {
      throw new Error("value cannot be null/undefined");
    }
    await this.ensureRoot();

    const entry = this.entryPath(key);
    const temp = entry + TEMP_SUFFIX;
    await writeFile(temp, value);
    // rename is atomic on the same filesystem (POSIX) and overwrite-safe
    // on Windows since Node 14.
    await rename(temp, entry);

    const keyManifest = entry + KEY_MANIFEST_SUFFIX;
    try {
      await stat(keyManifest);
    } catch (err: any) {
      if (err && err.code === "ENOENT") {
        await writeFile(keyManifest, key, { encoding: "utf8" });
      } else {
        throw err;
      }
    }
  }

  async remove(key: string): Promise<boolean> {
    if (!key) throw new Error("key cannot be empty");
    await this.ensureRoot();
    const entry = this.entryPath(key);
    let existed = false;
    try {
      await stat(entry);
      existed = true;
    } catch (err: any) {
      if (!(err && err.code === "ENOENT")) throw err;
    }
    if (existed) {
      await rm(entry, { force: true });
      const manifest = entry + KEY_MANIFEST_SUFFIX;
      await rm(manifest, { force: true });
    }
    return existed;
  }

  async contains(key: string): Promise<boolean> {
    if (!key) throw new Error("key cannot be empty");
    await this.ensureRoot();
    try {
      await stat(this.entryPath(key));
      return true;
    } catch (err: any) {
      if (err && err.code === "ENOENT") return false;
      throw err;
    }
  }

  async listKeys(prefix?: string): Promise<string[]> {
    await this.ensureRoot();
    let entries: string[];
    try {
      entries = await readdir(this.root);
    } catch (err: any) {
      if (err && err.code === "ENOENT") return [];
      throw err;
    }
    const out: string[] = [];
    for (const name of entries) {
      if (!name.endsWith(ENTRY_SUFFIX + KEY_MANIFEST_SUFFIX)) continue;
      try {
        const original = await readFile(join(this.root, name), "utf8");
        if (prefix && !original.startsWith(prefix)) continue;
        out.push(original);
      } catch {
        // tolerate transient read errors on enumeration
        continue;
      }
    }
    return out;
  }

  private entryPath(key: string): string {
    return join(this.root, hashKey(key) + ENTRY_SUFFIX);
  }
}

function hashKey(key: string): string {
  return createHash("sha256").update(key, "utf8").digest("hex");
}
