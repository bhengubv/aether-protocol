/**
 * Encryption-at-rest wrapper for {@link KeyValueStore}. Verifies that:
 *
 *   - put/get/contains/list/remove all behave identically to the inner
 *     store from the caller's perspective.
 *   - the bytes physically stored under the inner KV store are NOT
 *     plaintext.
 *   - tampering with any byte of a stored blob fails GCM authentication
 *     and returns null on read (the caller treats that as absent).
 *   - reading under a key version not registered in the provider
 *     returns null rather than throwing.
 *   - a multi-version provider can decrypt blobs written under either
 *     version (the rotation-window scenario).
 *   - {@link EncryptedKeyValueStore.rewrap} migrates every blob to the
 *     provider's current version.
 *
 * Mirrors the C# unit-suite {@code EncryptedKeyValueStoreTests}.
 *
 * SPDX-License-Identifier: MIT
 */
import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { randomBytes } from "node:crypto";

import {
  EncryptedKeyValueStore,
  ENCRYPTED_KV_MIN_BLOB_SIZE,
  InMemoryKeyValueStore,
  StaticDataAtRestKeyProvider,
} from "../src/storage/index.js";

describe("EncryptedKeyValueStore — round-trip", () => {
  it("get returns null for an absent key", async () => {
    const inner = new InMemoryKeyValueStore();
    const provider = StaticDataAtRestKeyProvider.withSingleKey(randomBytes(32));
    const secure = new EncryptedKeyValueStore(inner, provider);
    assert.equal(await secure.get("missing"), null);
  });

  it("put then get round-trips arbitrary byte values", async () => {
    const inner = new InMemoryKeyValueStore();
    const provider = StaticDataAtRestKeyProvider.withSingleKey(randomBytes(32));
    const secure = new EncryptedKeyValueStore(inner, provider);

    const cases: Uint8Array[] = [
      new Uint8Array([]),
      new Uint8Array([0x00]),
      new Uint8Array([0xff, 0x00, 0xff]),
      new Uint8Array(Buffer.from("hello world")),
      randomBytes(1024),
    ];
    for (const value of cases) {
      await secure.put("k", value);
      const got = await secure.get("k");
      assert.ok(got !== null);
      assert.deepEqual(Array.from(got!), Array.from(value));
    }
  });

  it("inner blob is NOT plaintext", async () => {
    const inner = new InMemoryKeyValueStore();
    const provider = StaticDataAtRestKeyProvider.withSingleKey(randomBytes(32));
    const secure = new EncryptedKeyValueStore(inner, provider);

    const value = new Uint8Array(Buffer.from("secret pattern abcdef"));
    await secure.put("k", value);
    const blob = await inner.get("k");
    assert.ok(blob !== null);
    // Plaintext must not appear in the blob.
    const blobStr = Buffer.from(blob!).toString("binary");
    assert.ok(!blobStr.includes("secret pattern abcdef"));
    // Blob is at least the minimum overhead long.
    assert.ok(blob!.length >= ENCRYPTED_KV_MIN_BLOB_SIZE);
    // Version byte is 1 by default.
    assert.equal(blob![0], 1);
  });

  it("list/contains/remove pass through unchanged", async () => {
    const inner = new InMemoryKeyValueStore();
    const provider = StaticDataAtRestKeyProvider.withSingleKey(randomBytes(32));
    const secure = new EncryptedKeyValueStore(inner, provider);

    await secure.put("a", new Uint8Array([1]));
    await secure.put("b", new Uint8Array([2]));
    await secure.put("c-other", new Uint8Array([3]));
    assert.equal(await secure.contains("a"), true);
    assert.equal(await secure.contains("missing"), false);
    const all = (await secure.listKeys()).sort();
    assert.deepEqual(all, ["a", "b", "c-other"]);
    const someAB = (await secure.listKeys("a")).sort();
    assert.deepEqual(someAB, ["a"]);

    assert.equal(await secure.remove("a"), true);
    assert.equal(await secure.remove("a"), false);
  });
});

describe("EncryptedKeyValueStore — tamper detection", () => {
  it("flipping any byte in a stored blob makes get() return null", async () => {
    const inner = new InMemoryKeyValueStore();
    const provider = StaticDataAtRestKeyProvider.withSingleKey(randomBytes(32));
    const secure = new EncryptedKeyValueStore(inner, provider);

    await secure.put("k", new Uint8Array(Buffer.from("payload")));
    const blob = (await inner.get("k"))!;

    // Flip the last byte of the auth tag.
    const tampered = new Uint8Array(blob);
    tampered[tampered.length - 1] ^= 0xff;
    await inner.put("k", tampered);

    const got = await secure.get("k");
    assert.equal(got, null);
  });

  it("a too-short blob is treated as absent", async () => {
    const inner = new InMemoryKeyValueStore();
    const provider = StaticDataAtRestKeyProvider.withSingleKey(randomBytes(32));
    const secure = new EncryptedKeyValueStore(inner, provider);

    await inner.put("k", new Uint8Array([0x01, 0x02])); // way too short
    assert.equal(await secure.get("k"), null);
  });
});

