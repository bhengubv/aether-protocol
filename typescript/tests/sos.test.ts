/**
 * Unit tests for the SOS service.
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/sos.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import {
  MAX_SOS_BROADCASTS_PER_HOUR,
  SOS_PRIORITY,
  SOS_TTL,
} from "../src/constants.js";
import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import { SosBroadcastService } from "../src/sos/index.js";
import { FakeMeshSender } from "./fakes.js";

const LOCAL = "local";

function newSvc() {
  const sender = new FakeMeshSender(LOCAL);
  const svc = new SosBroadcastService(sender);
  return { svc, sender };
}

function newSosPacket(source: string, ttl = SOS_TTL): MeshPacket {
  const body = new TextEncoder().encode(
    JSON.stringify({
      broadcast_id: crypto.randomUUID(),
      broadcast_type: "sos",
      message: "help",
      latitude: -33.9,
      longitude: 18.4,
      geohash: null,
    }),
  );
  const pkt = new MeshPacket();
  pkt.type = PacketType.SosBroadcast;
  pkt.sourceUhid = source;
  pkt.destinationUhid = "";
  pkt.ttl = ttl;
  pkt.priority = SOS_PRIORITY;
  pkt.payload = body;
  return pkt;
}

describe("SosBroadcastService — broadcast", () => {
  it("floods and stores alert", async () => {
    const { svc, sender } = newSvc();
    const ok = await svc.broadcast("sos", "help", -33.9, 18.4);
    assert.equal(ok, true);
    assert.equal(sender.broadcasts.length, 1);
    const pkt = sender.broadcasts[0]!;
    assert.equal(pkt.type, PacketType.SosBroadcast);
    assert.equal(pkt.ttl, SOS_TTL);
    assert.equal(pkt.priority, SOS_PRIORITY);
    assert.equal(svc.getActiveAlerts().length, 1);
  });

  it("rate-limited after max", async () => {
    const { svc } = newSvc();
    for (let i = 0; i < MAX_SOS_BROADCASTS_PER_HOUR; i++) {
      assert.equal(await svc.broadcast("sos", "h", 0, 0), true);
    }
    assert.equal(await svc.broadcast("sos", "h", 0, 0), false);
  });

  it("rejects empty broadcast type", async () => {
    const { svc } = newSvc();
    await assert.rejects(() => svc.broadcast("", "help", 0, 0));
  });
});

describe("SosBroadcastService — handle", () => {
  it("drops duplicate packet id", async () => {
    const { svc, sender } = newSvc();
    const pkt = newSosPacket("alice");
    await svc.handle(pkt);
    sender.clear();
    const after = svc.getActiveAlerts().length;

    await svc.handle(pkt);
    assert.equal(sender.broadcasts.length, 0);
    assert.equal(svc.getActiveAlerts().length, after);
  });

  it("ignores self-originated", async () => {
    const { svc, sender } = newSvc();
    const pkt = newSosPacket(LOCAL);
    await svc.handle(pkt);
    assert.equal(sender.broadcasts.length, 0);
  });

  it("raises onSosReceived", async () => {
    const { svc } = newSvc();
    let observed: any;
    svc.onSosReceived = (a) => { observed = a; };

    await svc.handle(newSosPacket("alice"));
    assert.ok(observed);
    assert.equal(observed.senderUhid, "alice");
  });

  it("rebroadcasts when TTL allows", async () => {
    const { svc, sender } = newSvc();
    await svc.handle(newSosPacket("alice", 5));
    assert.equal(sender.broadcasts.length, 1);
    assert.equal(sender.broadcasts[0]!.ttl, 4);
  });

  it("does not rebroadcast when TTL exhausted", async () => {
    const { svc, sender } = newSvc();
    await svc.handle(newSosPacket("alice", 1));
    assert.equal(sender.broadcasts.length, 0);
  });

  it("rejects wrong packet type", async () => {
    const { svc } = newSvc();
    const pkt = new MeshPacket();
    pkt.type = PacketType.Data;
    pkt.sourceUhid = "alice";
    await assert.rejects(() => svc.handle(pkt));
  });
});

describe("SosBroadcastService — resolve", () => {
  it("removes alert and fires callback", async () => {
    const { svc } = newSvc();
    let resolved: string | undefined;
    svc.onSosResolved = (id) => { resolved = id; };

    await svc.broadcast("sos", "h", 0, 0);
    const alert = svc.getActiveAlerts()[0]!;
    svc.resolve(alert.id);
    assert.equal(svc.getActiveAlerts().length, 0);
    assert.equal(resolved, alert.id);
  });

  it("unknown id is no-op", async () => {
    const { svc } = newSvc();
    let called = false;
    svc.onSosResolved = () => { called = true; };
    svc.resolve(crypto.randomUUID());
    assert.equal(called, false);
  });
});
