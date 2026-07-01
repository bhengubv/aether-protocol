// SPDX-License-Identifier: MIT
//
// Behavioural proof of the native circuit-relay-v2 ENGINE: a three-node topology
// where A and B can each reach relay R but NOT each other directly. A message from
// A must traverse the relay bridge to reach B — server off, no libp2p. Mirrors the
// Go transport_test.go (in-process 3-node mesh + 6 behavioural cases) and the C#
// CircuitRelayBridgeTests.

import test from "node:test";
import assert from "node:assert/strict";

import {
  Transport,
  RelayLink,
  RelayOptions,
  defaultRelayOptions,
} from "../src/circuitrelay/Transport.js";

// ── in-process one-hop mesh ──────────────────────────────────────────────────

class Mesh {
  private readonly edges = new Set<string>();
  private readonly links = new Map<string, ProcLink>();

  connect(x: string, y: string): void {
    this.edges.add(x + "|" + y);
    this.edges.add(y + "|" + x);
  }

  adjacent(x: string, y: string): boolean {
    return this.edges.has(x + "|" + y);
  }

  link(node: string): ProcLink {
    let l = this.links.get(node);
    if (l === undefined) {
      l = new ProcLink(this, node);
      this.links.set(node, l);
    }
    return l;
  }

  deliver(from: string, to: string, frame: Uint8Array): void {
    if (!this.adjacent(from, to)) return;
    const l = this.link(to);
    // async hop, like a real transport — avoids re-entrant recursion.
    setTimeout(() => l.dispatch(from, frame), 0);
  }
}

class ProcLink implements RelayLink {
  private handler: ((from: string, frame: Uint8Array) => void) | null = null;

  constructor(
    private readonly mesh: Mesh,
    private readonly node: string,
  ) {}

  sendFrame(node: string, frame: Uint8Array): boolean {
    if (!this.mesh.adjacent(this.node, node)) return false;
    this.mesh.deliver(this.node, node, frame);
    return true;
  }

  canReach(node: string): boolean {
    return this.mesh.adjacent(this.node, node);
  }

  onFrame(handler: (from: string, frame: Uint8Array) => void): void {
    this.handler = handler;
  }

  dispatch(from: string, frame: Uint8Array): void {
    this.handler?.(from, frame);
  }
}

// ── controllable clock ───────────────────────────────────────────────────────

class TestClock {
  // 2026-01-01T00:00:00Z, in epoch ms.
  private t = Date.UTC(2026, 0, 1, 0, 0, 0, 0);
  now = (): number => this.t;
  advance(ms: number): void {
    this.t += ms;
  }
}

// ── receipt helper: resolve with the next delivered message, or reject on timeout ─

interface Recv {
  sender: string;
  data: string;
}

/** A per-transport receipt queue with a promise+timeout awaiter, mirroring the Go `chan recv`. */
class RecvQueue {
  private readonly buffer: Recv[] = [];
  private waiter: ((r: Recv) => void) | null = null;

  push = (sender: string, data: Uint8Array): void => {
    const r: Recv = { sender, data: new TextDecoder().decode(data) };
    if (this.waiter) {
      const w = this.waiter;
      this.waiter = null;
      w(r);
    } else {
      this.buffer.push(r);
    }
  };

  /** Await the next message; reject after `timeoutMs`. */
  next(what: string, timeoutMs = 3000): Promise<Recv> {
    const queued = this.buffer.shift();
    if (queued !== undefined) return Promise.resolve(queued);
    return new Promise<Recv>((resolve, reject) => {
      const timer = setTimeout(() => {
        this.waiter = null;
        reject(new Error(`timeout waiting for ${what}`));
      }, timeoutMs);
      this.waiter = (r) => {
        clearTimeout(timer);
        resolve(r);
      };
    });
  }

  /** Assert NO message arrives within `windowMs`. */
  async expectNone(windowMs: number): Promise<void> {
    await sleep(windowMs);
    const got = this.buffer.shift();
    if (got !== undefined) {
      throw new Error(`unexpected message: {${got.sender} ${got.data}}`);
    }
  }
}

function sleep(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms));
}

interface Line {
  a: Transport;
  r: Transport;
  b: Transport;
  bRecv: RecvQueue;
  aRecv: RecvQueue;
}

