/**
 * Unit tests for the IDirectoryService / DirectoryService implementation.
 * SPDX-License-Identifier: MIT
 *
 * Closes Issue #60 — application-layer name -> ContentDescriptor resolver.
 *
 * Run with: tsx --test typescript/tests/directory.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import {
  ContentDescriptor,
  DirectoryEntryAnnouncedEvent,
  DirectoryService,
  NamePublishPayloadWire,
  NameQueryPayloadWire,
  descriptorToWire,
} from "../src/content/index.js";
import { FakeMeshSender } from "./fakes.js";

const LOCAL = "local-node";

function makeDescriptor(name = "lecture-01.opus"): ContentDescriptor {
  return {
    rootHash: "abc123",
    name,
    totalBytes: 1024,
    chunkSizeBytes: 256,
    chunkCount: 4,
    chunkHashes: ["h0", "h1", "h2", "h3"],
    contentType: "audio/opus",
    createdAt: new Date("2026-06-07T10:00:00.000Z").toISOString(),
  };
}

function newSvc() {
  const sender = new FakeMeshSender(LOCAL);
  const svc = new DirectoryService(sender);
  return { svc, sender };
}

// ── Publish ─────────────────────────────────────────────────────────────────

describe("DirectoryService — publish", () => {
  it("stores locally and broadcasts NamePublish", async () => {
    const { svc, sender } = newSvc();
    const desc = makeDescriptor();

    await svc.publish("podcast:abc", desc);

    // Local catalogue holds the entry.
    const names = await svc.listNames();
    assert.deepEqual(names, ["podcast:abc"]);

    // One NamePublish broadcast went out.
    assert.equal(sender.broadcasts.length, 1);
    const pkt = sender.broadcasts[0]!;
    assert.equal(pkt.type, PacketType.NamePublish);
    assert.equal(pkt.sourceUhid, LOCAL);

    // snake_case wire shape sanity check.
    const wire = JSON.parse(new TextDecoder().decode(pkt.payload)) as NamePublishPayloadWire;
    assert.equal(wire.name, "podcast:abc");
    assert.equal(wire.in_response_to_query_id, null);
    assert.equal(wire.descriptor.root_hash, desc.rootHash);
    assert.equal(wire.descriptor.total_bytes, desc.totalBytes);
    assert.equal(wire.descriptor.chunk_size_bytes, desc.chunkSizeBytes);
    assert.equal(wire.descriptor.chunk_count, desc.chunkCount);
    assert.equal(wire.descriptor.content_type, desc.contentType);
    assert.deepEqual(wire.descriptor.chunk_hashes, [...desc.chunkHashes]);
  });
});

// ── Resolve ─────────────────────────────────────────────────────────────────

describe("DirectoryService — resolve", () => {
  it("local-hit returns immediately, does NOT broadcast NameQuery", async () => {
    const { svc, sender } = newSvc();
    const desc = makeDescriptor();
    await svc.publish("podcast:abc", desc);

    // publish() already produced 1 broadcast — clear so we can prove no further
    // packets go out on local-hit resolve.
    sender.broadcasts = [];

    const hit = await svc.resolve("podcast:abc");
    assert.ok(hit, "expected local-hit");
    assert.equal(hit!.rootHash, desc.rootHash);
    assert.equal(sender.broadcasts.length, 0, "local-hit must not broadcast");
  });

  it("timeout returns null", async () => {
    const { svc } = newSvc();

    const t0 = Date.now();
    const result = await svc.resolve("missing:name", 50);
    const elapsed = Date.now() - t0;

    assert.equal(result, null);
    assert.ok(elapsed >= 40, `resolve should have waited near the timeout, got ${elapsed}ms`);
  });

  it("waiting resolve completes when matching NamePublish arrives", async () => {
    const { svc, sender } = newSvc();
    const desc = makeDescriptor();

    // Kick off the resolve — no local catalogue entry, so it'll broadcast NameQuery
    // and wait.
    const pending = svc.resolve("podcast:abc", 2000);

    // Wait a tick so the broadcast actually happens.
    await new Promise((r) => setImmediate(r));

    assert.equal(sender.broadcasts.length, 1);
    const queryPkt = sender.broadcasts[0]!;
    assert.equal(queryPkt.type, PacketType.NameQuery);
    const queryWire = JSON.parse(new TextDecoder().decode(queryPkt.payload)) as NameQueryPayloadWire;
    assert.equal(queryWire.name, "podcast:abc");
    assert.ok(queryWire.query_id, "query_id must be set");

    // Simulate a peer answering: feed back a unicast NamePublish whose
    // in_response_to_query_id matches.
    const responseWire: NamePublishPayloadWire = {
      name: "podcast:abc",
      descriptor: descriptorToWire(desc),
      in_response_to_query_id: queryWire.query_id,
    };
    const response = new MeshPacket();
    response.type = PacketType.NamePublish;
    response.sourceUhid = "peer-bob";
    response.destinationUhid = LOCAL;
    response.payload = new TextEncoder().encode(JSON.stringify(responseWire));
    await svc.handle(response);

    const result = await pending;
    assert.ok(result, "expected query to resolve with descriptor");
    assert.equal(result!.rootHash, desc.rootHash);
    assert.equal(result!.contentType, desc.contentType);
  });
});

// ── handle(NamePublish) ─────────────────────────────────────────────────────

describe("DirectoryService — handle(NamePublish)", () => {
  it("stores in local catalogue and fires onEntryAnnounced", async () => {
    const { svc } = newSvc();
    const desc = makeDescriptor();

    const events: DirectoryEntryAnnouncedEvent[] = [];
    svc.onEntryAnnounced = (e) => { events.push(e); };

    const wire: NamePublishPayloadWire = {
      name: "podcast:abc",
      descriptor: descriptorToWire(desc),
      in_response_to_query_id: null,
    };
    const pkt = new MeshPacket();
    pkt.type = PacketType.NamePublish;
    pkt.sourceUhid = "peer-alice";
    pkt.payload = new TextEncoder().encode(JSON.stringify(wire));
    await svc.handle(pkt);

    // Catalogue updated.
    const names = await svc.listNames();
    assert.deepEqual(names, ["podcast:abc"]);
    const hit = await svc.resolve("podcast:abc");
    assert.ok(hit);
    assert.equal(hit!.rootHash, desc.rootHash);

    // Event fired.
    assert.equal(events.length, 1);
    const e = events[0]!;
    assert.equal(e.name, "podcast:abc");
    assert.equal(e.sourceUhid, "peer-alice");
    assert.equal(e.descriptor.rootHash, desc.rootHash);
    assert.ok(!Number.isNaN(Date.parse(e.announcedAtUtc)));
  });
});

// ── handle(NameQuery) ───────────────────────────────────────────────────────

describe("DirectoryService — handle(NameQuery)", () => {
  it("unicasts NamePublish response when local catalogue holds the name", async () => {
    const { svc, sender } = newSvc();
    const desc = makeDescriptor();
    await svc.publish("podcast:abc", desc);
    sender.broadcasts = []; // clear the publish broadcast
    sender.unicasts = [];

    const queryWire: NameQueryPayloadWire = {
      name: "podcast:abc",
      query_id: "query-12345",
    };
    const queryPkt = new MeshPacket();
    queryPkt.type = PacketType.NameQuery;
    queryPkt.sourceUhid = "peer-bob";
    queryPkt.payload = new TextEncoder().encode(JSON.stringify(queryWire));
    await svc.handle(queryPkt);

    // A unicast NamePublish must have flown back to peer-bob.
    assert.equal(sender.unicasts.length, 1);
    const rec = sender.unicasts[0]!;
    assert.equal(rec.nextHopUhid, "peer-bob");
    assert.equal(rec.packet.type, PacketType.NamePublish);
    assert.equal(rec.packet.destinationUhid, "peer-bob");

    const respWire = JSON.parse(new TextDecoder().decode(rec.packet.payload)) as NamePublishPayloadWire;
    assert.equal(respWire.name, "podcast:abc");
    assert.equal(respWire.in_response_to_query_id, "query-12345");
    assert.equal(respWire.descriptor.root_hash, desc.rootHash);

    // No further broadcast — only unicast back to the querier.
    assert.equal(sender.broadcasts.length, 0);
  });

  it("silently ignores query for unknown name (no response, no error)", async () => {
    const { svc, sender } = newSvc();

    const queryWire: NameQueryPayloadWire = {
      name: "unknown:name",
      query_id: "query-67890",
    };
    const queryPkt = new MeshPacket();
    queryPkt.type = PacketType.NameQuery;
    queryPkt.sourceUhid = "peer-bob";
    queryPkt.payload = new TextEncoder().encode(JSON.stringify(queryWire));
    await svc.handle(queryPkt);

    assert.equal(sender.unicasts.length, 0, "must NOT respond to unknown name");
    assert.equal(sender.broadcasts.length, 0);
  });
});
