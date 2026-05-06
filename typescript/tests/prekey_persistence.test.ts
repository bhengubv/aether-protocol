/**
 * Persistence of identity keys, signed-pre-key history, and the OPK pool
 * via the {@link PreKeyStore} adapter — verifies that an instance which
 * restarts against the same KV store recovers all responder-side X3DH
 * material so peers can still mint new pre-key bundles AND complete
 * already-issued ones.
 *
 * Mirrors the C# unit-suite {@code SignalProtocolService.PreKeyPersistenceTests}.
 *
 * SPDX-License-Identifier: MIT
 */
import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { SignalProtocol } from "../src/security/SignalProtocol.js";
import {
  InMemorySignalSessionStore,
  KeyValueSignalSessionStore,
} from "../src/security/SignalSessionStore.js";
import {
  InMemoryPreKeyStore,
  KeyValuePreKeyStore,
} from "../src/security/PreKeyStore.js";
import { InMemoryKeyValueStore } from "../src/storage/index.js";

describe("PreKeyStore — identity persistence", () => {
  it("first boot persists the freshly-generated identity", async () => {
    const prekeys = new InMemoryPreKeyStore();
    const svc = new SignalProtocol({ preKeyStore: prekeys });
    await svc.ready();
    const stored = await prekeys.loadIdentity();
    assert.ok(stored !== null);
    assert.equal(stored!.ed25519PublicKey.length, 32);
    assert.equal(stored!.x25519PublicKey.length, 32);
  });

  it("a second instance loads the same identity rather than generating a new one", async () => {
    const prekeys = new InMemoryPreKeyStore();
    const a = new SignalProtocol({ preKeyStore: prekeys });
    await a.ready();
    await a.flushPendingWrites();
    const aPub = Buffer.from(a.getPublicKey()).toString("hex");
    const aXPub = Buffer.from(a.getX25519PublicKey()).toString("hex");

    const b = new SignalProtocol({ preKeyStore: prekeys });
    await b.ready();
    assert.equal(Buffer.from(b.getPublicKey()).toString("hex"), aPub);
    assert.equal(Buffer.from(b.getX25519PublicKey()).toString("hex"), aXPub);
  });
});

describe("PreKeyStore — SPK history persistence", () => {
  it("restored instance reuses the active SPK rather than rotating immediately", async () => {
    const prekeys = new InMemoryPreKeyStore();
    let svc = new SignalProtocol({ preKeyStore: prekeys });
    const b1 = await svc.generatePreKeyBundle("alice");
    await svc.flushPendingWrites();

    svc = new SignalProtocol({ preKeyStore: prekeys });
    const b2 = await svc.generatePreKeyBundle("alice");
    assert.equal(b1.signedPreKeyId, b2.signedPreKeyId);
    assert.deepEqual(Array.from(b1.signedPreKey), Array.from(b2.signedPreKey));
  });

  it("retained-history entry survives restart and still decrypts late messages", async () => {
    // Force rotation between bundle issuance and message arrival —
    // proves the responder can still complete X3DH against an SPK that
    // is no longer the active one but is within the retained window.
    const prekeys = new InMemoryPreKeyStore();
    const sessions = new InMemorySignalSessionStore();
    let now = new Date(2026, 0, 1).getTime();
    const nowProvider = () => new Date(now);

    const bobBuild = () =>
      new SignalProtocol({
        preKeyStore: prekeys,
        sessionStore: sessions,
        rotationOptions: { rotationIntervalMs: 1000, retainedHistoryCount: 3 },
        nowProvider,
      });

    let bob = bobBuild();
    const oldBundle = await bob.generatePreKeyBundle("bob");
    const alice = new SignalProtocol();
    await alice.generatePreKeyBundle("alice");
    await alice.processPreKeyBundle(oldBundle);
    const ct = await alice.encrypt("bob", new Uint8Array(Buffer.from("late arrival")));

    // Time advances past the rotation interval; Bob rotates on the next
    // bundle call, then is restarted before the late message arrives.
    now += 5000;
    await bob.generatePreKeyBundle("bob");
    await bob.flushPendingWrites();

    bob = bobBuild();
    await bob.ready();
    // History size should be 2 (active + 1 retained).
    assert.equal(bob.signedPreKeyHistoryCount, 2);

    const pt = await bob.decrypt("alice", ct);
    assert.deepEqual(Array.from(pt), Array.from(Buffer.from("late arrival")));
  });

  it("KV-backed SPK history serialises to and from JSON faithfully", async () => {
    const kv = new InMemoryKeyValueStore();
    const prekeys = new KeyValuePreKeyStore(kv);
    let svc = new SignalProtocol({ preKeyStore: prekeys });
    const b1 = await svc.generatePreKeyBundle("alice");
    await svc.flushPendingWrites();

    // Inspect the stored JSON for sanity.
    const raw = await kv.get(KeyValuePreKeyStore.SPK_HISTORY_KEY);
    assert.ok(raw !== null);
    const parsed = JSON.parse(Buffer.from(raw!).toString("utf8"));
    assert.equal(parsed.entries.length, 1);
    assert.equal(parsed.entries[0].id, b1.signedPreKeyId);

    // Restart and verify reuse.
    svc = new SignalProtocol({ preKeyStore: prekeys });
    const b2 = await svc.generatePreKeyBundle("alice");
    assert.equal(b1.signedPreKeyId, b2.signedPreKeyId);
  });
});

