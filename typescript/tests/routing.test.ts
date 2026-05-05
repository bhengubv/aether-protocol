/**
 * Unit tests for the routing service.
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/routing.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { DEFAULT_TTL } from "../src/constants.js";
import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import { RouteEntry } from "../src/models/index.js";
import {
  AcceptAllRouteReplyVerifier,
  IRouteReplyVerifier,
  InMemoryRouteStore,
  RoutingService,
} from "../src/routing/index.js";
import { FakeMeshSender } from "./fakes.js";

const LOCAL = "local-uhid";

function newSvc(verifier: IRouteReplyVerifier = new AcceptAllRouteReplyVerifier()) {
  const sender = new FakeMeshSender(LOCAL);
  const store = new InMemoryRouteStore();
  const svc = new RoutingService(sender, store, verifier);
  return { svc, sender, store };
}

function newRreq(source: string, dest: string, ttl = DEFAULT_TTL): MeshPacket {
  const p = new MeshPacket();
  p.type = PacketType.RouteRequest;
  p.sourceUhid = source;
  p.destinationUhid = dest;
  p.ttl = ttl;
  return p;
}

function newRrep(source: string, dest: string, ttl = DEFAULT_TTL): MeshPacket {
  const p = new MeshPacket();
  p.type = PacketType.RouteReply;
  p.sourceUhid = source;
  p.destinationUhid = dest;
  p.ttl = ttl;
  return p;
}

describe("RoutingService — HandleRouteRequest", () => {
  it("drops duplicate by id", async () => {
    const { svc, sender } = newSvc();
    const rreq = newRreq("alice", "bob");
    await svc.handleRouteRequest(rreq);
    sender.clear();
    await svc.handleRouteRequest(rreq);
    assert.equal(sender.broadcasts.length, 0);
    assert.equal(sender.unicasts.length, 0);
  });

  it("ignores self-originated", async () => {
    const { svc, sender, store } = newSvc();
    const rreq = newRreq(LOCAL, "bob");
    await svc.handleRouteRequest(rreq);
    assert.equal(sender.broadcasts.length, 0);
    assert.equal(sender.unicasts.length, 0);
    assert.equal((await store.getAll()).length, 0);
  });

  it("installs reverse route to source", async () => {
    const { svc, store } = newSvc();
    const rreq = newRreq("alice", "bob");
    await svc.handleRouteRequest(rreq);
    const route = await store.get("alice");
    assert.ok(route);
    assert.equal(route!.nextHopUhid, "alice");
    assert.ok(route!.hopCount >= 1);
  });

  it("as destination, sends RREP back", async () => {
    const { svc, sender } = newSvc();
    const rreq = newRreq("alice", LOCAL);
    await svc.handleRouteRequest(rreq);
    assert.equal(sender.unicasts.length, 1);
    const rec = sender.unicasts[0]!;
    assert.equal(rec.packet.type, PacketType.RouteReply);
    assert.equal(rec.packet.sourceUhid, LOCAL);
    assert.equal(rec.packet.destinationUhid, "alice");
    assert.equal(rec.nextHopUhid, "alice");
  });

  it("with cached route to destination, replies on behalf", async () => {
    const { svc, sender, store } = newSvc();
    const route: RouteEntry = {
      destinationUhid: "carol",
      nextHopUhid: "carol",
      hopCount: 1,
      qualityScore: 50,
      expiresAt: new Date(Date.now() + 5 * 60 * 1000),
    };
    await store.save(route);
    await svc.findRoute("carol");
    sender.clear();

    const rreq = newRreq("alice", "carol");
    await svc.handleRouteRequest(rreq);

    let rrep: MeshPacket | undefined;
    for (const u of sender.unicasts)
      if (u.packet.type === PacketType.RouteReply) { rrep = u.packet; break; }
    if (!rrep) {
      for (const b of sender.broadcasts)
        if (b.type === PacketType.RouteReply) { rrep = b; break; }
    }
    assert.ok(rrep, "expected an RREP");
    assert.equal(rrep!.sourceUhid, "carol");
  });

  it("forwards when TTL allows", async () => {
    const { svc, sender } = newSvc();
    const rreq = newRreq("alice", "carol", 5);
    await svc.handleRouteRequest(rreq);
    assert.equal(sender.broadcasts.length, 1);
    assert.equal(sender.broadcasts[0]!.ttl, 4);
  });

  it("drops when TTL exhausted", async () => {
    const { svc, sender } = newSvc();
    const rreq = newRreq("alice", "carol", 1);
    await svc.handleRouteRequest(rreq);
    assert.equal(sender.broadcasts.length, 0);
    assert.equal(sender.unicasts.length, 0);
  });
});

describe("RoutingService — HandleRouteReply", () => {
  it("installs forward route", async () => {
    const { svc, store } = newSvc();
    const rrep = newRrep("carol", LOCAL);
    await svc.handleRouteReply(rrep);
    const r = await store.get("carol");
    assert.ok(r);
    assert.equal(r!.nextHopUhid, "carol");
  });

  it("rejects when verifier fails", async () => {
    class Rejecting implements IRouteReplyVerifier {
      async verify(): Promise<boolean> { return false; }
    }
    const { svc, store } = newSvc(new Rejecting());
    await svc.handleRouteReply(newRrep("carol", LOCAL));
    assert.equal(await store.get("carol"), null);
  });

  it("forwards toward original requester", async () => {
    const { svc, sender, store } = newSvc();
    const reverse: RouteEntry = {
      destinationUhid: "alice",
      nextHopUhid: "bob",
      hopCount: 2,
      qualityScore: 50,
      expiresAt: new Date(Date.now() + 5 * 60 * 1000),
    };
    await store.save(reverse);
    await svc.findRoute("alice");
    sender.clear();

    const rrep = newRrep("carol", "alice", 4);
    await svc.handleRouteReply(rrep);

    const fwd = sender.unicasts.find(
      (u) => u.packet.type === PacketType.RouteReply && u.nextHopUhid === "bob",
    );
    assert.ok(fwd);
    assert.equal(fwd!.packet.ttl, 3);
  });
});

describe("RoutingService — FindRoute / Prune", () => {
  it("returns cached route without broadcasting", async () => {
    const { svc, sender, store } = newSvc();
    const route: RouteEntry = {
      destinationUhid: "bob",
      nextHopUhid: "bob",
      hopCount: 1,
      qualityScore: 50,
      expiresAt: new Date(Date.now() + 5 * 60 * 1000),
    };
    await store.save(route);
    const r = await svc.findRoute("bob");
    assert.ok(r);
    assert.equal(r!.nextHopUhid, "bob");
    assert.equal(sender.broadcasts.length, 0);
  });

  it("returns null when no peers connected", async () => {
    const { svc } = newSvc();
    const r = await svc.findRoute("bob");
    assert.equal(r, null);
  });

  it("pruneAsync removes expired routes", async () => {
    const { svc, store } = newSvc();
    await store.save({
      destinationUhid: "stale",
      nextHopUhid: "stale",
      hopCount: 1,
      qualityScore: 50,
      expiresAt: new Date(Date.now() - 10_000),
    });
    await store.save({
      destinationUhid: "fresh",
      nextHopUhid: "fresh",
      hopCount: 1,
      qualityScore: 50,
      expiresAt: new Date(Date.now() + 5 * 60 * 1000),
    });
    await svc.findRoute("fresh");
    await svc.prune();
    assert.equal(await store.get("stale"), null);
    assert.ok(await store.get("fresh"));
  });
});
