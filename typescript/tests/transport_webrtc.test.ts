/**
 * Loopback tests for the real WebRTC P2P transport (werift).
 * SPDX-License-Identifier: MIT
 *
 * Two real WebRtcTransport instances are wired only through an in-process signalling bus —
 * no central server, no STUN — and a direct data channel must negotiate over host candidates
 * and carry bytes. Mirrors the Go `webrtc_test.go` and the C# WebRTC loopback test.
 *
 * Run with: tsx --test typescript/tests/transport_webrtc.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import {
  WebRtcTransport,
  InMemorySignalingBus,
} from "../src/transport/webrtc/index.js";

/** Resolves with the first payload `bob` receives from `alice`, or rejects on timeout. */
function firstPayload(
  transport: WebRtcTransport,
  fromUhid: string,
  timeoutMs: number,
): Promise<Uint8Array> {
  return new Promise<Uint8Array>((resolve, reject) => {
    const timer = setTimeout(
      () => reject(new Error("timed out waiting for bytes over the data channel")),
      timeoutMs,
    );
    transport.onDataReceived = (from, data) => {
      if (from === fromUhid) {
        clearTimeout(timer);
        resolve(new Uint8Array(data));
      }
    };
  });
}

describe("WebRtcTransport — serverless loopback", () => {
  it("two peers negotiate a data channel over host-only ICE and exchange bytes", async () => {
    const bus = new InMemorySignalingBus();
    const hostOnly = []; // empty (not undefined) => host-candidate-only ICE, no network dependency

    const alice = new WebRtcTransport("alice", bus.endpoint("alice"), hostOnly);
    const bob = new WebRtcTransport("bob", bus.endpoint("bob"), hostOnly);

    try {
      const payload = new TextEncoder().encode("hello over a serverless webrtc datachannel");
      const received = firstPayload(bob, "alice", 30_000);

      const ok = await alice.sendAsync("bob", payload);
      assert.equal(ok, true, "alice.sendAsync should report success");

      const got = await received;
      assert.deepEqual(got, payload, "bob must receive the exact bytes alice sent");

      assert.equal(alice.isConnected("bob"), true, "alice should report connected to bob");
      assert.equal(bob.isConnected("alice"), true, "bob should report connected to alice");
    } finally {
      await alice.dispose();
      await bob.dispose();
      bus.close();
    }
  });

  it("exposes the ladder-facing metadata", () => {
    const bus = new InMemorySignalingBus();
    const tr = new WebRtcTransport("x", bus.endpoint("x"), []);
    try {
      assert.equal(tr.name, "WebRTC P2P");
      assert.equal(tr.isAvailable, true);
      assert.equal(tr.maxRangeMeters, 0, "internet range should be 0 (unbounded)");
      assert.ok(tr.maxBandwidthBps > 0);
    } finally {
      void tr.dispose();
      bus.close();
    }
  });
});
