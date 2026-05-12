/**
 * Unit tests for InProcessTransport.
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/transport_inprocess.test.ts
 */

import { describe, it, beforeEach } from "node:test";
import { strict as assert } from "node:assert";

import { InProcessTransport } from "../src/transport/InProcessTransport.js";

// ── Reset the static network before every test ────────────────────────────────

beforeEach(() => {
  InProcessTransport.resetNetwork();
});

// ── Constructor ───────────────────────────────────────────────────────────────

describe("InProcessTransport — constructor", () => {
  it("creates a node with correct name and defaults", () => {
    const t = new InProcessTransport("alice");
    assert.equal(t.name, "InProcess");
    assert.equal(t.isAvailable, true);
    assert.ok(t.maxBandwidthBps > 0);
  });

  it("throws for an empty UHID", () => {
    assert.throws(() => new InProcessTransport(""));
  });

  it("throws for a whitespace-only UHID", () => {
    assert.throws(() => new InProcessTransport("   "));
  });

  it("throws when the same UHID is registered twice", () => {
    new InProcessTransport("alice");
    assert.throws(
      () => new InProcessTransport("alice"),
      /already registered/
    );
  });

  it("increments activeNodeCount on creation", () => {
    assert.equal(InProcessTransport.activeNodeCount, 0);
    new InProcessTransport("node1");
    assert.equal(InProcessTransport.activeNodeCount, 1);
    new InProcessTransport("node2");
    assert.equal(InProcessTransport.activeNodeCount, 2);
  });
});

// ── isConnected ───────────────────────────────────────────────────────────────

describe("InProcessTransport — isConnected", () => {
  it("returns true for a registered peer", () => {
    const a = new InProcessTransport("alice");
    new InProcessTransport("bob");
    assert.equal(a.isConnected("bob"), true);
  });

  it("returns false for an unregistered peer", () => {
    const a = new InProcessTransport("alice");
    assert.equal(a.isConnected("ghost"), false);
  });

  it("returns false for an empty peer UHID", () => {
    const a = new InProcessTransport("alice");
    assert.equal(a.isConnected(""), false);
  });

  it("returns false after the peer is disposed", () => {
    const a = new InProcessTransport("alice");
    const b = new InProcessTransport("bob");
    assert.equal(a.isConnected("bob"), true);
    b.dispose();
    assert.equal(a.isConnected("bob"), false);
  });

  it("returns false when self is disposed", () => {
    const a = new InProcessTransport("alice");
    new InProcessTransport("bob");
    a.dispose();
    assert.equal(a.isConnected("bob"), false);
  });
});

// ── sendAsync ─────────────────────────────────────────────────────────────────

describe("InProcessTransport — sendAsync delivery", () => {
  it("delivers data to the peer's onDataReceived callback", async () => {
    const a = new InProcessTransport("alice");
    const b = new InProcessTransport("bob");

    let received: Uint8Array | null = null;
    let senderUhid = "";
    b.onDataReceived = (from, data) => {
      senderUhid = from;
      received = new Uint8Array(data);
    };

    const ok = await a.sendAsync("bob", new Uint8Array([0xDE, 0xAD, 0xBE, 0xEF]));
    assert.equal(ok, true);
    assert.ok(received !== null, "onDataReceived must fire");
    assert.deepEqual(received, new Uint8Array([0xDE, 0xAD, 0xBE, 0xEF]));
    assert.equal(senderUhid, "alice");
  });

  it("returns false for an unknown peer", async () => {
    const a = new InProcessTransport("alice");
    const ok = await a.sendAsync("ghost", new Uint8Array([1]));
    assert.equal(ok, false);
  });

  it("returns false for an empty peer UHID", async () => {
    const a = new InProcessTransport("alice");
    const ok = await a.sendAsync("", new Uint8Array([1]));
    assert.equal(ok, false);
  });

  it("returns false when self is disposed", async () => {
    const a = new InProcessTransport("alice");
    new InProcessTransport("bob");
    a.dispose();
    const ok = await a.sendAsync("bob", new Uint8Array([1]));
    assert.equal(ok, false);
  });

  it("returns false when peer is disposed", async () => {
    const a = new InProcessTransport("alice");
    const b = new InProcessTransport("bob");
    b.dispose();
    const ok = await a.sendAsync("bob", new Uint8Array([1]));
    assert.equal(ok, false);
  });

  it("delivers a copy so mutations after send do not affect delivered data", async () => {
    const a = new InProcessTransport("alice");
    const b = new InProcessTransport("bob");

    let received: Uint8Array | null = null;
    b.onDataReceived = (_, data) => { received = new Uint8Array(data); };

    const payload = new Uint8Array([10, 20, 30]);
    await a.sendAsync("bob", payload);

    // Mutate payload after send.
    payload[0] = 0xFF;

    assert.ok(received !== null);
    assert.equal(received[0], 10, "delivered data must not be affected by post-send mutation");
  });
});

// ── sendAsync — metrics ───────────────────────────────────────────────────────

