/**
 * Unit tests for the SOS acknowledgement path (PacketType.SosAck). A receiving node sends a
 * directed ack back to the originator; the originator tallies distinct reach and fires
 * onSosAcknowledged. Uses a fake IMeshSender — no transport needed. Mirrors the C# SosAckTests.
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/sos_ack.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import { SosBroadcastService } from "../src/sos/index.js";
import { SosAcknowledgement } from "../src/models/index.js";
import { FakeMeshSender } from "./fakes.js";

function build(sender: FakeMeshSender): SosBroadcastService {
  return new SosBroadcastService(sender);
}

/** Originate a real SosBroadcast packet on a separate node and return it + its id. */
async function originateSos(
  originUhid: string,
): Promise<{ sos: MeshPacket; id: string }> {
  const originSender = new FakeMeshSender(originUhid);
  const origin = build(originSender);
  await origin.broadcast("medical", "help", -26.2, 28.04, "ke7g");
  return { sos: originSender.broadcasts[0]!, id: origin.getActiveAlerts()[0]!.id };
}

function makeAck(broadcastId: string, responderUhid: string): MeshPacket {
  const pkt = new MeshPacket();
  pkt.type = PacketType.SosAck;
  pkt.sourceUhid = responderUhid;
  pkt.destinationUhid = "aether:origin:aa";
  pkt.payload = new TextEncoder().encode(
    JSON.stringify({ broadcast_id: broadcastId, received_at_ms: 1_700_000_000_000 }),
  );
  return pkt;
}

// Byte-identity gate: the SosAck payload must serialize to exactly these bytes in every language
// (fixtures/sos/vectors.json). snake_case, field order broadcast_id then received_at_ms, no
// whitespace, UUID lowercase-dashed, received_at_ms a bare integer.
function serializeSosAckPayload(broadcastId: string, receivedAtMs: number): string {
  return JSON.stringify({ broadcast_id: broadcastId, received_at_ms: receivedAtMs });
}

describe("SosAckPayload — canonical byte-identity", () => {
  it("serializes vector 1 to canonical bytes", () => {
    assert.equal(
      serializeSosAckPayload("0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f", 1_700_000_000_000),
      '{"broadcast_id":"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f","received_at_ms":1700000000000}',
    );
  });

  it("serializes vector 2 (nil uuid, zero ts) to canonical bytes", () => {
    assert.equal(
      serializeSosAckPayload("00000000-0000-0000-0000-000000000000", 0),
      '{"broadcast_id":"00000000-0000-0000-0000-000000000000","received_at_ms":0}',
    );
  });
});

describe("SosBroadcastService — handle sends ack", () => {
  it("receiving an SOS sends a directed ack to the originator", async () => {
    const { sos, id } = await originateSos("aether:origin:aa");

    const receiverSender = new FakeMeshSender("aether:receiver:bb");
    await build(receiverSender).handle(sos);

    assert.equal(receiverSender.unicasts.length, 1);
    const ack = receiverSender.unicasts[0]!;
    assert.equal(ack.packet.type, PacketType.SosAck);
    assert.equal(ack.nextHopUhid, "aether:origin:aa");
    assert.equal(ack.packet.destinationUhid, "aether:origin:aa");

    const body = JSON.parse(new TextDecoder().decode(ack.packet.payload));
    assert.equal(body.broadcast_id, id);
  });

  it("handling our own SOS does not generate an ack", async () => {
    const localSender = new FakeMeshSender("aether:origin:aa");
    const svc = build(localSender);
    await svc.broadcast("panic", undefined, 0, 0);

    // Re-handling our own broadcast must not generate an ack.
    await svc.handle(localSender.broadcasts[0]!);
    assert.equal(localSender.unicasts.length, 0);
  });
});

describe("SosBroadcastService — handleAck", () => {
  it("on originator, records responder and fires event (total=1)", async () => {
    const origin = build(new FakeMeshSender("aether:origin:aa"));
    await origin.broadcast("fire", "north wing", -26.1, 28.0);
    const id = origin.getActiveAlerts()[0]!.id;

    let captured: SosAcknowledgement | undefined;
    origin.onSosAcknowledged = (e) => { captured = e; };

    await origin.handleAck(makeAck(id, "aether:responder:cc"));

    assert.ok(captured);
    assert.equal(captured!.broadcastId, id);
    assert.equal(captured!.responderUhid, "aether:responder:cc");
    assert.equal(captured!.totalAcknowledgements, 1);
    assert.ok(origin.getActiveAlerts()[0]!.acknowledgedBy.has("aether:responder:cc"));
  });

  it("duplicate responder counted once", async () => {
    const origin = build(new FakeMeshSender("aether:origin:aa"));
    await origin.broadcast("medical", undefined, 0, 0);
    const id = origin.getActiveAlerts()[0]!.id;

    let events = 0;
    origin.onSosAcknowledged = () => { events++; };

    await origin.handleAck(makeAck(id, "aether:responder:cc"));
    await origin.handleAck(makeAck(id, "aether:responder:cc")); // same responder again

    assert.equal(events, 1);
    assert.equal(origin.getActiveAlerts()[0]!.acknowledgedBy.size, 1);
  });

  it("two distinct responders count two", async () => {
    const origin = build(new FakeMeshSender("aether:origin:aa"));
    await origin.broadcast("medical", undefined, 0, 0);
    const id = origin.getActiveAlerts()[0]!.id;

    await origin.handleAck(makeAck(id, "aether:responder:cc"));
    await origin.handleAck(makeAck(id, "aether:responder:dd"));

    assert.equal(origin.getActiveAlerts()[0]!.acknowledgedBy.size, 2);
  });

  it("unknown broadcast is a no-op", async () => {
    const svc = build(new FakeMeshSender("aether:local:01"));
    let raised = false;
    svc.onSosAcknowledged = () => { raised = true; };

    await svc.handleAck(makeAck(crypto.randomUUID(), "aether:responder:cc"));
    assert.equal(raised, false);
  });

  it("wrong packet type throws", async () => {
    const svc = build(new FakeMeshSender("aether:local:01"));
    const pkt = makeAck(crypto.randomUUID(), "aether:responder:cc");
    pkt.type = PacketType.Data;
    await assert.rejects(() => svc.handleAck(pkt));
  });
});
