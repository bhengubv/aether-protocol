/**
 * Signed-pre-key rotation with retention window. Verifies that:
 *
 *   - SPK rotation triggers automatically on the next bundle call once
 *     the active SPK is older than the rotation interval.
 *   - {@link SignalProtocol.rotateSignedPreKey} forces an off-cycle
 *     rotation when the host wants control.
 *   - Retained-prior SPKs continue to decrypt slightly-stale PreKey
 *     messages (Signal §3.3 — keys SHOULD be rotated, but messages mid-flight
 *     under the old key must still complete).
 *   - Pruned SPKs (older than the retention window) reject PreKey
 *     messages cleanly.
 *
 * Mirrors the C# unit-suite {@code SignalProtocolService.RotationTests}.
 *
 * SPDX-License-Identifier: MIT
 */
import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import {
  SignalProtocol,
  DEFAULT_SPK_ROTATION_OPTIONS,
} from "../src/security/SignalProtocol.js";

describe("SPK rotation — automatic", () => {
  it("active SPK is reused when the rotation interval has not elapsed", async () => {
    let now = new Date(2026, 0, 1).getTime();
    const svc = new SignalProtocol({
      rotationOptions: { rotationIntervalMs: 60_000, retainedHistoryCount: 3 },
      nowProvider: () => new Date(now),
    });
    const b1 = await svc.generatePreKeyBundle("alice");
    now += 30_000;
    const b2 = await svc.generatePreKeyBundle("alice");
    assert.equal(b1.signedPreKeyId, b2.signedPreKeyId);
    assert.equal(svc.signedPreKeyHistoryCount, 1);
  });

  it("active SPK rotates when the rotation interval has elapsed", async () => {
    let now = new Date(2026, 0, 1).getTime();
    const svc = new SignalProtocol({
      rotationOptions: { rotationIntervalMs: 60_000, retainedHistoryCount: 3 },
      nowProvider: () => new Date(now),
    });
    const b1 = await svc.generatePreKeyBundle("alice");
    now += 60_001;
    const b2 = await svc.generatePreKeyBundle("alice");
    assert.notEqual(b1.signedPreKeyId, b2.signedPreKeyId);
    assert.equal(svc.signedPreKeyHistoryCount, 2);
  });
});

describe("SPK rotation — explicit", () => {
  it("rotateSignedPreKey is a no-op when the interval has not elapsed", async () => {
    let now = new Date(2026, 0, 1).getTime();
    const svc = new SignalProtocol({
      rotationOptions: { rotationIntervalMs: 60_000, retainedHistoryCount: 3 },
      nowProvider: () => new Date(now),
    });
    await svc.generatePreKeyBundle("alice");
    const rotated = await svc.rotateSignedPreKey();
    assert.equal(rotated, false);
    assert.equal(svc.signedPreKeyHistoryCount, 1);
  });

  it("rotateSignedPreKey rotates when the active SPK has aged out", async () => {
    let now = new Date(2026, 0, 1).getTime();
    const svc = new SignalProtocol({
      rotationOptions: { rotationIntervalMs: 60_000, retainedHistoryCount: 3 },
      nowProvider: () => new Date(now),
    });
    const b1 = await svc.generatePreKeyBundle("alice");
    now += 60_001;
    const rotated = await svc.rotateSignedPreKey();
    assert.equal(rotated, true);
    assert.equal(svc.signedPreKeyHistoryCount, 2);
    assert.notEqual(svc.activeSignedPreKeyId, b1.signedPreKeyId);
  });
});

describe("SPK rotation — retention window", () => {
  it("retained-prior SPK still decrypts late PreKey messages", async () => {
    let now = new Date(2026, 0, 1).getTime();
    const bob = new SignalProtocol({
      rotationOptions: { rotationIntervalMs: 60_000, retainedHistoryCount: 3 },
      nowProvider: () => new Date(now),
    });
    const oldBundle = await bob.generatePreKeyBundle("bob");

    // Initiator captures the bundle, encrypts a message, but doesn't
    // deliver it yet.
    const alice = new SignalProtocol();
    await alice.generatePreKeyBundle("alice");
    await alice.processPreKeyBundle(oldBundle);
    const ct = await alice.encrypt("bob", new Uint8Array(Buffer.from("late")));

    // Time advances past the rotation interval; Bob rotates on the
    // next bundle call. The OLD SPK is now retained, not active.
    now += 60_001;
    await bob.generatePreKeyBundle("bob");
    assert.equal(bob.signedPreKeyHistoryCount, 2);

    // The late PreKey message must still decrypt.
    const pt = await bob.decrypt("alice", ct);
    assert.deepEqual(Array.from(pt), Array.from(Buffer.from("late")));
  });

  it("pruned SPK (beyond retention window) rejects PreKey messages", async () => {
    let now = new Date(2026, 0, 1).getTime();
    const bob = new SignalProtocol({
      rotationOptions: { rotationIntervalMs: 60_000, retainedHistoryCount: 1 },
      nowProvider: () => new Date(now),
    });
    const oldBundle = await bob.generatePreKeyBundle("bob");
    const alice = new SignalProtocol();
    await alice.generatePreKeyBundle("alice");
    await alice.processPreKeyBundle(oldBundle);
    const ct = await alice.encrypt("bob", new Uint8Array(Buffer.from("very late")));

    // Two consecutive rotations push the original SPK beyond the
    // retention budget (retainedHistoryCount=1 → keep only 2 total).
    now += 60_001;
    await bob.generatePreKeyBundle("bob");
    now += 60_001;
    await bob.generatePreKeyBundle("bob");

    // History should be 2 (active + 1 retained = the second-most-recent).
    assert.equal(bob.signedPreKeyHistoryCount, 2);

    await assert.rejects(
      () => bob.decrypt("alice", ct),
      /signed pre-key id/i
    );
  });

  it("retained history is bounded by 1 + retainedHistoryCount across many rotations", async () => {
    let now = new Date(2026, 0, 1).getTime();
    const svc = new SignalProtocol({
      rotationOptions: { rotationIntervalMs: 1, retainedHistoryCount: 2 },
      nowProvider: () => new Date(now),
    });
    for (let i = 0; i < 10; i++) {
      now += 10;
      await svc.generatePreKeyBundle("alice");
    }
    // History should never exceed 1 + 2 = 3.
    assert.equal(svc.signedPreKeyHistoryCount, 3);
  });
});

describe("SPK rotation — defaults", () => {
  it("default rotation interval is 7 days, retain 3", () => {
    assert.equal(DEFAULT_SPK_ROTATION_OPTIONS.rotationIntervalMs, 7 * 24 * 60 * 60 * 1000);
    assert.equal(DEFAULT_SPK_ROTATION_OPTIONS.retainedHistoryCount, 3);
  });

  it("constructor rejects non-positive rotation interval", () => {
    assert.throws(
      () =>
        new SignalProtocol({
          rotationOptions: { rotationIntervalMs: 0, retainedHistoryCount: 3 },
        })
    );
    assert.throws(
      () =>
        new SignalProtocol({
          rotationOptions: { rotationIntervalMs: -1, retainedHistoryCount: 3 },
        })
    );
  });

  it("constructor rejects negative retainedHistoryCount", () => {
    assert.throws(
      () =>
        new SignalProtocol({
          rotationOptions: { rotationIntervalMs: 1000, retainedHistoryCount: -1 },
        })
    );
  });
});
