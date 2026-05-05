/**
 * Round-trip tests for PacketSerializer.
 * SPDX-License-Identifier: MIT
 *
 * Mirror of swift/Tests/PacketSerializationTests.swift; cross-language byte
 * equivalence is anchored separately under fixtures/.
 *
 * Run with: tsx --test typescript/tests/serializer.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import { PacketSerializer } from "../src/protocol/PacketSerializer.js";

function nonce(fill = 0x00): Uint8Array {
  const out = new Uint8Array(8);
  out.fill(fill);
  return out;
}

describe("PacketSerializer — round-trip", () => {
  it("preserves all fields end-to-end", () => {
    const p = new MeshPacket();
    p.type = PacketType.Data;
    p.sourceUhid = "alice-node";
    p.destinationUhid = "bob-node";
    p.ttl = 7;
    p.priority = 10;
    p.payload = new TextEncoder().encode("Hello, Aether!");
    p.packetNonce = nonce(0xab);
    p.timestampMs = 1710528000000n;

    const got = PacketSerializer.deserialize(PacketSerializer.serialize(p));

    assert.equal(got.type, p.type);
    assert.equal(got.sourceUhid, p.sourceUhid);
    assert.equal(got.destinationUhid, p.destinationUhid);
    assert.equal(got.ttl, p.ttl);
    assert.equal(got.priority, p.priority);
    assert.deepEqual(Array.from(got.payload), Array.from(p.payload));
    assert.deepEqual(Array.from(got.packetNonce), Array.from(p.packetNonce));
    assert.equal(got.protocolVersion, p.protocolVersion);
  });

  it("empty destination UHID round-trips", () => {
    const p = new MeshPacket();
    p.type = PacketType.SosBroadcast;
    p.sourceUhid = "node-1";
    p.destinationUhid = "";
    p.packetNonce = nonce();
    const got = PacketSerializer.deserialize(PacketSerializer.serialize(p));
    assert.equal(got.sourceUhid, "node-1");
    assert.equal(got.destinationUhid, "");
  });

  it("empty payload round-trips", () => {
    const p = new MeshPacket();
    p.type = PacketType.Heartbeat;
    p.sourceUhid = "node-1";
    p.packetNonce = nonce();
    p.payload = new Uint8Array();
    const got = PacketSerializer.deserialize(PacketSerializer.serialize(p));
    assert.equal(got.payload.length, 0);
  });

  it("large payload round-trips", () => {
    const p = new MeshPacket();
    p.type = PacketType.ChunkData;
    p.sourceUhid = "node-1";
    p.destinationUhid = "node-2";
    p.packetNonce = nonce();
    p.payload = new Uint8Array(262144).fill(0xff);
    const got = PacketSerializer.deserialize(PacketSerializer.serialize(p));
    assert.equal(got.payload.length, 262144);
    assert.equal(got.payload[0], 0xff);
    assert.equal(got.payload[262143], 0xff);
  });

  it("UUID round-trips", () => {
    const p = new MeshPacket();
    p.id = "550e8400-e29b-41d4-a716-446655440000";
    p.type = PacketType.Data;
    p.sourceUhid = "node-1";
    p.packetNonce = nonce();
    const got = PacketSerializer.deserialize(PacketSerializer.serialize(p));
    assert.equal(got.id, p.id);
  });

  it("UUID wire order is RFC4122 big-endian", () => {
    const p = new MeshPacket();
    p.id = "550e8400-e29b-41d4-a716-446655440000";
    p.type = PacketType.Data;
    p.sourceUhid = "n";
    p.packetNonce = nonce();
    const b = PacketSerializer.serialize(p);
    const want = [
      0x55, 0x0e, 0x84, 0x00, 0xe2, 0x9b, 0x41, 0xd4,
      0xa7, 0x16, 0x44, 0x66, 0x55, 0x44, 0x00, 0x00,
    ];
    assert.deepEqual(Array.from(b.subarray(2, 18)), want);
  });

  it("too-short input throws", () => {
    assert.throws(() =>
      PacketSerializer.deserialize(new Uint8Array([0x01, 0x02])),
    );
  });

  it("tryDeserialize returns null on garbage", () => {
    assert.equal(
      PacketSerializer.tryDeserialize(new Uint8Array([0xff])),
      null,
    );
  });

  it("all packet types round-trip", () => {
    for (const t of Object.values(PacketType).filter(
      (v) => typeof v === "number",
    ) as number[]) {
      const p = new MeshPacket();
      p.type = t;
      p.sourceUhid = `node-${t}`;
      p.packetNonce = nonce();
      const got = PacketSerializer.deserialize(PacketSerializer.serialize(p));
      assert.equal(got.type, t);
    }
  });

  it("timestamp preserved to the millisecond", () => {
    const p = new MeshPacket();
    p.type = PacketType.Data;
    p.sourceUhid = "node-1";
    p.timestampMs = 1710528000000n;
    p.packetNonce = nonce();
    const got = PacketSerializer.deserialize(PacketSerializer.serialize(p));
    assert.equal(got.timestampMs, 1710528000000n);
  });

  it("Unicode UHIDs round-trip", () => {
    const p = new MeshPacket();
    p.type = PacketType.Data;
    p.sourceUhid = "노드-1";
    p.destinationUhid = "узел-2";
    p.packetNonce = nonce();
    const got = PacketSerializer.deserialize(PacketSerializer.serialize(p));
    assert.equal(got.sourceUhid, "노드-1");
    assert.equal(got.destinationUhid, "узел-2");
  });

  it("signature preserved", () => {
    const p = new MeshPacket();
    p.type = PacketType.Data;
    p.sourceUhid = "node-1";
    p.packetNonce = nonce();
    p.signature = new Uint8Array(64).fill(0xab);
    const got = PacketSerializer.deserialize(PacketSerializer.serialize(p));
    assert.deepEqual(Array.from(got.signature), Array.from(p.signature));
  });

  it("TTL full int32 range preserved", () => {
    // > UInt8 max — would have wrapped to 0 under the pre-2026-05-02 bug.
    const p = new MeshPacket();
    p.type = PacketType.Data;
    p.sourceUhid = "n";
    p.ttl = 256;
    p.packetNonce = nonce();
    const got = PacketSerializer.deserialize(PacketSerializer.serialize(p));
    assert.equal(got.ttl, 256);
  });
});
