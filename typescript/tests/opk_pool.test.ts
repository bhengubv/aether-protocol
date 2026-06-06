/**
 * One-time pre-key pool tests.
 *
 * The pool replaces the prior single-OPK implementation, which had a
 * concurrency hazard: two initiators racing into generatePreKeyBundle could
 * both stamp the same preKeyId on different bundles, the responder would
 * accept the first PreKey message and reject the second.
 *
 * Mirrors the C# unit-suite in
 * src/AetherNet.Core.Tests/Security/SignalProtocolService.OpkPoolTests.cs.
 *
 * SPDX-License-Identifier: MIT
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import {
  SignalProtocol,
  DEFAULT_OPK_POOL_SIZE,
} from "../src/security/SignalProtocol.js";

describe("OPK pool — construction", () => {
  it("starts empty before the first generatePreKeyBundle", () => {
    const svc = new SignalProtocol();
    const status = svc.getOpkPoolStatus();
    assert.equal(status.held, 0);
    assert.equal(status.available, 0);
  });

  it("default pool size matches the published constant (100)", () => {
    const svc = new SignalProtocol();
    assert.equal(svc.opkPoolSize, DEFAULT_OPK_POOL_SIZE);
    assert.equal(DEFAULT_OPK_POOL_SIZE, 100);
  });

  it("respects custom opkPoolSize", () => {
    const svc = new SignalProtocol({ opkPoolSize: 7 });
    assert.equal(svc.opkPoolSize, 7);
  });

  it("rejects opkPoolSize < 1", () => {
    assert.throws(() => new SignalProtocol({ opkPoolSize: 0 }));
    assert.throws(() => new SignalProtocol({ opkPoolSize: -5 }));
  });

  it("rejects non-integer opkPoolSize", () => {
    assert.throws(() => new SignalProtocol({ opkPoolSize: 1.5 }));
    assert.throws(() => new SignalProtocol({ opkPoolSize: NaN }));
  });
});

describe("OPK pool — bundle generation", () => {
  it("first bundle issues one OPK and leaves opkPoolSize - 1 available (top-up before dequeue)", async () => {
    const svc = new SignalProtocol({ opkPoolSize: 10 });
    const bundle = await svc.generatePreKeyBundle("alice");
    assert.ok(bundle.preKeyId > 0);
    assert.equal(bundle.preKey.length, 32);

    // top-up runs FIRST (ensures availableOpkIds.length >= opkPoolSize),
    // then we dequeue ONE → available = opkPoolSize - 1 = 9. Total held =
    // 10 (9 un-issued + 1 issued).
    const status = svc.getOpkPoolStatus();
    assert.equal(status.held, 10);
    assert.equal(status.available, 9);
  });

  it("issues distinct preKeyIds across many bundles", async () => {
    const svc = new SignalProtocol({ opkPoolSize: 100 });
    const ids = new Set<number>();
    for (let i = 0; i < 50; i++) {
      const bundle = await svc.generatePreKeyBundle("alice");
      assert.ok(!ids.has(bundle.preKeyId), `duplicate preKeyId ${bundle.preKeyId} at i=${i}`);
      ids.add(bundle.preKeyId);
    }
    assert.equal(ids.size, 50);
  });

  it("issued bundles draw distinct preKey public keys", async () => {
    const svc = new SignalProtocol({ opkPoolSize: 50 });
    const seen = new Set<string>();
    for (let i = 0; i < 25; i++) {
      const b = await svc.generatePreKeyBundle("alice");
      const hex = Buffer.from(b.preKey).toString("hex");
      assert.ok(!seen.has(hex), `duplicate OPK pub at i=${i}`);
      seen.add(hex);
    }
  });

  it("reuses the same SignedPreKey across bundles (rotation deferred)", async () => {
    const svc = new SignalProtocol();
    const b1 = await svc.generatePreKeyBundle("alice");
    const b2 = await svc.generatePreKeyBundle("alice");
    assert.equal(b1.signedPreKeyId, b2.signedPreKeyId);
    assert.deepEqual(Array.from(b1.signedPreKey), Array.from(b2.signedPreKey));
  });
});

describe("OPK pool — top-up after consumption", () => {
  it("pool tops back up to opkPoolSize - 1 available after each issue (top-up runs before dequeue)", async () => {
    const svc = new SignalProtocol({ opkPoolSize: 5 });
    for (let i = 0; i < 12; i++) {
      await svc.generatePreKeyBundle("alice");
      // After each call, top-up ran (available -> 5), then dequeue (available -> 4).
      assert.equal(svc.getOpkPoolStatus().available, 4,
        `available drifted at i=${i}`);
    }
  });

  it("processPreKeyBundle followed by responder-side decrypt consumes one OPK", async () => {
    const alice = new SignalProtocol({ opkPoolSize: 5 });
    const bob = new SignalProtocol({ opkPoolSize: 5 });

    const bobBundle1 = await bob.generatePreKeyBundle("bob");
    // Bob's pool now has: 4 available + 1 issued = 5 held.
    assert.equal(bob.getOpkPoolStatus().held, 5);

    await alice.generatePreKeyBundle("alice");
    await alice.processPreKeyBundle(bobBundle1);

    const ct = await alice.encrypt("bob", new Uint8Array(Buffer.from("hi")));
    await bob.decrypt("alice", ct);

    // Bob's responder-side X3DH consumed the issued OPK — the held count
    // drops by 1 (4 still available + 0 issued-but-un-consumed = 4 held).
    assert.equal(bob.getOpkPoolStatus().held, 4);
    assert.equal(bob.getOpkPoolStatus().available, 4);
  });
});

describe("OPK pool — concurrent async init", () => {
  it("two concurrent generatePreKeyBundle calls produce distinct ids", async () => {
    // Single-threaded JS still races across await: without serialisation,
    // two concurrent generatePreKeyBundle calls could both observe the same
    // pool state after their await points and pop the same head id. The
    // opkLock chain MUST prevent this.
    const svc = new SignalProtocol({ opkPoolSize: 50 });
    const results = await Promise.all(
      Array.from({ length: 20 }, () => svc.generatePreKeyBundle("alice"))
    );
    const ids = new Set(results.map((b) => b.preKeyId));
    assert.equal(ids.size, results.length, "concurrent issues must be distinct");

    const pubs = new Set(results.map((b) => Buffer.from(b.preKey).toString("hex")));
    assert.equal(pubs.size, results.length, "concurrent OPK pubs must be distinct");
  });

  it("100 concurrent calls all succeed and respect pool invariants", async () => {
    const svc = new SignalProtocol({ opkPoolSize: 30 });
    const results = await Promise.all(
      Array.from({ length: 100 }, () => svc.generatePreKeyBundle("alice"))
    );
    const ids = new Set(results.map((b) => b.preKeyId));
    assert.equal(ids.size, 100);

    // After all 100 issues, the pool top-up ran on the LAST call so
    // available is opkPoolSize - 1 = 29 (top-up ensures >= 30, then
    // dequeue drops it to 29).
    assert.equal(svc.getOpkPoolStatus().available, 29);
  });
});

describe("OPK pool — OPK1 hazard regression", () => {
  it("two initiators against the SAME bundle still race correctly (one fails — bundle uniqueness is the contract)", async () => {
    // Reuse-of-bundle is by design rejected: the responder consumes the
    // OPK on first decrypt, so the second initiator presenting the same
    // bundle gets a clean "OPK already consumed" rejection. This is NOT
    // the hazard the pool fixes — the hazard is two SEPARATE bundles
    // colliding on the same id.
    const bob = new SignalProtocol({ opkPoolSize: 5 });
    const alice = new SignalProtocol();
    const carol = new SignalProtocol();

    const bobBundle = await bob.generatePreKeyBundle("bob");
    await alice.generatePreKeyBundle("alice");
    await carol.generatePreKeyBundle("carol");
    await alice.processPreKeyBundle(bobBundle);
    await carol.processPreKeyBundle(bobBundle);

    const aMsg = await alice.encrypt("bob", new Uint8Array(Buffer.from("a")));
    await bob.decrypt("alice", aMsg);

    const cMsg = await carol.encrypt("bob", new Uint8Array(Buffer.from("c")));
    // Second consumer of the same bundle MUST be rejected.
    await assert.rejects(() => bob.decrypt("carol", cMsg));
  });

  it("two SEPARATE bundles from the same node have different OPK ids (the hazard)", async () => {
    // This is the specific hazard the pool fixes. Pre-pool, the single
    // shared OPK was rotated on every bundle call, but two concurrent
    // calls could both stamp the same id before either rotated.
    const bob = new SignalProtocol({ opkPoolSize: 10 });
    const b1 = await bob.generatePreKeyBundle("bob");
    const b2 = await bob.generatePreKeyBundle("bob");
    assert.notEqual(b1.preKeyId, b2.preKeyId);
    assert.notDeepEqual(Array.from(b1.preKey), Array.from(b2.preKey));
  });
});
