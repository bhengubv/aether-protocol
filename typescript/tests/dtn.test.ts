/**
 * Unit tests for the DTN service.
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/dtn.test.ts
 */

import { describe, it, test } from "node:test";
import { strict as assert } from "node:assert";

import { DTN_MAX_BUNDLES_PER_NODE, DTN_BUNDLE_TTL_HOURS } from "../src/constants.js";
import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import {
  BundlePriority,
  BundleStatus,
  DtnBundle,
  NodeCapabilities,
  newDtnBundle,
} from "../src/models/index.js";
import { DtnService, InMemoryDtnBundleStore } from "../src/dtn/index.js";
import { NodeReputationService } from "../src/reputation.js";
import { FakeMeshSender } from "./fakes.js";

const LOCAL = "local";

function newSvc() {
  const sender = new FakeMeshSender(LOCAL);
  const store = new InMemoryDtnBundleStore();
  const svc = new DtnService(sender, store);
  return { svc, sender, store };
}

function buildBundlePacket(source: string, bundle: DtnBundle): MeshPacket {
  const obj = {
    id: bundle.id,
    sender_uhid: bundle.senderUhid,
    recipient_uhid: bundle.recipientUhid,
    encrypted_payload: Array.from(bundle.encryptedPayload),
    priority: bundle.priority,
    status: bundle.status,
    copy_count: bundle.copyCount,
    max_copies: bundle.maxCopies,
    sender_geohash: bundle.senderGeohash ?? null,
    recipient_last_geohash: bundle.recipientLastGeohash ?? null,
    hop_count: bundle.hopCount,
    created_at_ms: bundle.createdAt.getTime(),
    expires_at_ms: bundle.expiresAt.getTime(),
  };
  const pkt = new MeshPacket();
  pkt.type = PacketType.DtnBundle;
  pkt.sourceUhid = source;
  pkt.destinationUhid = bundle.recipientUhid;
  pkt.payload = new TextEncoder().encode(JSON.stringify(obj));
  return pkt;
}

describe("DtnService — CreateBundle", () => {
  it("persists bundle and attempts delivery", async () => {
    const { svc, store } = newSvc();
    const bundle = await svc.createBundle("recipient", new Uint8Array([1, 2, 3]));
    assert.equal(bundle.recipientUhid, "recipient");
    assert.equal(bundle.status, BundleStatus.Pending);
    assert.equal((await store.getActive()).length, 1);
  });

  it("with direct peer, delivers immediately", async () => {
    const { svc, sender } = newSvc();
    sender.addPeer({
      uhid: "recipient",
      publicKey: new Uint8Array(),
      lastSeen: new Date(),
      reliabilityScore: 50,
      capabilities: NodeCapabilities.DtnCarrier,
    });
    const bundle = await svc.createBundle("recipient", new Uint8Array([1, 2, 3]));
    assert.equal(bundle.status, BundleStatus.Delivered);
    const hit = sender.unicasts.some(
      (u) => u.nextHopUhid === "recipient" && u.packet.type === PacketType.DtnBundle,
    );
    assert.ok(hit);
  });
});

describe("DtnService — HandleAsync DtnBundle", () => {
  it("as recipient, marks delivered and sends receipt", async () => {
    const { svc, sender, store } = newSvc();
    const bundle = newDtnBundle("alice", LOCAL, new Uint8Array([9]));
    await svc.handle(buildBundlePacket("alice", bundle));
    const stored = await store.get(bundle.id);
    assert.ok(stored);
    assert.equal(stored!.status, BundleStatus.Delivered);
    const hit = sender.unicasts.some(
      (u) => u.packet.type === PacketType.DtnDeliveryReceipt && u.nextHopUhid === "alice",
    );
    assert.ok(hit, "expected delivery receipt to alice");
  });

  it("not recipient with capacity, accepts custody", async () => {
    const { svc, sender, store } = newSvc();
    const bundle = newDtnBundle("alice", "bob", new Uint8Array([1]));
    await svc.handle(buildBundlePacket("alice", bundle));
    const stored = await store.get(bundle.id);
    assert.equal(stored!.status, BundleStatus.InCustody);
    assert.equal(stored!.hopCount, 1);
    const hit = sender.unicasts.some(
      (u) => u.packet.type === PacketType.DtnCustodyAck && u.nextHopUhid === "alice",
    );
    assert.ok(hit);
  });

  it("at capacity, refuses custody", async () => {
    const { svc, sender, store } = newSvc();
    for (let i = 0; i < DTN_MAX_BUNDLES_PER_NODE; i++) {
      const fill = newDtnBundle("x", "y", new Uint8Array());
      fill.status = BundleStatus.InCustody;
      await store.save(fill);
    }
    sender.unicasts = [];

    const bundle = newDtnBundle("alice", "bob", new Uint8Array());
    await svc.handle(buildBundlePacket("alice", bundle));

    const ack = sender.unicasts.find((u) => u.packet.type === PacketType.DtnCustodyAck);
    assert.ok(ack);
    const body = JSON.parse(new TextDecoder().decode(ack!.packet.payload));
    assert.equal(body.accepted, false);
  });
});