describe("EncryptedKeyValueStore — key versioning", () => {
  it("blob written under one provider does NOT decrypt under a wrong key", async () => {
    const inner = new InMemoryKeyValueStore();
    const p1 = StaticDataAtRestKeyProvider.withSingleKey(randomBytes(32));
    const p2 = StaticDataAtRestKeyProvider.withSingleKey(randomBytes(32));
    const secure1 = new EncryptedKeyValueStore(inner, p1);
    const secure2 = new EncryptedKeyValueStore(inner, p2);

    await secure1.put("k", new Uint8Array(Buffer.from("payload")));
    assert.equal(await secure2.get("k"), null);
  });

  it("multi-version provider decrypts both old- and new-version blobs", async () => {
    const inner = new InMemoryKeyValueStore();
    const k1 = randomBytes(32);
    const k2 = randomBytes(32);
    const oldProvider = StaticDataAtRestKeyProvider.withSingleKey(k1);
    const oldSecure = new EncryptedKeyValueStore(inner, oldProvider);
    await oldSecure.put("k", new Uint8Array(Buffer.from("old-version")));

    const multi = new StaticDataAtRestKeyProvider(
      new Map([
        [1, k1],
        [2, k2],
      ]),
      2
    );
    const newSecure = new EncryptedKeyValueStore(inner, multi);

    // Old blob (v1) still decrypts under the multi-version provider.
    const got = await newSecure.get("k");
    assert.deepEqual(Array.from(got!), Array.from(Buffer.from("old-version")));

    // New writes use v2.
    await newSecure.put("k2", new Uint8Array(Buffer.from("new-version")));
    const blob2 = (await inner.get("k2"))!;
    assert.equal(blob2[0], 2);
  });

  it("rewrap migrates all blobs to the current key version", async () => {
    const inner = new InMemoryKeyValueStore();
    const k1 = randomBytes(32);
    const k2 = randomBytes(32);
    const oldProvider = StaticDataAtRestKeyProvider.withSingleKey(k1);
    const oldSecure = new EncryptedKeyValueStore(inner, oldProvider);
    for (let i = 0; i < 5; i++) {
      await oldSecure.put(`k${i}`, new Uint8Array(Buffer.from(`v${i}`)));
    }

    const multi = new StaticDataAtRestKeyProvider(
      new Map([
        [1, k1],
        [2, k2],
      ]),
      2
    );
    const newSecure = new EncryptedKeyValueStore(inner, multi);

    const rewrapped = await newSecure.rewrap();
    assert.equal(rewrapped, 5);

    // Every blob now has version byte 2.
    for (let i = 0; i < 5; i++) {
      const blob = (await inner.get(`k${i}`))!;
      assert.equal(blob[0], 2);
      const pt = await newSecure.get(`k${i}`);
      assert.deepEqual(Array.from(pt!), Array.from(Buffer.from(`v${i}`)));
    }
  });

  it("blob with unknown key version returns null rather than throwing", async () => {
    const inner = new InMemoryKeyValueStore();
    const k1 = randomBytes(32);
    const k2 = randomBytes(32);
    const v1Only = StaticDataAtRestKeyProvider.withSingleKey(k1);
    const v2Only = StaticDataAtRestKeyProvider.withSingleKey(k2);
    const secure1 = new EncryptedKeyValueStore(inner, v1Only);

    await secure1.put("k", new Uint8Array(Buffer.from("v1-data")));

    // Read with a provider that knows only v2 — its current version is
    // 1 too, but with a DIFFERENT key, so getKey(1) returns the wrong
    // key and decryption fails. The result is null. To exercise the
    // "version-not-registered" branch specifically, write a blob and
    // then mutate the version byte to a value the provider has never
    // heard of (e.g. 99).
    const blob = (await inner.get("k"))!;
    blob[0] = 99;
    await inner.put("k", blob);

    const got = await secure1.get("k");
    assert.equal(got, null);
    // Sanity: v2Only also can't read it.
    const secure2 = new EncryptedKeyValueStore(inner, v2Only);
    assert.equal(await secure2.get("k"), null);
  });
});