describe("PreKeyStore — OPK pool persistence", () => {
  it("issued-but-unconsumed OPKs are restored in the issued state after restart", async () => {
    const prekeys = new InMemoryPreKeyStore();
    let bob = new SignalProtocol({ preKeyStore: prekeys, opkPoolSize: 5 });
    const bundle = await bob.generatePreKeyBundle("bob");
    await bob.flushPendingWrites();
    // Pool layout: 4 available + 1 issued = 5 held.
    assert.equal(bob.getOpkPoolStatus().held, 5);
    assert.equal(bob.getOpkPoolStatus().available, 4);

    bob = new SignalProtocol({ preKeyStore: prekeys, opkPoolSize: 5 });
    await bob.ready();
    assert.equal(bob.getOpkPoolStatus().held, 5);
    assert.equal(bob.getOpkPoolStatus().available, 4);

    // Bob can still complete X3DH against the issued OPK.
    const alice = new SignalProtocol();
    await alice.generatePreKeyBundle("alice");
    await alice.processPreKeyBundle(bundle);
    const m1 = await alice.encrypt("bob", new Uint8Array(Buffer.from("hi")));
    const p1 = await bob.decrypt("alice", m1);
    assert.deepEqual(Array.from(p1), Array.from(Buffer.from("hi")));
  });

  it("OPKs consumed before restart are NOT held after restart", async () => {
    const prekeys = new InMemoryPreKeyStore();
    let bob = new SignalProtocol({
      preKeyStore: prekeys,
      sessionStore: new InMemorySignalSessionStore(),
      opkPoolSize: 5,
    });
    const bundle = await bob.generatePreKeyBundle("bob");

    const alice = new SignalProtocol();
    await alice.generatePreKeyBundle("alice");
    await alice.processPreKeyBundle(bundle);
    const m1 = await alice.encrypt("bob", new Uint8Array([1]));
    await bob.decrypt("alice", m1);
    await bob.flushPendingWrites();

    // Held drops to 4 after consumption.
    assert.equal(bob.getOpkPoolStatus().held, 4);

    bob = new SignalProtocol({
      preKeyStore: prekeys,
      sessionStore: new InMemorySignalSessionStore(),
      opkPoolSize: 5,
    });
    await bob.ready();
    assert.equal(bob.getOpkPoolStatus().held, 4);
    assert.equal(bob.getOpkPoolStatus().available, 4);
  });
});
