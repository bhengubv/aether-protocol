/**
 * Unit tests for the Heartbeat service (PacketType.Heartbeat = 10). Uses a fake IMeshSender —
 * no transport needed. Mirrors the C# HeartbeatTests, plus the canonical byte-identity gate
 * from fixtures/heartbeat/vectors.json.
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/heartbeat.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import { HeartbeatService } from "../src/heartbeat/index.js";
import type { PeerLiveness } from "../src/heartbeat/index.js";
import { FakeMeshSender } from "./fakes.js";

const LOCAL = "aether:local:01";

function build(sender: FakeMeshSender): HeartbeatService {
  return new HeartbeatService(sender);
}

/** Canonical Heartbeat payload serialization — MUST be byte-identical across all language ports. */
function serializeHeartbeatPayload(sequence: number, sentAtMs: number): string {
  return JSON.stringify({ sequence, sent_at_ms: sentAtMs });
}

/** Build a real Heartbeat packet from a peer with the canonical payload. */
function heartbeatFrom(source: string, sequence: number, sentAtMs: number): MeshPacket {
  const pkt = new MeshPacket();
  pkt.type = PacketType.Heartbeat;
  pkt.sourceUhid = source;
  pkt.destinationUhid = "*";
  pkt.payload = new TextEncoder().encode(serializeHeartbeatPayload(sequence, sentAtMs));
  return pkt;
}

// ── canonical byte-identity gate ──────────────────────────────────────────────

describe("HeartbeatPayload — canonical byte-identity", () => {
  // Mirrors [InlineData] in HeartbeatTests.SerializesToCanonicalBytes.
  it("serializes vector 1 (basic) to canonical bytes", () => {
    assert.equal(
      serializeHeartbeatPayload(1, 1_700_000_000_000),
      '{"sequence":1,"sent_at_ms":1700000000000}',
    );
  });

  it("serializes vector 2 (zero) to canonical bytes", () => {
    assert.equal(
      serializeHeartbeatPayload(0, 0),
      '{"sequence":0,"sent_at_ms":0}',
    );
  });

  // Cross-language parity: reproduce every vector in fixtures/heartbeat/vectors.json.
  it("reproduces every fixture vector byte-for-byte", () => {
    const vectorsPath = fileURLToPath(
      new URL("../../fixtures/heartbeat/vectors.json", import.meta.url),
    );
    const V = JSON.parse(readFileSync(vectorsPath, "utf8")) as {
      vectors: { name: string; sequence: number; sent_at_ms: number; expected_json: string }[];
    };
    assert.ok(V.vectors.length >= 2, "fixture must carry at least the two reference vectors");
    for (const vec of V.vectors) {
      assert.equal(
        serializeHeartbeatPayload(vec.sequence, vec.sent_at_ms),
        vec.expected_json,
        `canonical bytes for vector "${vec.name}"`,
      );
    }
  });
});

// ── send ──────────────────────────────────────────────────────────────────────

describe("HeartbeatService — send", () => {
  it("broadcasts a heartbeat with incrementing sequence", async () => {
    const sender = new FakeMeshSender(LOCAL);
    const svc = build(sender);

    await svc.sendHeartbeat();
    await svc.sendHeartbeat();

    assert.equal(sender.broadcasts.length, 2);
    for (const p of sender.broadcasts) {
      assert.equal(p.type, PacketType.Heartbeat);
      assert.equal(p.ttl, 1);
      assert.equal(p.sourceUhid, LOCAL);
      assert.equal(p.destinationUhid, "*");
    }

    const first = JSON.parse(new TextDecoder().decode(sender.broadcasts[0]!.payload));
    const second = JSON.parse(new TextDecoder().decode(sender.broadcasts[1]!.payload));
    assert.equal(first.sequence, 1);
    assert.equal(second.sequence, 2);
  });

  it("returns the delivered peer count", async () => {
    const sender = new FakeMeshSender(LOCAL);
    // FakeMeshSender.broadcast returns the connected-peer count.
    sender.addPeer({ uhid: "aether:peer:aa" } as any);
    sender.addPeer({ uhid: "aether:peer:bb" } as any);
    const delivered = await build(sender).sendHeartbeat();
    assert.equal(delivered, 2);
  });
});

// ── handle ──────────────────────────────────────────────────────────────────

describe("HeartbeatService — handle", () => {
  it("records the peer and raises onPeerSeen", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    let seen: PeerLiveness | undefined;
    svc.onPeerSeen = (e) => { seen = e; };

    const ok = await svc.handle(heartbeatFrom("aether:peer:aa", 7, 1_700_000_000_000));

    assert.equal(ok, true);
    assert.ok(seen);
    assert.equal(seen!.uhid, "aether:peer:aa");
    assert.equal(seen!.lastSequence, 7);
    assert.equal(seen!.lastSentAtMs, 1_700_000_000_000);

    const known = svc.getKnownPeers();
    assert.equal(known.length, 1);
    assert.equal(known[0]!.uhid, "aether:peer:aa");
  });

  it("refreshes an existing peer", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    await svc.handle(heartbeatFrom("aether:peer:aa", 1, 1000));
    await svc.handle(heartbeatFrom("aether:peer:aa", 2, 2000));

    const known = svc.getKnownPeers();
    assert.equal(known.length, 1);
    assert.equal(known[0]!.lastSequence, 2);
  });

  it("ignores our own heartbeat", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    const ok = await svc.handle(heartbeatFrom(LOCAL, 1, 1000));
    assert.equal(ok, false);
    assert.equal(svc.getKnownPeers().length, 0);
  });

  it("rejects the wrong packet type", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    const pkt = heartbeatFrom("aether:peer:aa", 1, 1000);
    pkt.type = PacketType.Data;
    assert.equal(await svc.handle(pkt), false);
    assert.equal(svc.getKnownPeers().length, 0);
  });

  it("drops a malformed payload without recording a peer", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    let raised = false;
    svc.onPeerSeen = () => { raised = true; };

    const pkt = new MeshPacket();
    pkt.type = PacketType.Heartbeat;
    pkt.sourceUhid = "aether:peer:aa";
    pkt.payload = new TextEncoder().encode("{not valid json");

    assert.equal(await svc.handle(pkt), false);
    assert.equal(raised, false);
    assert.equal(svc.getKnownPeers().length, 0);
  });
});

// ── getLivePeers ──────────────────────────────────────────────────────────────

describe("HeartbeatService — getLivePeers", () => {
  it("includes a recently-seen peer", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    await svc.handle(heartbeatFrom("aether:peer:aa", 1, 1000));

    // A just-received heartbeat is live within any generous window.
    const live = svc.getLivePeers(3600);
    assert.equal(live.length, 1);
    assert.equal(live[0]!.uhid, "aether:peer:aa");

    // A negative window pushes the recency horizon into the future, so it excludes even a
    // just-seen peer — a deterministic proof the filter filters (no wall-clock race).
    assert.equal(svc.getLivePeers(-1).length, 0);
  });
});