describe("DtnService — HandleAsync DtnCustodyAck", () => {
  it("positive ack increments copy_count", async () => {
    const { svc, store } = newSvc();
    const bundle = await svc.createBundle("recipient", new Uint8Array([1]));
    const initial = bundle.copyCount;

    const body = new TextEncoder().encode(
      JSON.stringify({ bundle_id: bundle.id, accepted: true }),
    );
    const pkt = new MeshPacket();
    pkt.type = PacketType.DtnCustodyAck;
    pkt.sourceUhid = "carrier";
    pkt.destinationUhid = LOCAL;
    pkt.payload = body;
    await svc.handle(pkt);

    const stored = await store.get(bundle.id);
    assert.equal(stored!.copyCount, initial + 1);
  });

  it("negative ack does not increment", async () => {
    const { svc, store } = newSvc();
    const bundle = await svc.createBundle("recipient", new Uint8Array([1]));
    const initial = bundle.copyCount;

    const body = new TextEncoder().encode(
      JSON.stringify({ bundle_id: bundle.id, accepted: false }),
    );
    const pkt = new MeshPacket();
    pkt.type = PacketType.DtnCustodyAck;
    pkt.sourceUhid = "carrier";
    pkt.destinationUhid = LOCAL;
    pkt.payload = body;
    await svc.handle(pkt);

    const stored = await store.get(bundle.id);
    assert.equal(stored!.copyCount, initial);
  });
});

describe("DtnService — HandleAsync DtnDeliveryReceipt", () => {
  it("marks bundle delivered and fires callback", async () => {
    const { svc, store } = newSvc();
    const bundle = await svc.createBundle("recipient", new Uint8Array([1]));

    let observed: any;
    svc.onBundleDelivered = (r) => { observed = r; };

    const body = new TextEncoder().encode(JSON.stringify({
      bundle_id: bundle.id,
      recipient_uhid: "recipient",
      total_hops: 3,
      total_custody_transfers: 2,
      delivered_at_ms: Date.now(),
    }));
    const pkt = new MeshPacket();
    pkt.type = PacketType.DtnDeliveryReceipt;
    pkt.sourceUhid = "recipient";
    pkt.destinationUhid = LOCAL;
    pkt.payload = body;
    await svc.handle(pkt);

    const stored = await store.get(bundle.id);
    assert.equal(stored!.status, BundleStatus.Delivered);
    assert.ok(observed);
    assert.equal(observed.totalHops, 3);
  });
});

describe("DtnService — ExpireStale", () => {
  it("flips status for expired bundles", async () => {
    const { svc, store } = newSvc();
    const expired = newDtnBundle("a", "b", new Uint8Array());
    expired.status = BundleStatus.Pending;
    expired.expiresAt = new Date(Date.now() - 60_000);
    await store.save(expired);

    const fresh = newDtnBundle("a", "b", new Uint8Array());
    fresh.status = BundleStatus.Pending;
    await store.save(fresh);

    const n = await svc.expireStale();
    assert.equal(n, 1);
    const freshAfter = await store.get(fresh.id);
    assert.equal(freshAfter!.status, BundleStatus.Pending);
  });
});

