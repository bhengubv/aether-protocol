/**
 * {@link DerivedDataAtRestKeyProvider}: scrypt-derived AES-256 keys for
 * the encryption-at-rest wrapper. Verifies caching, validation,
 * end-to-end composition with {@link EncryptedKeyValueStore}, and
 * rotation.
 *
 * Mirrors the C# unit-suite {@code DerivedDataAtRestKeyProviderTests}
 * (which uses PBKDF2 600k — same OWASP-recommended bar, different KDF
 * because the .NET BCL ships PBKDF2 but not scrypt natively).
 *
 * SPDX-License-Identifier: MIT
 */
import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { randomBytes } from "node:crypto";

import {
  DerivedDataAtRestKeyProvider,
  DEFAULT_DERIVED_KEY_COST,
  EncryptedKeyValueStore,
  InMemoryKeyValueStore,
} from "../src/storage/index.js";

// Cheap test cost — 2^10. Production default is 2^14.
const TEST_COST = 1024;

describe("DerivedDataAtRestKeyProvider — defaults", () => {
  it("DEFAULT_DERIVED_KEY_COST matches the OWASP scrypt recommendation (2^14)", () => {
    assert.equal(DEFAULT_DERIVED_KEY_COST, 16384);
  });

  it("create derives a 32-byte key for version 1", async () => {
    const salt = randomBytes(16);
    const provider = await DerivedDataAtRestKeyProvider.create("passphrase", salt, TEST_COST);
    assert.equal(provider.currentVersion, 1);
    const k = provider.getKey(1);
    assert.ok(k !== null);
    assert.equal(k!.length, 32);
  });

  it("getKey returns null for unknown versions", async () => {
    const salt = randomBytes(16);
    const provider = await DerivedDataAtRestKeyProvider.create("passphrase", salt, TEST_COST);
    assert.equal(provider.getKey(2), null);
    assert.equal(provider.getKey(99), null);
  });
});

describe("DerivedDataAtRestKeyProvider — input validation", () => {
  it("rejects empty passphrase", async () => {
    await assert.rejects(() =>
      DerivedDataAtRestKeyProvider.create("", randomBytes(16), TEST_COST)
    );
  });

  it("rejects salt shorter than 16 bytes", async () => {
    await assert.rejects(() =>
      DerivedDataAtRestKeyProvider.create("p", randomBytes(8), TEST_COST)
    );
  });

  it("rejects non-power-of-2 cost", async () => {
    await assert.rejects(() =>
      DerivedDataAtRestKeyProvider.create("p", randomBytes(16), 1000)
    );
  });

  it("rejects cost < 2", async () => {
    await assert.rejects(() =>
      DerivedDataAtRestKeyProvider.create("p", randomBytes(16), 1)
    );
  });
});

describe("DerivedDataAtRestKeyProvider — caching", () => {
  it("getKey returns the same buffer reference on repeated calls", async () => {
    const salt = randomBytes(16);
    const provider = await DerivedDataAtRestKeyProvider.create("p", salt, TEST_COST);
    const k1 = provider.getKey(1);
    const k2 = provider.getKey(1);
    assert.ok(k1 === k2);
  });

  it("two providers with the same passphrase + salt derive the same key", async () => {
    const salt = randomBytes(16);
    const a = await DerivedDataAtRestKeyProvider.create("p", salt, TEST_COST);
    const b = await DerivedDataAtRestKeyProvider.create("p", salt, TEST_COST);
    assert.deepEqual(Array.from(a.getKey(1)!), Array.from(b.getKey(1)!));
  });

  it("different passphrases produce different keys", async () => {
    const salt = randomBytes(16);
    const a = await DerivedDataAtRestKeyProvider.create("p1", salt, TEST_COST);
    const b = await DerivedDataAtRestKeyProvider.create("p2", salt, TEST_COST);
    assert.notDeepEqual(Array.from(a.getKey(1)!), Array.from(b.getKey(1)!));
  });

  it("different salts produce different keys", async () => {
    const a = await DerivedDataAtRestKeyProvider.create("p", randomBytes(16), TEST_COST);
    const b = await DerivedDataAtRestKeyProvider.create("p", randomBytes(16), TEST_COST);
    assert.notDeepEqual(Array.from(a.getKey(1)!), Array.from(b.getKey(1)!));
  });
});

describe("DerivedDataAtRestKeyProvider — composition with EncryptedKeyValueStore", () => {
  it("end-to-end round-trip via the encryption-at-rest wrapper", async () => {
    const inner = new InMemoryKeyValueStore();
    const provider = await DerivedDataAtRestKeyProvider.create(
      "correct-horse-battery-staple",
      randomBytes(16),
      TEST_COST
    );
    const secure = new EncryptedKeyValueStore(inner, provider);
    await secure.put("k", new Uint8Array(Buffer.from("hello")));
    const got = await secure.get("k");
    assert.deepEqual(Array.from(got!), Array.from(Buffer.from("hello")));
  });

  it("a provider with the wrong passphrase cannot decrypt", async () => {
    const inner = new InMemoryKeyValueStore();
    const salt = randomBytes(16);
    const right = await DerivedDataAtRestKeyProvider.create("good", salt, TEST_COST);
    const wrong = await DerivedDataAtRestKeyProvider.create("bad", salt, TEST_COST);
    const a = new EncryptedKeyValueStore(inner, right);
    const b = new EncryptedKeyValueStore(inner, wrong);
    await a.put("k", new Uint8Array(Buffer.from("payload")));
    assert.equal(await b.get("k"), null);
  });
});

describe("DerivedDataAtRestKeyProvider — rotation", () => {
  it("withRotation adds a new version while keeping the old one decryptable", async () => {
    const oldSalt = randomBytes(16);
    const oldProvider = await DerivedDataAtRestKeyProvider.create("old-pass", oldSalt, TEST_COST);
    const newProvider = await oldProvider.withRotation(2, "new-pass", randomBytes(16), TEST_COST);

    assert.equal(newProvider.currentVersion, 2);
    assert.ok(newProvider.getKey(1) !== null, "v1 must still be available for decryption");
    assert.ok(newProvider.getKey(2) !== null, "v2 must be the new active key");
    assert.notDeepEqual(
      Array.from(newProvider.getKey(1)!),
      Array.from(newProvider.getKey(2)!)
    );
  });

  it("EncryptedKeyValueStore.rewrap migrates blobs from v1 to v2 under the rotated provider", async () => {
    const inner = new InMemoryKeyValueStore();
    const v1 = await DerivedDataAtRestKeyProvider.create("v1", randomBytes(16), TEST_COST);
    const oldStore = new EncryptedKeyValueStore(inner, v1);
    await oldStore.put("k", new Uint8Array(Buffer.from("payload")));
    assert.equal((await inner.get("k"))![0], 1);

    const v1AndV2 = await v1.withRotation(2, "v2", randomBytes(16), TEST_COST);
    const newStore = new EncryptedKeyValueStore(inner, v1AndV2);
    const rewrapped = await newStore.rewrap();
    assert.equal(rewrapped, 1);
    assert.equal((await inner.get("k"))![0], 2);
  });
});
