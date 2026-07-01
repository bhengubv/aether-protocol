// SPDX-License-Identifier: MIT
//
// Cross-language circuit-relay-v2 wire-format verifier. Serializes each input
// case and asserts byte-equality with fixtures/circuit-relay/expected/<name>.bin
// (the Go oracle output), then deserializes and asserts every field round-trips.

import test from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

import {
  RelayFrame,
  RelayMessageType,
  RelayStatus,
  serializeRelayFrame,
  deserializeRelayFrame,
} from "../src/circuitrelay/RelayFrame.js";

const here = path.dirname(fileURLToPath(import.meta.url));

const NIL_UUID = "00000000-0000-0000-0000-000000000000";

interface RelayInput {
  name: string;
  type: number;
  status?: number;
  source_uhid?: string;
  destination_uhid?: string;
  relay_uhid?: string;
  connection_id?: string;
  reservation_expires_at_ms?: number;
  limit_duration_seconds?: number;
  limit_data_bytes?: number;
  payload_hex?: string;
  payload_len?: number;
}

function fixturesDir(): string {
  let dir = here;
  for (let i = 0; i < 10; i++) {
    if (existsSync(path.join(dir, "fixtures", "circuit-relay", "inputs.json"))) {
      return path.join(dir, "fixtures", "circuit-relay");
    }
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  throw new Error("fixtures/circuit-relay/inputs.json not found from " + here);
}

function loadInputs(): RelayInput[] {
  return JSON.parse(readFileSync(path.join(fixturesDir(), "inputs.json"), "utf8"));
}

function hexToBytes(hex: string): Uint8Array {
  const b = new Uint8Array(hex.length / 2);
  for (let i = 0; i < b.length; i++) b[i] = parseInt(hex.substr(i * 2, 2), 16);
  return b;
}

function payloadFor(input: RelayInput): Uint8Array {
  if ((input.payload_len ?? 0) > 0) {
    const b = new Uint8Array(input.payload_len!);
    for (let i = 0; i < b.length; i++) b[i] = i % 256;
    return b;
  }
  return hexToBytes(input.payload_hex ?? "");
}

function frameFor(input: RelayInput): RelayFrame {
  return {
    type: input.type as RelayMessageType,
    status: (input.status ?? 0) as RelayStatus,
    sourceUhid: input.source_uhid ?? "",
    destinationUhid: input.destination_uhid ?? "",
    relayUhid: input.relay_uhid ?? "",
    connectionId: input.connection_id ?? "",
    reservationExpiresAtMs: input.reservation_expires_at_ms ?? 0,
    limitDurationSeconds: input.limit_duration_seconds ?? 0,
    limitDataBytes: input.limit_data_bytes ?? 0,
    payload: payloadFor(input),
  };
}

for (const input of loadInputs()) {
  test(`relay fixture serialize ${input.name}`, () => {
    const got = serializeRelayFrame(frameFor(input));
    const expected = new Uint8Array(readFileSync(path.join(fixturesDir(), "expected", input.name + ".bin")));
    assert.deepEqual([...got], [...expected]);
  });

  test(`relay fixture deserialize ${input.name}`, () => {
    const data = new Uint8Array(readFileSync(path.join(fixturesDir(), "expected", input.name + ".bin")));
    const f = deserializeRelayFrame(data);
    assert.equal(f.type, input.type);
    assert.equal(f.status, input.status ?? 0);
    assert.equal(f.sourceUhid, input.source_uhid ?? "");
    assert.equal(f.destinationUhid, input.destination_uhid ?? "");
    assert.equal(f.relayUhid, input.relay_uhid ?? "");
    assert.equal(f.connectionId, input.connection_id && input.connection_id.length > 0 ? input.connection_id : NIL_UUID);
    assert.equal(f.reservationExpiresAtMs, input.reservation_expires_at_ms ?? 0);
    assert.equal(f.limitDurationSeconds, input.limit_duration_seconds ?? 0);
    assert.equal(f.limitDataBytes, input.limit_data_bytes ?? 0);
    assert.deepEqual([...f.payload], [...payloadFor(input)]);
  });
}
