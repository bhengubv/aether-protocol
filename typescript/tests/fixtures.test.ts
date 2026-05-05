/**
 * Cross-language wire-format fixture verifier.
 * SPDX-License-Identifier: MIT
 *
 * Reads ../fixtures/inputs.json and ../fixtures/expected/*.bin and asserts
 * that this language's PacketSerializer produces identical bytes for each
 * canonical input. See fixtures/README.md.
 *
 * Run with: tsx --test typescript/tests/fixtures.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, resolve } from "node:path";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import { PacketSerializer } from "../src/protocol/PacketSerializer.js";

interface FixtureInput {
  name: string;
  description: string;
  id: string;
  type: number;
  source_uhid: string;
  destination_uhid: string;
  ttl: number;
  priority: number;
  payload_hex: string;
  packet_nonce_hex: string;
  signature_hex: string;
  timestamp_ms: number;
  protocol_version: number;
}

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const fixturesDir = resolve(__dirname, "..", "..", "fixtures");

function hexToBytes(s: string): Uint8Array {
  if (!s) return new Uint8Array();
  const out = new Uint8Array(s.length / 2);
  for (let i = 0; i < out.length; i++) {
    out[i] = parseInt(s.substring(i * 2, i * 2 + 2), 16);
  }
  return out;
}

function loadInputs(): FixtureInput[] {
  const raw = readFileSync(resolve(fixturesDir, "inputs.json"), "utf8");
  return JSON.parse(raw);
}

function packetFromInput(input: FixtureInput): MeshPacket {
  const p = new MeshPacket();
  p.id = input.id;
  p.type = input.type as PacketType;
  p.sourceUhid = input.source_uhid;
  p.destinationUhid = input.destination_uhid;
  p.ttl = input.ttl;
  p.priority = input.priority;
  p.payload = hexToBytes(input.payload_hex);
  p.packetNonce = hexToBytes(input.packet_nonce_hex);
  p.signature = hexToBytes(input.signature_hex);
  p.timestampMs = BigInt(input.timestamp_ms);
  p.protocolVersion = input.protocol_version;
  return p;
}

describe("PacketSerializer — cross-language fixtures", () => {
  for (const input of loadInputs()) {
    it(`serializes ${input.name} to expected bytes`, () => {
      const got = PacketSerializer.serialize(packetFromInput(input));
      const expected = readFileSync(resolve(fixturesDir, "expected", `${input.name}.bin`));
      assert.equal(got.length, expected.length, `length mismatch for ${input.name}`);
      assert.deepEqual(
        Array.from(got),
        Array.from(expected),
        `byte mismatch for ${input.name} — see fixtures/README.md`,
      );
    });

    it(`deserializes ${input.name} into matching fields`, () => {
      const expected = readFileSync(resolve(fixturesDir, "expected", `${input.name}.bin`));
      const got = PacketSerializer.deserialize(new Uint8Array(expected));

      assert.equal(got.id, input.id);
      assert.equal(got.type, input.type);
      assert.equal(got.sourceUhid, input.source_uhid);
      assert.equal(got.destinationUhid, input.destination_uhid);
      assert.equal(got.ttl, input.ttl);
      assert.equal(got.priority, input.priority);
      assert.deepEqual(Array.from(got.payload), Array.from(hexToBytes(input.payload_hex)));
      assert.deepEqual(
        Array.from(got.packetNonce),
        Array.from(hexToBytes(input.packet_nonce_hex)),
      );
      assert.deepEqual(
        Array.from(got.signature),
        Array.from(hexToBytes(input.signature_hex)),
      );
      assert.equal(got.timestampMs, BigInt(input.timestamp_ms));
      assert.equal(got.protocolVersion, input.protocol_version);
    });
  }
});
