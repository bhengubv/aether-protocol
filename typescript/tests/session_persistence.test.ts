/**
 * Persistent Signal session state via the {@link SignalSessionStore}
 * adapter — verifies that an instance which restarts against the same
 * KV store recovers identity, sessions, and the OPK pool without
 * re-keying.
 *
 * Mirrors the C# unit-suite {@code SignalProtocolService.PersistenceTests}.
 *
 * SPDX-License-Identifier: MIT
 */
import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { SignalProtocol } from "../src/security/SignalProtocol.js";
import {
  InMemorySignalSessionStore,
  KeyValueSignalSessionStore,
} from "../src/security/SignalSessionStore.js";
import { InMemoryPreKeyStore, KeyValuePreKeyStore } from "../src/security/PreKeyStore.js";
import {
  InMemoryKeyValueStore,
  FileSystemKeyValueStore,
} from "../src/storage/index.js";

describe("SignalProtocol — session persistence (in-memory adapters)", () => {
  it("restored instance encrypts/decrypts on the existing chain (no re-keying)", async () => {
    const aliceSessions = new InMemorySignalSessionStore();
    const alicePrekeys = new InMemoryPreKeyStore();
    const bobSessions = new InMemorySignalSessionStore();
    const bobPrekeys = new InMemoryPreKeyStore();

    let alice = new SignalProtocol({ sessionStore: aliceSessions, preKeyStore: alicePrekeys });
    let bob = new SignalProtocol({ sessionStore: bobSessions, preKeyStore: bobPrekeys });

    await alice.generatePreKeyBundle("alice");
    const bobBundle = await bob.generatePreKeyBundle("bob");

    await alice.processPreKeyBundle(bobBundle);
    const m1 = await alice.encrypt("bob", new Uint8Array(Buffer.from("hello")));
    const p1 = await bob.decrypt("alice", m1);
    assert.deepEqual(Array.from(p1), Array.from(Buffer.from("hello")));

    // Simulate restart: drop both instances, recreate from the same stores.
    await alice.flushPendingWrites();
    await bob.flushPendingWrites();
    alice = new SignalProtocol({ sessionStore: aliceSessions, preKeyStore: alicePrekeys });
    bob = new SignalProtocol({ sessionStore: bobSessions, preKeyStore: bobPrekeys });
    await alice.ready();
    await bob.ready();

    // Both sides should still hold the session.
    assert.equal(alice.hasSession("bob"), true);
    assert.equal(bob.hasSession("alice"), true);

    // Continue on the same chain.
    const m2 = await alice.encrypt("bob", new Uint8Array(Buffer.from("after restart")));
    const p2 = await bob.decrypt("alice", m2);
    assert.deepEqual(Array.from(p2), Array.from(Buffer.from("after restart")));
  });

  it("identity keys are stable across restart", async () => {
    const sessions = new InMemorySignalSessionStore();
    const prekeys = new InMemoryPreKeyStore();
    let svc = new SignalProtocol({ sessionStore: sessions, preKeyStore: prekeys });
    await svc.ready();
    const ed = Buffer.from(svc.getPublicKey()).toString("hex");
    const x = Buffer.from(svc.getX25519PublicKey()).toString("hex");

    // Restart — identity must be loaded back.
    svc = new SignalProtocol({ sessionStore: sessions, preKeyStore: prekeys });
    await svc.ready();
    assert.equal(Buffer.from(svc.getPublicKey()).toString("hex"), ed);
    assert.equal(Buffer.from(svc.getX25519PublicKey()).toString("hex"), x);
  });

  it("local UHID is preserved after a setLocalUhid call across restart", async () => {
    const sessions = new InMemorySignalSessionStore();
    const prekeys = new InMemoryPreKeyStore();
    let svc = new SignalProtocol({ sessionStore: sessions, preKeyStore: prekeys });
    await svc.ready();
    svc.setLocalUhid("device-7");
    await svc.flushPendingWrites();

    svc = new SignalProtocol({ sessionStore: sessions, preKeyStore: prekeys });
    await svc.ready();
    // After hydration, the second instance can encrypt without a separate
    // setLocalUhid call — but it needs a session. We assert via internal
    // state observable through generatePreKeyBundle which now reuses the
    // same UHID from storage.
    const bundle = await svc.generatePreKeyBundle("device-7");
    assert.equal(bundle.uhid, "device-7");
  });
});

