/**
 * Unit tests for the extensibility no-op providers:
 *   NoopIncentiveProvider, NoopBackendClient, NoopFeatureFlagProvider.
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/extensibility.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import {
  NoopIncentiveProvider,
  NoopBackendClient,
  NoopFeatureFlagProvider,
} from "../src/extensibility.js";
import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { newDtnBundle } from "../src/models/index.js";
import type { DtnBundle, SosAlert } from "../src/models/index.js";

// ── Helpers ───────────────────────────────────────────────────────────────────

function makePacket(from = "alice"): MeshPacket {
  const p = new MeshPacket();
  p.sourceUhid = from;
  return p;
}

function makeDtnBundle(): DtnBundle {
  return newDtnBundle("alice", "bob", new Uint8Array([1, 2, 3]));
}

function makeSosAlert(): SosAlert {
  return {
    id: crypto.randomUUID(),
    senderUhid: "alice",
    broadcastType: "emergency",
    message: "help",
    latitude: -26.2,
    longitude: 28.04,
    receivedAt: new Date(),
  };
}

// ── NoopIncentiveProvider ─────────────────────────────────────────────────────

describe("NoopIncentiveProvider — recordRelay", () => {
  it("returns void without throwing", async () => {
    const p = new NoopIncentiveProvider();
    await assert.doesNotReject(() => p.recordRelay("alice", makePacket()));
  });

  it("can be called many times without error", async () => {
    const p = new NoopIncentiveProvider();
    for (let i = 0; i < 10; i++) {
      await p.recordRelay(`node-${i}`, makePacket(`node-${i}`));
    }
  });
});

describe("NoopIncentiveProvider — shouldPrioritize", () => {
  it("returns false for any packet", async () => {
    const p = new NoopIncentiveProvider();
    const result = await p.shouldPrioritize(makePacket("alice"));
    assert.equal(result, false);
  });

  it("returns false for multiple different packets", async () => {
    const p = new NoopIncentiveProvider();
    const senders = ["alice", "bob", "carol", "dave"];
    for (const sender of senders) {
      const result = await p.shouldPrioritize(makePacket(sender));
      assert.equal(result, false, `expected false for sender=${sender}`);
    }
  });
});

// ── NoopBackendClient ─────────────────────────────────────────────────────────

describe("NoopBackendClient — relayMessage", () => {
  it("returns false without throwing", async () => {
    const c = new NoopBackendClient();
    const result = await c.relayMessage("alice", "bob", new Uint8Array([1, 2, 3]), 0);
    assert.equal(result, false);
  });

  it("returns false for empty encrypted content", async () => {
    const c = new NoopBackendClient();
    const result = await c.relayMessage("a", "b", new Uint8Array(0), 1);
    assert.equal(result, false);
  });

  it("returns false regardless of priority value", async () => {
    const c = new NoopBackendClient();
    for (const pri of [0, 1, 5, 100]) {
      const result = await c.relayMessage("a", "b", new Uint8Array([1]), pri);
      assert.equal(result, false, `expected false for priority=${pri}`);
    }
  });
});

describe("NoopBackendClient — syncDtnBundle", () => {
  it("returns false without throwing", async () => {
    const c = new NoopBackendClient();
    const result = await c.syncDtnBundle(makeDtnBundle());
    assert.equal(result, false);
  });

  it("can be called multiple times without accumulating state", async () => {
    const c = new NoopBackendClient();
    const r1 = await c.syncDtnBundle(makeDtnBundle());
    const r2 = await c.syncDtnBundle(makeDtnBundle());
    assert.equal(r1, false);
    assert.equal(r2, false);
  });
});

describe("NoopBackendClient — syncSos", () => {
  it("returns false without throwing", async () => {
    const c = new NoopBackendClient();
    const result = await c.syncSos(makeSosAlert());
    assert.equal(result, false);
  });

  it("returns false for multiple different alerts", async () => {
    const c = new NoopBackendClient();
    for (let i = 0; i < 5; i++) {
      const result = await c.syncSos(makeSosAlert());
      assert.equal(result, false, `expected false on call #${i}`);
    }
  });
});

// ── NoopFeatureFlagProvider ───────────────────────────────────────────────────

describe("NoopFeatureFlagProvider — isEnabled", () => {
  it("returns TRUE (no-op enables all features by default)", async () => {
    const f = new NoopFeatureFlagProvider();
    const result = await f.isEnabled("any-feature");
    assert.equal(result, true);
  });

  it("returns true for every known feature flag name", async () => {
    const f = new NoopFeatureFlagProvider();
    const flags = [
      "rlnc",
      "dtn",
      "voice",
      "video",
      "watch-together",
      "group-voice",
      "sos",
      "FEATURE_UNDER_DEVELOPMENT",
    ];
    for (const flag of flags) {
      const result = await f.isEnabled(flag);
      assert.equal(result, true, `expected true for flag="${flag}"`);
    }
  });

  it("returns true for an empty feature name", async () => {
    const f = new NoopFeatureFlagProvider();
    const result = await f.isEnabled("");
    assert.equal(result, true);
  });
});

// ── Cross-provider contract ───────────────────────────────────────────────────

describe("Noop providers — default constructor", () => {
  it("all three noop classes instantiate without arguments", () => {
    assert.doesNotThrow(() => new NoopIncentiveProvider());
    assert.doesNotThrow(() => new NoopBackendClient());
    assert.doesNotThrow(() => new NoopFeatureFlagProvider());
  });
});
