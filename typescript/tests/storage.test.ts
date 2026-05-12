/**
 * Unit tests for InMemoryKeyValueStore and FileSystemKeyValueStore.
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/storage.test.ts
 */

import { describe, it, before, after } from "node:test";
import { strict as assert } from "node:assert";
import * as fs from "node:fs/promises";
import * as os from "node:os";
import * as path from "node:path";

import { InMemoryKeyValueStore } from "../src/storage/InMemoryKeyValueStore.js";
import { FileSystemKeyValueStore } from "../src/storage/FileSystemKeyValueStore.js";

// ── InMemoryKeyValueStore ────────────────────────────────────────────────────

describe("InMemoryKeyValueStore — get / put", () => {
  it("returns null for a missing key", async () => {
    const store = new InMemoryKeyValueStore();
    const val = await store.get("missing");
    assert.equal(val, null);
  });

  it("returns stored bytes after put", async () => {
    const store = new InMemoryKeyValueStore();
    const data = new Uint8Array([1, 2, 3]);
    await store.put("k1", data);
    const got = await store.get("k1");
    assert.ok(got !== null);
    assert.deepEqual(got, data);
  });

  it("overwrites an existing key", async () => {
    const store = new InMemoryKeyValueStore();
    await store.put("k1", new Uint8Array([1, 2, 3]));
    await store.put("k1", new Uint8Array([9, 8, 7]));
    const got = await store.get("k1");
    assert.deepEqual(got, new Uint8Array([9, 8, 7]));
  });

  it("get returns a defensive copy (mutation does not affect store)", async () => {
    const store = new InMemoryKeyValueStore();
    await store.put("k1", new Uint8Array([10, 20, 30]));
    const got = await store.get("k1");
    assert.ok(got !== null);
    got[0] = 0xFF; // mutate returned copy
    const got2 = await store.get("k1");
    assert.equal(got2![0], 10, "stored value must not be affected by mutation of returned copy");
  });

  it("put stores a defensive copy (mutation of source does not affect store)", async () => {
    const store = new InMemoryKeyValueStore();
    const original = new Uint8Array([10, 20, 30]);
    await store.put("k1", original);
    original[0] = 0xFF; // mutate source after put
    const got = await store.get("k1");
    assert.equal(got![0], 10, "stored value must not be affected by mutation of source after put");
  });

  it("throws for empty key on put", async () => {
    const store = new InMemoryKeyValueStore();
    await assert.rejects(() => store.put("", new Uint8Array([1])));
  });

  it("throws for empty key on get", async () => {
    const store = new InMemoryKeyValueStore();
    await assert.rejects(() => store.get(""));
  });

  it("throws when value is null/undefined on put", async () => {
    const store = new InMemoryKeyValueStore();
    await assert.rejects(() => store.put("k1", null as unknown as Uint8Array));
  });
});

describe("InMemoryKeyValueStore — remove", () => {
  it("returns true when key existed", async () => {
    const store = new InMemoryKeyValueStore();
    await store.put("k1", new Uint8Array([1]));
    const removed = await store.remove("k1");
    assert.equal(removed, true);
  });

  it("key is gone after remove", async () => {
    const store = new InMemoryKeyValueStore();
    await store.put("k1", new Uint8Array([1]));
    await store.remove("k1");
    const got = await store.get("k1");
    assert.equal(got, null);
  });

  it("returns false when key did not exist", async () => {
    const store = new InMemoryKeyValueStore();
    const removed = await store.remove("ghost");
    assert.equal(removed, false);
  });
});

describe("InMemoryKeyValueStore — listKeys", () => {
  it("returns empty array when store is empty", async () => {
    const store = new InMemoryKeyValueStore();
    const keys = await store.listKeys();
    assert.deepEqual(keys, []);
  });

  it("returns all keys in the store", async () => {
    const store = new InMemoryKeyValueStore();
    await store.put("apple", new Uint8Array([1]));
    await store.put("banana", new Uint8Array([2]));
    await store.put("cherry", new Uint8Array([3]));
    const keys = await store.listKeys();
    assert.equal(keys.length, 3);
    assert.ok(keys.includes("apple"));
    assert.ok(keys.includes("banana"));
    assert.ok(keys.includes("cherry"));
  });

  it("filters keys by prefix when prefix is given", async () => {
    const store = new InMemoryKeyValueStore();
    await store.put("user:alice", new Uint8Array([1]));
    await store.put("user:bob", new Uint8Array([2]));
    await store.put("config:timeout", new Uint8Array([3]));
    const userKeys = await store.listKeys("user:");
    assert.equal(userKeys.length, 2);
    assert.ok(userKeys.includes("user:alice"));
    assert.ok(userKeys.includes("user:bob"));
    assert.ok(!userKeys.includes("config:timeout"));
  });

  it("does not include removed keys", async () => {
    const store = new InMemoryKeyValueStore();
    await store.put("a", new Uint8Array([1]));
    await store.put("b", new Uint8Array([2]));
    await store.remove("a");
    const keys = await store.listKeys();
    assert.deepEqual(keys, ["b"]);
  });
});

describe("InMemoryKeyValueStore — isolation between instances", () => {
  it("two separate instances do not share data", async () => {
    const s1 = new InMemoryKeyValueStore();
    const s2 = new InMemoryKeyValueStore();
    await s1.put("k", new Uint8Array([1]));
    const got = await s2.get("k");
    assert.equal(got, null, "stores must be independent");
  });
});

// ── FileSystemKeyValueStore ───────────────────────────────────────────────────

describe("FileSystemKeyValueStore — constructor", () => {
  it("throws for empty rootDirectory", () => {
    assert.throws(() => new FileSystemKeyValueStore(""));
  });
});