describe("InProcessTransport — metrics", () => {
  it("metrics object is non-null on creation", () => {
    const a = new InProcessTransport("alice");
    assert.ok(a.metrics !== null && a.metrics !== undefined);
  });

  it("sample_count increments after a successful send", async () => {
    const a = new InProcessTransport("alice");
    const b = new InProcessTransport("bob");
    b.onDataReceived = () => {}; // Register callback so send succeeds.

    const before = a.metrics.sampleCount ?? 0;
    await a.sendAsync("bob", new Uint8Array([1, 2, 3]));
    const after = a.metrics.sampleCount ?? 0;
    assert.ok(after > before, "sampleCount must increase after successful send");
  });
});

// ── sendStreamAsync ───────────────────────────────────────────────────────────

describe("InProcessTransport — sendStreamAsync", () => {
  it("delivers streamed data to onDataReceived", async () => {
    const a = new InProcessTransport("alice");
    const b = new InProcessTransport("bob");

    let received: Uint8Array | null = null;
    b.onDataReceived = (_, data) => { received = new Uint8Array(data); };

    const chunks = [new Uint8Array([1, 2]), new Uint8Array([3, 4])];
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        for (const chunk of chunks) controller.enqueue(chunk);
        controller.close();
      },
    });

    const ok = await a.sendStreamAsync("bob", stream);
    assert.equal(ok, true);
    assert.ok(received !== null, "onDataReceived must fire for stream");
    // Combined: [1, 2, 3, 4]
    assert.deepEqual(received, new Uint8Array([1, 2, 3, 4]));
  });

  it("returns false when self is disposed", async () => {
    const a = new InProcessTransport("alice");
    new InProcessTransport("bob");
    a.dispose();

    const stream = new ReadableStream<Uint8Array>({
      start(c) { c.enqueue(new Uint8Array([1])); c.close(); },
    });
    const ok = await a.sendStreamAsync("bob", stream);
    assert.equal(ok, false);
  });
});

// ── dispose ───────────────────────────────────────────────────────────────────

describe("InProcessTransport — dispose", () => {
  it("isAvailable becomes false after dispose", () => {
    const a = new InProcessTransport("alice");
    a.dispose();
    assert.equal(a.isAvailable, false);
  });

  it("decrements activeNodeCount after dispose", () => {
    new InProcessTransport("a");
    const b = new InProcessTransport("b");
    assert.equal(InProcessTransport.activeNodeCount, 2);
    b.dispose();
    assert.equal(InProcessTransport.activeNodeCount, 1);
  });

  it("clears onDataReceived after dispose", () => {
    const a = new InProcessTransport("alice");
    a.onDataReceived = () => {};
    a.dispose();
    assert.equal(a.onDataReceived, undefined);
  });

  it("calling dispose twice is safe (idempotent)", () => {
    const a = new InProcessTransport("alice");
    assert.doesNotThrow(() => {
      a.dispose();
      a.dispose();
    });
  });

  it("UHID can be re-registered after its owner is disposed", () => {
    const a = new InProcessTransport("alice");
    a.dispose();
    // Should not throw because the old registration was removed.
    assert.doesNotThrow(() => {
      const a2 = new InProcessTransport("alice");
      a2.dispose();
    });
  });
});

// ── resetNetwork ──────────────────────────────────────────────────────────────

describe("InProcessTransport — resetNetwork", () => {
  it("sets activeNodeCount to zero", () => {
    new InProcessTransport("node1");
    new InProcessTransport("node2");
    assert.equal(InProcessTransport.activeNodeCount, 2);
    InProcessTransport.resetNetwork();
    assert.equal(InProcessTransport.activeNodeCount, 0);
  });

  it("allows previously-used UHIDs to be re-registered after reset", () => {
    new InProcessTransport("alice");
    InProcessTransport.resetNetwork();
    assert.doesNotThrow(() => {
      const a2 = new InProcessTransport("alice");
      a2.dispose();
    });
  });
});

// ── Multiple nodes ────────────────────────────────────────────────────────────

describe("InProcessTransport — multiple nodes coexist", () => {
  it("many nodes can be registered and communicate pairwise", async () => {
    const nodes = ["n1", "n2", "n3", "n4"].map(
      (id) => new InProcessTransport(id)
    );

    const deliveries: string[] = [];
    for (const node of nodes) {
      const id = node["localUhid" as keyof InProcessTransport] as string;
      node.onDataReceived = (from) => deliveries.push(`${from}->${id}`);
    }

    // n1 sends to all others.
    await nodes[0].sendAsync("n2", new Uint8Array([1]));
    await nodes[0].sendAsync("n3", new Uint8Array([2]));
    await nodes[0].sendAsync("n4", new Uint8Array([3]));

    assert.equal(deliveries.length, 3);
    assert.ok(deliveries.includes("n1->n2"));
    assert.ok(deliveries.includes("n1->n3"));
    assert.ok(deliveries.includes("n1->n4"));
  });
});
