/**
 * Unit tests for rankTransports() and PerTransportMetrics.
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/transport_rank.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import {
  PerTransportMetrics,
  rankTransports,
} from "../src/transport/ITransportService.js";
import type { ITransportService } from "../src/transport/ITransportService.js";

// ── StubTransport ─────────────────────────────────────────────────────────────

class StubTransport implements ITransportService {
  readonly name: string;
  isAvailable: boolean;
  readonly maxBandwidthBps: number;
  readonly maxRangeMeters = 100;
  readonly powerCostRelative: number;
  readonly maxConcurrentPeers = 10;
  readonly metrics?: PerTransportMetrics;

  constructor(
    name: string,
    opts: {
      isAvailable?: boolean;
      maxBandwidthBps?: number;
      powerCostRelative?: number;
      metrics?: PerTransportMetrics;
    } = {},
  ) {
    this.name              = name;
    this.isAvailable       = opts.isAvailable       ?? true;
    this.maxBandwidthBps   = opts.maxBandwidthBps   ?? 100_000;
    this.powerCostRelative = opts.powerCostRelative ?? 1;
    this.metrics           = opts.metrics;
  }

  async sendAsync(): Promise<boolean>       { return true; }
  async sendStreamAsync(): Promise<boolean> { return true; }
  isConnected(): boolean                    { return false; }
}

// ── PerTransportMetrics ───────────────────────────────────────────────────────

describe("PerTransportMetrics — initial state", () => {

  it("sampleCount starts at 0", () => {
    const m = new PerTransportMetrics();
    assert.strictEqual(0, m.sampleCount);
  });

  it("ewmaRttMs starts at 200", () => {
    const m = new PerTransportMetrics();
    assert.ok(Math.abs(m.ewmaRttMs - 200.0) < 1e-9);
  });

  it("ewmaLossRate starts at 0.05", () => {
    const m = new PerTransportMetrics();
    assert.ok(Math.abs(m.ewmaLossRate - 0.05) < 1e-9);
  });

  it("ewmaThroughputBps starts at 0", () => {
    const m = new PerTransportMetrics();
    assert.strictEqual(0, m.ewmaThroughputBps);
  });
});

describe("PerTransportMetrics — recordSample", () => {

  it("increments sampleCount on each call", () => {
    const m = new PerTransportMetrics();
    m.recordSample(100, true, 1000);
    assert.strictEqual(1, m.sampleCount);
    m.recordSample(100, true, 1000);
    assert.strictEqual(2, m.sampleCount);
  });

  it("updates RTT EWMA correctly: α=0.2×100 + 0.8×200 = 180", () => {
    const m = new PerTransportMetrics();
    m.recordSample(100, true, 1000);
    assert.ok(
      Math.abs(m.ewmaRttMs - 180.0) < 1e-9,
      `expected 180, got ${m.ewmaRttMs}`,
    );
  });

  it("skips RTT update when rttMs is 0", () => {
    const m = new PerTransportMetrics();
    m.recordSample(0, true, 0);
    assert.ok(Math.abs(m.ewmaRttMs - 200.0) < 1e-9);
  });

  it("raises loss rate on failure: 0.2×1 + 0.8×0.05 = 0.24", () => {
    const m = new PerTransportMetrics();
    m.recordSample(100, false, 0);
    assert.ok(
      Math.abs(m.ewmaLossRate - 0.24) < 1e-9,
      `expected 0.24, got ${m.ewmaLossRate}`,
    );
  });

  it("lowers loss rate on success: 0.2×0 + 0.8×0.05 = 0.04", () => {
    const m = new PerTransportMetrics();
    m.recordSample(100, true, 1000);
    assert.ok(
      Math.abs(m.ewmaLossRate - 0.04) < 1e-9,
      `expected 0.04, got ${m.ewmaLossRate}`,
    );
  });

  it("bootstraps throughput on first successful sample", () => {
    // bytes=1000, rtt=100 ms → tput = 1000×8×1000/100 = 80_000 bps
    const m = new PerTransportMetrics();
    m.recordSample(100, true, 1000);
    assert.ok(
      Math.abs(m.ewmaThroughputBps - 80_000) < 0.01,
      `expected 80000, got ${m.ewmaThroughputBps}`,
    );
  });

  it("blends throughput EWMA on second success: 0.2×160_000 + 0.8×80_000 = 96_000", () => {
    const m = new PerTransportMetrics();
    m.recordSample(100, true, 1000);   // bootstrap: 80_000
    m.recordSample(100, true, 2000);   // 160_000 bps; EWMA → 96_000
    assert.ok(
      Math.abs(m.ewmaThroughputBps - 96_000) < 0.01,
      `expected 96000, got ${m.ewmaThroughputBps}`,
    );
  });

  it("does not update throughput on failure", () => {
    const m = new PerTransportMetrics();
    m.recordSample(100, true,  1000);  // bootstrap 80_000
    m.recordSample(100, false, 0);
    assert.ok(Math.abs(m.ewmaThroughputBps - 80_000) < 0.01);
  });

  it("does not update throughput when rttMs is 0", () => {
    const m = new PerTransportMetrics();
    m.recordSample(0, true, 1000);
    assert.strictEqual(0, m.ewmaThroughputBps);
  });
});

describe("PerTransportMetrics — compositeScore", () => {

  it("returns a positive value with defaults", () => {
    const m = new PerTransportMetrics();
    assert.ok(m.compositeScore(500_000, 1) > 0);
  });

  it("powerCostRelative=0 is clamped to 1", () => {
    const m = new PerTransportMetrics();
    assert.ok(
      Math.abs(m.compositeScore(500_000, 0) - m.compositeScore(500_000, 1)) < 1e-9,
    );
  });

  it("formula with no prior throughput: effective=bandwidth×0.1", () => {
    // effective_bps = 500_000 × 0.1 = 50_000
    // score = (50_000 / 1) × (1 − 0.05) / max(200, 1) = 50_000 × 0.95 / 200 = 237.5
    const m = new PerTransportMetrics();
    const expected = (500_000 * 0.1 / 1) * (1 - 0.05) / 200;
    assert.ok(
      Math.abs(m.compositeScore(500_000, 1) - expected) < 1e-9,
      `expected ${expected}, got ${m.compositeScore(500_000, 1)}`,
    );
  });

  it("higher bandwidth yields higher score", () => {
    const m = new PerTransportMetrics();
    assert.ok(m.compositeScore(1_000_000, 1) > m.compositeScore(100_000, 1));
  });

  it("higher power cost yields lower score", () => {
    const m = new PerTransportMetrics();
    assert.ok(m.compositeScore(500_000, 1) > m.compositeScore(500_000, 10));
  });

  it("improves after many fast lossless samples", () => {
    const m = new PerTransportMetrics();
    const before = m.compositeScore(500_000, 1);
    for (let i = 0; i < 20; i++) {
      m.recordSample(10, true, 5000);
    }
    assert.ok(
      m.compositeScore(500_000, 1) > before,
      "score should improve after fast lossless observations",
    );
  });
});

// ── rankTransports ────────────────────────────────────────────────────────────

describe("rankTransports — filtering", () => {

  it("empty input returns empty array", () => {
    assert.deepStrictEqual([], rankTransports([]));
  });

  it("excludes unavailable transport", () => {
    const t = new StubTransport("ble", { isAvailable: false });
    assert.strictEqual(0, rankTransports([t]).length);
  });

  it("all unavailable returns empty array", () => {
    const ts = [
      new StubTransport("ble",  { isAvailable: false }),
      new StubTransport("wifi", { isAvailable: false }),
    ];
    assert.strictEqual(0, rankTransports(ts).length);
  });

  it("available transport is included", () => {
    const t = new StubTransport("ble");
    const result = rankTransports([t]);
    assert.strictEqual(1, result.length);
    assert.strictEqual(t, result[0].transport);
  });

  it("includes only available transports from a mixed list", () => {
    const a = new StubTransport("avail",   { isAvailable: true });
    const u = new StubTransport("unavail", { isAvailable: false });
    const result = rankTransports([a, u]);
    assert.strictEqual(1, result.length);
    assert.strictEqual("avail", result[0].transport.name);
  });
});

describe("rankTransports — scoring and ordering", () => {

  it("results are sorted by score descending", () => {
    const low  = new StubTransport("low",  { maxBandwidthBps: 10_000,    powerCostRelative: 10 });
    const high = new StubTransport("high", { maxBandwidthBps: 1_000_000, powerCostRelative: 1  });
    const result = rankTransports([low, high]);
    assert.strictEqual(2, result.length);
    assert.ok(result[0].score >= result[1].score);
    assert.strictEqual("high", result[0].transport.name);
  });

  it("static score equals maxBandwidthBps / powerCostRelative when no metrics", () => {
    // power=1, bandwidth=500_000 → score = 500_000
    const t = new StubTransport("wifi", { maxBandwidthBps: 500_000, powerCostRelative: 1 });
    const result = rankTransports([t]);
    assert.ok(
      Math.abs(result[0].score - 500_000) < 0.001,
      `expected 500000, got ${result[0].score}`,
    );
  });

  it("static score clamps powerCostRelative to at least 1", () => {
    // power=0 → clamped to 1; score = 200_000
    const t = new StubTransport("zero-cost", { maxBandwidthBps: 200_000, powerCostRelative: 0 });
    const result = rankTransports([t]);
    assert.ok(
      Math.abs(result[0].score - 200_000) < 0.001,
      `expected 200000, got ${result[0].score}`,
    );
  });

  it("transport with live metrics uses compositeScore path", () => {
    const m = new PerTransportMetrics();
    m.recordSample(50, true, 1000);
    const t = new StubTransport("ble-live", {
      maxBandwidthBps: 100_000,
      powerCostRelative: 2,
      metrics: m,
    });
    const result = rankTransports([t]);
    assert.strictEqual(1, result.length);
    assert.ok(result[0].score > 0);
  });

  it("transport without metrics uses static score path", () => {
    const withMetrics    = new StubTransport("live",   { maxBandwidthBps: 100_000, metrics: new PerTransportMetrics() });
    const withoutMetrics = new StubTransport("static", { maxBandwidthBps: 100_000 });
    // Both should produce a positive score and appear in results
    const result = rankTransports([withMetrics, withoutMetrics]);
    assert.strictEqual(2, result.length);
    for (const r of result) {
      assert.ok(r.score > 0);
    }
  });
});