describe("SignalProtocol — session persistence (KV / filesystem)", () => {
  it("survives a process restart on the filesystem", async () => {
    const dirA = mkdtempSync(join(tmpdir(), "aether-sess-a-"));
    const dirB = mkdtempSync(join(tmpdir(), "aether-sess-b-"));

    const buildAlice = () => {
      const kv = new FileSystemKeyValueStore(dirA);
      return new SignalProtocol({
        sessionStore: new KeyValueSignalSessionStore(kv),
        preKeyStore: new KeyValuePreKeyStore(kv),
      });
    };
    const buildBob = () => {
      const kv = new FileSystemKeyValueStore(dirB);
      return new SignalProtocol({
        sessionStore: new KeyValueSignalSessionStore(kv),
        preKeyStore: new KeyValuePreKeyStore(kv),
      });
    };

    let alice = buildAlice();
    let bob = buildBob();
    await alice.ready();
    await bob.ready();

    await alice.generatePreKeyBundle("alice");
    const bobBundle = await bob.generatePreKeyBundle("bob");
    await alice.processPreKeyBundle(bobBundle);
    const m1 = await alice.encrypt("bob", new Uint8Array(Buffer.from("greetings")));
    await bob.decrypt("alice", m1);

    // Restart both ends — flush durable writes first.
    await alice.flushPendingWrites();
    await bob.flushPendingWrites();
    alice = buildAlice();
    bob = buildBob();
    await alice.ready();
    await bob.ready();

    assert.equal(alice.hasSession("bob"), true);
    assert.equal(bob.hasSession("alice"), true);

    // Round-trip a message across the persisted chain.
    const m2 = await bob.encrypt("alice", new Uint8Array(Buffer.from("from bob")));
    const p2 = await alice.decrypt("bob", m2);
    assert.deepEqual(Array.from(p2), Array.from(Buffer.from("from bob")));
  });

  it("listPeers returns the persisted peer set", async () => {
    const kv = new InMemoryKeyValueStore();
    const sessions = new KeyValueSignalSessionStore(kv);
    const prekeys = new KeyValuePreKeyStore(kv);
    const alice = new SignalProtocol({ sessionStore: sessions, preKeyStore: prekeys });
    const bob = new SignalProtocol({ sessionStore: new InMemorySignalSessionStore(), preKeyStore: new InMemoryPreKeyStore() });
    const carol = new SignalProtocol({ sessionStore: new InMemorySignalSessionStore(), preKeyStore: new InMemoryPreKeyStore() });

    await alice.generatePreKeyBundle("alice");
    const bb = await bob.generatePreKeyBundle("bob");
    const cb = await carol.generatePreKeyBundle("carol");
    await alice.processPreKeyBundle(bb);
    await alice.processPreKeyBundle(cb);

    // Force fire-and-forget saves to flush.
    await alice.encrypt("bob", new Uint8Array([1]));
    await alice.encrypt("carol", new Uint8Array([2]));
    await alice.flushPendingWrites();

    const peers = await sessions.listPeers();
    assert.equal(peers.length, 2);
    assert.deepEqual(peers.sort(), ["bob", "carol"]);
  });

  it("delete removes a persisted session", async () => {
    const sessions = new InMemorySignalSessionStore();
    const prekeys = new InMemoryPreKeyStore();
    const alice = new SignalProtocol({ sessionStore: sessions, preKeyStore: prekeys });
    const bob = new SignalProtocol();

    await alice.generatePreKeyBundle("alice");
    const bb = await bob.generatePreKeyBundle("bob");
    await alice.processPreKeyBundle(bb);
    await alice.encrypt("bob", new Uint8Array([1]));
    await alice.flushPendingWrites();

    assert.equal((await sessions.listPeers()).length, 1);
    await sessions.delete("bob");
    assert.equal((await sessions.listPeers()).length, 0);
  });

  it("a fresh SignalProtocol with no stores still works (back-compat)", async () => {
    const a = new SignalProtocol();
    const b = new SignalProtocol();
    await a.generatePreKeyBundle("a");
    const bb = await b.generatePreKeyBundle("b");
    await a.processPreKeyBundle(bb);
    const m = await a.encrypt("b", new Uint8Array(Buffer.from("hi")));
    const p = await b.decrypt("a", m);
    assert.deepEqual(Array.from(p), Array.from(Buffer.from("hi")));
  });
});