// ── Item 20: DTN reputation hooks ────────────────────────────────────────────

class FakeReputation {
  deliveryCalls: Array<{ uhid: string; roundTripMs: number }> = [];
  refusalCalls: string[] = [];
  recordDeliverySuccess(uhid: string, roundTripMs: number): void {
    this.deliveryCalls.push({ uhid, roundTripMs });
  }
  recordCustodyRefusal(uhid: string): void { this.refusalCalls.push(uhid); }
  recordRreqFloodAttempt(_: string): void {}
  recordReplayAttempt(_: string): void {}
  recordSignatureFailure(_: string): void {}
  recordDeliveryFailure(_: string): void {}
  getReputationScore(_: string): number { return 1.0; }
  getAllScores(): Map<string, number> { return new Map(); }
  applyWeightedDelta(_: string, __: number): void {}
}

test("dtnDeliveryToSelfFiresReputationSuccess", async () => {
  const sender = new FakeMeshSender(LOCAL);
  const store = new InMemoryDtnBundleStore();
  const svc = new DtnService(sender, store);
  const rep = new FakeReputation();
  svc.setReputation(rep as unknown as NodeReputationService);

  // Bundle addressed to LOCAL — must fire recordDeliverySuccess
  const bundle = newDtnBundle("alice", LOCAL, new Uint8Array([1]));
  await svc.handle(buildBundlePacket("alice", bundle));

  assert.equal(rep.deliveryCalls.length, 1);
  assert.equal(rep.deliveryCalls[0]!.uhid, "alice");
  assert.equal(rep.deliveryCalls[0]!.roundTripMs, 0);
});

test("dtnBundleForOtherNodeDoesNotFireReputationSuccess", async () => {
  const sender = new FakeMeshSender(LOCAL);
  const store = new InMemoryDtnBundleStore();
  const svc = new DtnService(sender, store);
  const rep = new FakeReputation();
  svc.setReputation(rep as unknown as NodeReputationService);

  // Bundle addressed to "bob", not LOCAL — must NOT fire reputation
  const bundle = newDtnBundle("alice", "bob", new Uint8Array([2]));
  await svc.handle(buildBundlePacket("alice", bundle));

  assert.equal(rep.deliveryCalls.length, 0);
});

test("dtnCustodyRefusalFiresReputationCustodyRefusal", async () => {
  const sender = new FakeMeshSender(LOCAL);
  const store = new InMemoryDtnBundleStore();
  const svc = new DtnService(sender, store);
  const rep = new FakeReputation();
  svc.setReputation(rep as unknown as NodeReputationService);

  const bundle = await svc.createBundle("recipient", new Uint8Array([3]));

  const body = new TextEncoder().encode(
    JSON.stringify({ bundle_id: bundle.id, accepted: false }),
  );
  const pkt = new MeshPacket();
  pkt.type = PacketType.DtnCustodyAck;
  pkt.sourceUhid = "carrier";
  pkt.destinationUhid = LOCAL;
  pkt.payload = body;
  await svc.handle(pkt);

  assert.equal(rep.refusalCalls.length, 1);
  assert.equal(rep.refusalCalls[0], "carrier");
});

test("dtnReputationNullNoError", async () => {
  const sender = new FakeMeshSender(LOCAL);
  const store = new InMemoryDtnBundleStore();
  const svc = new DtnService(sender, store);
  // No setReputation call — reputation stays null

  // Delivery to self — must not throw
  const bundle = newDtnBundle("alice", LOCAL, new Uint8Array([4]));
  await svc.handle(buildBundlePacket("alice", bundle));

  // Custody refusal — must not throw
  const bundle2 = await svc.createBundle("recipient", new Uint8Array([5]));
  const body = new TextEncoder().encode(
    JSON.stringify({ bundle_id: bundle2.id, accepted: false }),
  );
  const pkt = new MeshPacket();
  pkt.type = PacketType.DtnCustodyAck;
  pkt.sourceUhid = "carrier";
  pkt.destinationUhid = LOCAL;
  pkt.payload = body;
  await svc.handle(pkt);
  // Reaching here without exception is the assertion
});
