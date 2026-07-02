/**
 * Unit tests for the Presence WIRE binding (PacketType.PresenceBeacon = 21 / PresenceQuery = 22).
 * Uses a fake IMeshSender — no transport needed. Mirrors the C# PresenceEridAnnounceTests presence
 * cases, plus the canonical byte-identity gate from fixtures/presence/vectors.json.
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/presence.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import {
  PresenceService,
  serializePresenceBeaconPayload,
  serializePresenceQueryPayload,
  type PresenceBeaconPayload,
  type PresenceBeaconReceived,
  type PresenceQueryReceived,
} from "../src/presence/index.js";
import { FakeMeshSender } from "./fakes.js";

// ── canonical byte-identity gate ──────────────────────────────────────────────

describe("Presence payloads — canonical byte-identity", () => {
  // Mirrors Beacon_Available_SerializesToCanonicalBytes.
  it("serializes an available beacon to canonical bytes", () => {
    assert.equal(
      serializePresenceBeaconPayload({
        erid: "3B38HPPFG9JXE37Q",
        geohash: "u4pru",
        capabilities: 73,
        status: 1,
        sentAtMs: 1_700_000_000_000,
      }),
      '{"erid":"3B38HPPFG9JXE37Q","geohash":"u4pru","capabilities":73,"status":1,"sent_at_ms":1700000000000}',
    );
  });

  // Mirrors Beacon_HiddenOffline_SerializesToCanonicalBytes.
  it("serializes a hidden/offline beacon (empty geohash) to canonical bytes", () => {
    assert.equal(
      serializePresenceBeaconPayload({
        erid: "0Z5BD0HB1Q7W76MY",
        geohash: "",
        capabilities: 0,
        status: 5,
        sentAtMs: 0,
      }),
      '{"erid":"0Z5BD0HB1Q7W76MY","geohash":"","capabilities":0,"status":5,"sent_at_ms":0}',
    );
  });

  // Mirrors Query_SerializesToCanonicalBytes.
  it("serializes a query to canonical bytes", () => {
    assert.equal(
      serializePresenceQueryPayload({
        queryId: "11112222-3333-4444-5555-666677778888",
        geohash: "u4pru",
      }),
      '{"query_id":"11112222-3333-4444-5555-666677778888","geohash":"u4pru"}',
    );
  });

  // Cross-language parity: reproduce every vector in fixtures/presence/vectors.json.
  it("reproduces every fixture beacon + query vector byte-for-byte", () => {
    const vectorsPath = fileURLToPath(
      new URL("../../fixtures/presence/vectors.json", import.meta.url),
    );
    const V = JSON.parse(readFileSync(vectorsPath, "utf8")) as {
      beacon_vectors: {
        name: string;
        erid: string;
        geohash: string;
        capabilities: number;
        status: number;
        sent_at_ms: number;
        expected_json: string;
      }[];
      query_vectors: {
        name: string;
        query_id: string;
        geohash: string;
        expected_json: string;
      }[];
    };
    assert.ok(V.beacon_vectors.length >= 2, "fixture must carry at least two beacon vectors");
    assert.ok(V.query_vectors.length >= 1, "fixture must carry at least one query vector");

    for (const vec of V.beacon_vectors) {
      assert.equal(
        serializePresenceBeaconPayload({
          erid: vec.erid,
          geohash: vec.geohash,
          capabilities: vec.capabilities,
          status: vec.status,
          sentAtMs: vec.sent_at_ms,
        }),
        vec.expected_json,
        `canonical bytes for beacon vector "${vec.name}"`,
      );
    }
    for (const vec of V.query_vectors) {
      assert.equal(
        serializePresenceQueryPayload({ queryId: vec.query_id, geohash: vec.geohash }),
        vec.expected_json,
        `canonical bytes for query vector "${vec.name}"`,
      );
    }
  });
});

// ── broadcast + handle ────────────────────────────────────────────────────────

describe("PresenceService — beacon broadcast + handle", () => {
  // Mirrors BroadcastBeacon_EmitsBeaconPacket_AndHandleRaisesEvent.
  it("broadcasts a PresenceBeacon packet and handle raises the received event", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    sender.addPeer({ uhid: "p1" } as never);
    sender.addPeer({ uhid: "p2" } as never);
    sender.addPeer({ uhid: "p3" } as never);
    sender.addPeer({ uhid: "p4" } as never);
    const svc = new PresenceService(sender);

    const beacon: PresenceBeaconPayload = {
      erid: "3B38HPPFG9JXE37Q",
      geohash: "u4pru",
      capabilities: 73,
      status: 1,
      sentAtMs: 1_700_000_000_000,
    };

    assert.equal(await svc.broadcastBeacon(beacon), 4);
    assert.equal(sender.broadcasts.length, 1);
    const sent = sender.broadcasts[0]!;
    assert.equal(sent.type, PacketType.PresenceBeacon);
    assert.equal(sent.sourceUhid, "aether:alice:01");
    assert.equal(sent.destinationUhid, "*");

    let got: PresenceBeaconReceived | undefined;
    svc.onBeaconReceived = (e) => { got = e; };
    sent.sourceUhid = "aether:alice:01";
    assert.equal(await svc.handle(sent), true);
    assert.ok(got);
    assert.equal(got!.beacon.erid, "3B38HPPFG9JXE37Q");
    assert.equal(got!.beacon.geohash, "u4pru");
    assert.equal(got!.beacon.capabilities, 73);
    assert.equal(got!.beacon.status, 1);
    assert.equal(got!.fromUhid, "aether:alice:01");
  });
});

describe("PresenceService — query broadcast + handle", () => {
  // Mirrors Query_EmitsQueryPacket_AndHandleRaisesEvent.
  it("broadcasts a PresenceQuery packet and handle raises the received event", async () => {
    const sender = new FakeMeshSender("aether:bob:02");
    const svc = new PresenceService(sender);

    const qid = await svc.query("u4pru");
    assert.ok(qid);
    // lowercase-dashed UUID, as minted by crypto.randomUUID.
    assert.match(qid, /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);

    assert.equal(sender.broadcasts.length, 1);
    const sent = sender.broadcasts[0]!;
    assert.equal(sent.type, PacketType.PresenceQuery);
    assert.equal(sent.sourceUhid, "aether:bob:02");
    assert.equal(sent.destinationUhid, "*");

    const body = JSON.parse(new TextDecoder().decode(sent.payload));
    assert.equal(body.query_id, qid);
    assert.equal(body.geohash, "u4pru");

    let got: PresenceQueryReceived | undefined;
    svc.onQueryReceived = (e) => { got = e; };
    assert.equal(await svc.handle(sent), true);
    assert.ok(got);
    assert.equal(got!.query.queryId, qid);
    assert.equal(got!.query.geohash, "u4pru");
  });
});

// ── handle: rejection paths ───────────────────────────────────────────────────

describe("PresenceService — handle rejection paths", () => {
  // Mirrors Presence_Handle_WrongType_ReturnsFalse.
  it("rejects the wrong packet type", async () => {
    const svc = new PresenceService(new FakeMeshSender("aether:local:01"));
    const pkt = new MeshPacket();
    pkt.type = PacketType.Data;
    pkt.payload = new Uint8Array(0);
    assert.equal(await svc.handle(pkt), false);
  });

  // Mirrors Presence_Handle_BeaconWithEmptyErid_ReturnsFalse.
  it("rejects a beacon with an empty erid", async () => {
    const svc = new PresenceService(new FakeMeshSender("aether:local:01"));
    const pkt = new MeshPacket();
    pkt.type = PacketType.PresenceBeacon;
    pkt.sourceUhid = "aether:x:01";
    pkt.payload = new TextEncoder().encode(
      serializePresenceBeaconPayload({
        erid: "",
        geohash: "",
        capabilities: 0,
        status: 0,
        sentAtMs: 0,
      }),
    );
    assert.equal(await svc.handle(pkt), false);
  });

  it("drops a malformed beacon payload", async () => {
    const svc = new PresenceService(new FakeMeshSender("aether:local:01"));
    const pkt = new MeshPacket();
    pkt.type = PacketType.PresenceBeacon;
    pkt.sourceUhid = "aether:bob:02";
    pkt.payload = new TextEncoder().encode("{not valid json");
    assert.equal(await svc.handle(pkt), false);
  });
});
