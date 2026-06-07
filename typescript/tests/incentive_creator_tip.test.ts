/**
 * Unit tests for IncentiveProvider.recordCreatorTip (Issue #61).
 * SPDX-License-Identifier: MIT
 *
 * Distinct from recordRelay (carrier credit) — recordCreatorTip records an
 * end-user-to-author payment.
 *
 * Run with: tsx --test typescript/tests/incentive_creator_tip.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import {
  IncentiveProvider,
  NoopIncentiveProvider,
} from "../src/extensibility.js";
import { MeshPacket } from "../src/protocol/MeshPacket.js";

describe("IncentiveProvider — recordCreatorTip", () => {
  it("default NoopIncentiveProvider resolves without throwing", async () => {
    const provider = new NoopIncentiveProvider();
    // Must complete cleanly — no exception, no rejection.
    await provider.recordCreatorTip("creator-uhid", 100, "content-hash-abc");
    // Reaching this line is the assertion (no throw).
    assert.ok(true);
  });

  it("custom impl receives args verbatim", async () => {
    type Captured = { creatorUhid: string; amount: number; contentHash: string };
    const calls: Captured[] = [];

    class RecordingProvider implements IncentiveProvider {
      async recordRelay(_localUhid: string, _packet: MeshPacket): Promise<void> {}
      async shouldPrioritize(_packet: MeshPacket): Promise<boolean> { return false; }
      async recordCreatorTip(creatorUhid: string, amount: number, contentHash: string): Promise<void> {
        calls.push({ creatorUhid, amount, contentHash });
      }
    }

    const provider = new RecordingProvider();
    await provider.recordCreatorTip("creator-alice", 250, "abc123def456");
    await provider.recordCreatorTip("creator-bob", 50, "999fff");

    assert.equal(calls.length, 2);
    assert.deepEqual(calls[0], {
      creatorUhid: "creator-alice",
      amount: 250,
      contentHash: "abc123def456",
    });
    assert.deepEqual(calls[1], {
      creatorUhid: "creator-bob",
      amount: 50,
      contentHash: "999fff",
    });
  });

  it("tips and relay credit are independent recording paths", async () => {
    const relayCalls: string[] = [];
    const tipCalls: Array<{ creator: string; amount: number; hash: string }> = [];

    class SplitProvider implements IncentiveProvider {
      async recordRelay(localUhid: string, _packet: MeshPacket): Promise<void> {
        relayCalls.push(localUhid);
      }
      async shouldPrioritize(_packet: MeshPacket): Promise<boolean> { return false; }
      async recordCreatorTip(creatorUhid: string, amount: number, contentHash: string): Promise<void> {
        tipCalls.push({ creator: creatorUhid, amount, hash: contentHash });
      }
    }

    const provider = new SplitProvider();
    await provider.recordRelay("local-node", new MeshPacket());
    await provider.recordCreatorTip("author-x", 10, "hash-y");

    assert.equal(relayCalls.length, 1);
    assert.equal(tipCalls.length, 1);
    assert.equal(relayCalls[0], "local-node");
    assert.deepEqual(tipCalls[0], { creator: "author-x", amount: 10, hash: "hash-y" });
  });
});
