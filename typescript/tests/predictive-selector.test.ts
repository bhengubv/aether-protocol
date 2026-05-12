/**
 * Unit tests for PredictiveTransportSelector — Kalman RTT filter and scoring.
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/predictive-selector.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import {
  PredictiveTransportSelector,
} from "../src/transport/PredictiveTransportSelector.js";
import { PerTransportMetrics } from "../src/transport/ITransportService.js";
import type { ITransportService } from "../src/transport/ITransportService.js";

// ── FakeTransport — minimal duck-typed stub ────────────────────────────────────

class FakeTransport implements ITransportService {
  readonly name: string;
  isAvailable: boolean;
  readonly maxBandwidthBps: number;
  readonly maxRangeMeters = 100;
  readonly powerCostRelative: number;
  readonly maxConcurrentPeers = 10;
  readonly metrics: PerTransportMetrics;

  constructor(
    name: string,
    bandwidthBps = 500_000,
    powerCost = 1,
    available = true,
  ) {
    this.name              = name;
    this.maxBandwidthBps   = bandwidthBps;
    this.powerCostRelative = powerCost;
    this.isAvailable       = available;
    this.metrics           = new PerTransportMetrics();
  }

  async sendAsync(): Promise<boolean> { return true; }
  async sendStreamAsync(): Promise<boolean> { return true; }
  isConnected(): boolean { return false; }
}

// ── Kalman filter tests ────────────────────────────────────────────────────────

describe("Kalman RTT filter — indirect via PredictiveTransportSelector", () => {

  it("converges on steady-state RTT after 50 identical samples", () => {
    const sel = new PredictiveTransportSelector();
    const t   = new FakeTransport("t");
    sel.register(t, 200.0);

    for (let i = 0; i < 50; i++) {
      sel.observeMetrics(t, 100, true, 1000);
    }

    const state = sel.getKalmanState(t);
    assert.ok(state !== undefined, "getKalmanState should return a value");
    assert.ok(
      Math.abs(state.rttMs - 100.0) < 5.0,
      `Kalman did not converge: rttMs=${state.rttMs.toFixed(2)}, want ~100`,
    );
  });

  it("decreases posterior variance with each observation", () => {
    const sel = new PredictiveTransportSelector();
    const t   = new FakeTransport("t");
    sel.register(t, 200.0);

    const initial = sel.getKalmanState(t)!.variance;
    for (let i = 0; i < 10; i++) {
      sel.observeMetrics(t, 200, true, 1000);
    }
    const after = sel.getKalmanState(t)!.variance;
    assert.ok(after < initial, `variance ${after} should be < initial ${initial}`);
  });

  it("detects positive drift for rising RTT series", () => {
    const sel = new PredictiveTransportSelector();
    const t   = new FakeTransport("t");
    sel.register(t, 100.0);

    for (let i = 0; i < 10; i++) {
      sel.observeMetrics(t, 100 + (i + 1) * 15, true, 1000);
    }

    const state = sel.getKalmanState(t);
    assert.ok(state !== undefined);
    assert.ok(
      state.driftMs > 0,
      `drift ${state.driftMs.toFixed(4)} should be positive for rising RTT`,
    );
  });
});

// ── PredictiveTransportSelector lifecycle ──────────────────────────────────────

describe("PredictiveTransportSelector — lifecycle and scoring", () => {

  it("ranks fast transport first over slow transport", () => {
    const sel  = new PredictiveTransportSelector();
    const fast = new FakeTransport("fast", 1_000_000, 1,  true);
    const slow = new FakeTransport("slow",    10_000, 10, true);
    sel.register(fast, 50.0);
    sel.register(slow, 150.0);

    // Feed good observations to fast so it has a real EWMA score.
    for (let i = 0; i < 5; i++) {
      sel.observeMetrics(fast, 50, true, 1000);
    }

    const ranked = sel.rank(100);
    assert.strictEqual(ranked.length, 2);
    assert.strictEqual(
      ranked[0].transport.name,
      "fast",
      `expected 'fast' first, got '${ranked[0].transport.name}'`,
    );
  });

  it("excludes unavailable transports from rank()", () => {
    const sel     = new PredictiveTransportSelector();
    const avail   = new FakeTransport("avail",   500_000, 1, true);
    const unavail = new FakeTransport("unavail", 500_000, 1, false);
    sel.register(avail,   100.0);
    sel.register(unavail, 100.0);

    const ranked = sel.rank();
    assert.strictEqual(ranked.length, 1);
    assert.strictEqual(ranked[0].transport.name, "avail");
  });

  it("unregister() removes the transport from ranking", () => {
    const sel = new PredictiveTransportSelector();
    const t   = new FakeTransport("t");
    sel.register(t, 100.0);
    sel.unregister(t);
    assert.strictEqual(sel.rank().length, 0);
  });

  it("selectBest() returns undefined when no transports registered", () => {
    const sel = new PredictiveTransportSelector();
    assert.strictEqual(sel.selectBest(), undefined);
  });

  it("duplicate register() is a no-op (does not double-add)", () => {
    const sel = new PredictiveTransportSelector();
    const t   = new FakeTransport("t");
    sel.register(t, 100.0);
    sel.register(t, 200.0);  // duplicate — should be ignored
    assert.strictEqual(sel.rank().length, 1);
  });

  it("getKalmanState() returns correct initial values after register()", () => {
    const sel = new PredictiveTransportSelector();
    const t   = new FakeTransport("t");
    sel.register(t, 123.0);

    const state = sel.getKalmanState(t);
    assert.ok(state !== undefined);
    assert.ok(Math.abs(state.rttMs - 123.0) < 1e-9);
    assert.ok(Math.abs(state.driftMs) < 1e-9);
    assert.ok(state.variance > 0.0);
  });

  it("getKalmanState() returns undefined for unregistered transport", () => {
    const sel = new PredictiveTransportSelector();
    const t   = new FakeTransport("t");
    assert.strictEqual(sel.getKalmanState(t), undefined);
  });

  it("rank() returns a positive score", () => {
    const sel = new PredictiveTransportSelector();
    const t   = new FakeTransport("t");
    sel.register(t, 100.0);

    const ranked = sel.rank();
    assert.strictEqual(ranked.length, 1);
    assert.ok(ranked[0].score > 0.0);
  });

  it("score improves after good observations", () => {
    const sel = new PredictiveTransportSelector();
    const t   = new FakeTransport("t");
    sel.register(t, 200.0);
    const scoreBefore = sel.rank()[0].score;

    for (let i = 0; i < 10; i++) {
      sel.observeMetrics(t, 20, true, 5000);
    }

    const scoreAfter = sel.rank()[0].score;
    assert.ok(
      scoreAfter > scoreBefore,
      `score should improve after good observations (before=${scoreBefore.toFixed(4)}, after=${scoreAfter.toFixed(4)})`,
    );
  });
});