// buildLine wires A ── R ── B with NO A-B edge. relayOpts/relayClock configure R.
function buildLine(relayOpts: RelayOptions, relayClock?: () => number): Line {
  const m = new Mesh();
  m.connect("A", "R");
  m.connect("R", "B");
  const a = new Transport("A", m.link("A"), defaultRelayOptions());
  const r = new Transport("R", m.link("R"), relayOpts, relayClock);
  const b = new Transport("B", m.link("B"), defaultRelayOptions());
  const bRecv = new RecvQueue();
  const aRecv = new RecvQueue();
  b.setOnData(bRecv.push);
  a.setOnData(aRecv.push);
  return { a, r, b, bRecv, aRecv };
}

// (a) A->R->B relay, B receives; R activeBridgeCount == 1.
test("engine: message traverses relay with no direct link", async () => {
  const { a, r, b, bRecv } = buildLine(defaultRelayOptions());

  assert.equal(a.isConnected("B"), false, "A should not be directly connected to B");
  assert.equal(await b.reserve("R"), true, "B.reserve(R) failed");
  a.setRoute("B", "R");

  assert.equal(await a.send("B", new TextEncoder().encode("deadbeef")), true, "A.send returned false");

  const got = await bRecv.next("B receiving relayed message");
  assert.equal(got.sender, "A");
  assert.equal(got.data, "deadbeef");
  assert.equal(r.activeBridgeCount(), 1, "relay bridge count should be 1");
});

// (b) bidirectional.
test("engine: bridge is bidirectional", async () => {
  const { a, b, bRecv, aRecv } = buildLine(defaultRelayOptions());
  assert.equal(await b.reserve("R"), true, "reserve failed");
  a.setRoute("B", "R");
  assert.equal(await a.send("B", new TextEncoder().encode("hi")), true, "A.send failed");
  await bRecv.next("B receiving");

  assert.equal(await b.send("A", new TextEncoder().encode("reply")), true, "B.send(A) failed");
  const got = await aRecv.next("A receiving B's reply");
  assert.equal(got.sender, "B");
  assert.equal(got.data, "reply");
});

// (c) connect refused without reservation.
test("engine: connect refused without reservation", async () => {
  const { a, r, bRecv } = buildLine(defaultRelayOptions());
  a.setRoute("B", "R"); // route known, but B never reserved
  assert.equal(await a.send("B", new TextEncoder().encode("x")), false, "A.send should fail without a reservation");
  await bRecv.expectNone(200);
  assert.equal(r.activeBridgeCount(), 0, "relay bridge count should be 0");
});

// (d) send fails with no route.
test("engine: send fails without route", async () => {
  const { a, b } = buildLine(defaultRelayOptions());
  assert.equal(await b.reserve("R"), true, "reserve failed");
  // no setRoute
  assert.equal(await a.send("B", new TextEncoder().encode("x")), false, "A.send should fail with no relay route known");
});

// (e) data budget 10 -> first 5B delivered, second 8B (cum 13) dropped + bridge torn down.
test("engine: relay enforces data budget", async () => {
  const opts = defaultRelayOptions();
  opts.bridgeDataLimitBytes = 10;
  const { a, r, b, bRecv } = buildLine(opts);
  assert.equal(await b.reserve("R"), true, "reserve failed");
  a.setRoute("B", "R");

  assert.equal(await a.send("B", new Uint8Array([1, 2, 3, 4, 5])), true, "first send failed"); // 5 bytes, within 10
  await bRecv.next("first (in-budget) message");

  await a.send("B", new Uint8Array([6, 7, 8, 9, 10, 11, 12, 13])); // 8 more -> 13 > 10 -> torn down
  await bRecv.expectNone(300);
  assert.equal(r.activeBridgeCount(), 0, "bridge should be torn down on budget breach");
});

// (f) reservation expiry via injectable clock.
test("engine: reservation expiry refuses connect", async () => {
  const clk = new TestClock();
  const opts = defaultRelayOptions();
  opts.reservationTtlMs = 30 * 60 * 1000; // 30 minutes
  const { a, b, bRecv } = buildLine(opts, clk.now);

  assert.equal(await b.reserve("R"), true, "reserve failed");
  a.setRoute("B", "R");

  clk.advance(31 * 60 * 1000); // past the reservation TTL on R's clock

  assert.equal(await a.send("B", new TextEncoder().encode("x")), false, "A.send should fail after reservation expiry");
  await bRecv.expectNone(200);
});