describe("FileSystemKeyValueStore — basic get / put", () => {
  let tmpDir: string;
  let store: FileSystemKeyValueStore;

  before(async () => {
    tmpDir = await fs.mkdtemp(path.join(os.tmpdir(), "aether-storage-test-"));
    store = new FileSystemKeyValueStore(tmpDir);
  });

  after(async () => {
    await fs.rm(tmpDir, { recursive: true, force: true });
  });

  it("returns null for a missing key", async () => {
    const val = await store.get("nonexistent");
    assert.equal(val, null);
  });

  it("stores and retrieves bytes", async () => {
    const data = new Uint8Array([0xDE, 0xAD, 0xBE, 0xEF]);
    await store.put("test-key", data);
    const got = await store.get("test-key");
    assert.ok(got !== null);
    assert.deepEqual(got, data);
  });

  it("overwrites an existing key", async () => {
    await store.put("overwrite", new Uint8Array([1, 2, 3]));
    await store.put("overwrite", new Uint8Array([9, 8, 7]));
    const got = await store.get("overwrite");
    assert.deepEqual(got, new Uint8Array([9, 8, 7]));
  });

  it("handles keys with special characters", async () => {
    const key = "user:alice/profile.json";
    const data = new TextEncoder().encode(JSON.stringify({ name: "alice" }));
    await store.put(key, data);
    const got = await store.get(key);
    assert.ok(got !== null);
    assert.deepEqual(got, data);
  });

  it("throws for empty key on put", async () => {
    await assert.rejects(() => store.put("", new Uint8Array([1])));
  });

  it("throws for empty key on get", async () => {
    await assert.rejects(() => store.get(""));
  });
});

describe("FileSystemKeyValueStore — remove", () => {
  let tmpDir: string;
  let store: FileSystemKeyValueStore;

  before(async () => {
    tmpDir = await fs.mkdtemp(path.join(os.tmpdir(), "aether-storage-rm-"));
    store = new FileSystemKeyValueStore(tmpDir);
  });

  after(async () => {
    await fs.rm(tmpDir, { recursive: true, force: true });
  });

  it("returns true when key existed", async () => {
    await store.put("del-me", new Uint8Array([42]));
    const removed = await store.remove("del-me");
    assert.equal(removed, true);
  });

  it("key is gone after remove", async () => {
    await store.put("gone", new Uint8Array([1]));
    await store.remove("gone");
    const got = await store.get("gone");
    assert.equal(got, null);
  });

  it("returns false for a key that does not exist", async () => {
    const removed = await store.remove("never-existed");
    assert.equal(removed, false);
  });
});

describe("FileSystemKeyValueStore — listKeys", () => {
  let tmpDir: string;
  let store: FileSystemKeyValueStore;

  before(async () => {
    tmpDir = await fs.mkdtemp(path.join(os.tmpdir(), "aether-storage-list-"));
    store = new FileSystemKeyValueStore(tmpDir);
  });

  after(async () => {
    await fs.rm(tmpDir, { recursive: true, force: true });
  });

  it("returns empty array when store is empty", async () => {
    const keys = await store.listKeys();
    assert.deepEqual(keys, []);
  });

  it("returns all inserted keys", async () => {
    await store.put("alpha", new Uint8Array([1]));
    await store.put("beta", new Uint8Array([2]));
    await store.put("gamma", new Uint8Array([3]));
    const keys = await store.listKeys();
    assert.equal(keys.length, 3);
    assert.ok(keys.includes("alpha"));
    assert.ok(keys.includes("beta"));
    assert.ok(keys.includes("gamma"));
  });

  it("filters by prefix when prefix is given", async () => {
    // Keys from previous test plus new ones
    await store.put("ns:x", new Uint8Array([10]));
    await store.put("ns:y", new Uint8Array([11]));
    const nsKeys = await store.listKeys("ns:");
    assert.ok(nsKeys.includes("ns:x"));
    assert.ok(nsKeys.includes("ns:y"));
    // Should not include keys without "ns:" prefix
    assert.ok(!nsKeys.includes("alpha"));
  });
});

describe("FileSystemKeyValueStore — namespace isolation", () => {
  let tmpDir: string;

  before(async () => {
    tmpDir = await fs.mkdtemp(path.join(os.tmpdir(), "aether-storage-ns-"));
  });

  after(async () => {
    await fs.rm(tmpDir, { recursive: true, force: true });
  });

  it("different namespaces do not share keys", async () => {
    const s1 = new FileSystemKeyValueStore(tmpDir, "ns1");
    const s2 = new FileSystemKeyValueStore(tmpDir, "ns2");
    await s1.put("shared-key", new Uint8Array([0xAA]));
    const got = await s2.get("shared-key");
    assert.equal(got, null, "namespace isolation: ns2 must not see ns1 key");
  });
});

describe("FileSystemKeyValueStore — persistence", () => {
  let tmpDir: string;

  before(async () => {
    tmpDir = await fs.mkdtemp(path.join(os.tmpdir(), "aether-storage-persist-"));
  });

  after(async () => {
    await fs.rm(tmpDir, { recursive: true, force: true });
  });

  it("data survives recreating the store object", async () => {
    const store1 = new FileSystemKeyValueStore(tmpDir, "persist");
    const expected = new Uint8Array([0xFF, 0xFE, 0xFD]);
    await store1.put("durable", expected);

    // Create a brand-new instance pointing at the same directory.
    const store2 = new FileSystemKeyValueStore(tmpDir, "persist");
    const got = await store2.get("durable");
    assert.ok(got !== null, "key should survive across store instances");
    assert.deepEqual(got, expected);
  });
});
