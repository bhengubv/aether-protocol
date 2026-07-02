/**
 * Unit tests for the ERID-announce WIRE binding (PacketType.EridAnnounce = 56). Directed transport
 * of an already-encrypted ERID announcement over the mesh — a fake IMeshSender captures directed
 * sends. Mirrors the C# PresenceEridAnnounceTests EridAnnounce cases, plus a re-pin of the shared
 * EridAnnouncementCodec frame against fixtures/erid/vectors.json (announcement_encode_hex).
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/eridAnnounce.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import { EridAnnounceService } from "../src/eridannounce/index.js";
import { encode } from "../src/identity/EridAnnouncementCodec.js";
import { FakeMeshSender } from "./fakes.js";

const hex = (b: Uint8Array) => Buffer.from(b).toString("hex");

// ── EridAnnounce(56) transport ────────────────────────────────────────────────

describe("EridAnnounceService — send + handle", () => {
  // Mirrors EridAnnounce_Send_EmitsDirectedPacket_AndHandleRaisesEvent.
  it("directed-sends an EridAnnounce packet and handle raises the received event", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    const svc = new EridAnnounceService(sender);
    const enc = new Uint8Array([1, 2, 3, 4, 5]); // opaque Signal-encrypted announcement

    assert.equal(await svc.sendAnnounce("aether:bob:02", enc), true);
    assert.equal(sender.unicasts.length, 1);
    const sent = sender.unicasts[0]!;
    assert.equal(sent.packet.type, PacketType.EridAnnounce);
    assert.equal(sent.nextHopUhid, "aether:bob:02");
    assert.equal(sent.packet.sourceUhid, "aether:alice:01");
    assert.equal(sent.packet.destinationUhid, "aether:bob:02");
    assert.deepEqual(sent.packet.payload, enc);

    let gotBytes: Uint8Array | undefined;
    let gotFrom: string | undefined;
    svc.onAnnounceReceived = (bytes, fromUhid) => {
      gotBytes = bytes;
      gotFrom = fromUhid;
    };
    sent.packet.sourceUhid = "aether:bob:02";
    assert.equal(await svc.handle(sent.packet), true);
    assert.ok(gotBytes);
    assert.deepEqual(gotBytes, enc);
    assert.equal(gotFrom, "aether:bob:02");
  });

  // Mirrors EridAnnounce_Handle_WrongTypeOrEmpty_ReturnsFalse.
  it("rejects the wrong packet type or an empty body", async () => {
    const svc = new EridAnnounceService(new FakeMeshSender("aether:local:01"));

    const wrongType = new MeshPacket();
    wrongType.type = PacketType.Data;
    wrongType.payload = new Uint8Array([1]);
    assert.equal(await svc.handle(wrongType), false);

    const emptyBody = new MeshPacket();
    emptyBody.type = PacketType.EridAnnounce;
    emptyBody.payload = new Uint8Array(0);
    assert.equal(await svc.handle(emptyBody), false);
  });

  it("rejects an empty peer uhid or an empty announcement", async () => {
    const svc = new EridAnnounceService(new FakeMeshSender("aether:alice:01"));
    await assert.rejects(
      () => svc.sendAnnounce("", new Uint8Array([1])),
      /peerUhid must not be empty/,
    );
    await assert.rejects(
      () => svc.sendAnnounce("aether:bob:02", new Uint8Array(0)),
      /encryptedAnnouncement cannot be empty/,
    );
  });
});

// ── shared codec re-pin ───────────────────────────────────────────────────────

describe("EridAnnouncementCodec — canonical frame re-pin", () => {
  // Mirrors EridAnnouncementCodec_MatchesCanonicalFrame — re-pins the shared 8/8 codec against
  // fixtures/erid (routing_key_hex, epoch 900, length 16 -> announcement_encode_hex).
  it("encode(routingKey, 900, 16) reproduces the fixture announcement frame", () => {
    const vectorsPath = fileURLToPath(
      new URL("../../fixtures/erid/vectors.json", import.meta.url),
    );
    const V = JSON.parse(readFileSync(vectorsPath, "utf8")) as {
      routing_key_hex: string;
      announcement_encode_hex: string;
    };
    const routingKey = new Uint8Array(Buffer.from(V.routing_key_hex, "hex"));
    const frame = encode(routingKey, 900, 16);
    assert.equal(hex(frame), V.announcement_encode_hex, "announcement frame");
  });
});
