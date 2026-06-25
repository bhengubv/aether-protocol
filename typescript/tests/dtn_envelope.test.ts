// SPDX-License-Identifier: MIT
//
// Cross-language DTN-envelope wire-format verifier. Serializes each input case
// and asserts byte-equality with fixtures/dtn/expected/<name>.bin (the Go
// oracle output), then deserializes and asserts every field round-trips.

import test from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

import { BundlePriority, BundleStatus, DtnBundle, DtnDeliveryReceipt } from "../src/models/index.js";
import {
  deserializeBundle,
  deserializeCustodyAck,
  deserializeDeliveryReceipt,
  serializeBundle,
  serializeCustodyAck,
  serializeDeliveryReceipt,
} from "../src/dtn/DtnEnvelope.js";

const here = path.dirname(fileURLToPath(import.meta.url));

interface DtnInput {
  kind: string;
  name: string;
  id?: string;
  priority?: number;
  status?: number;
  copy_count?: number;
  max_copies?: number;
  hop_count?: number;
  created_at_ms?: number;
  expires_at_ms?: number;
  sender_uhid?: string;
  recipient_uhid?: string;
  sender_geohash?: string | null;
  recipient_last_geohash?: string | null;
  encrypted_payload_hex?: string;
  encrypted_payload_len?: number;
  bundle_id?: string;
  accepted?: boolean;
  total_hops?: number;
  total_custody_transfers?: number;
  delivered_at_ms?: number;
}

function fixturesDir(): string {
  let dir = here;
  for (let i = 0; i < 10; i++) {
    if (existsSync(path.join(dir, "fixtures", "dtn", "inputs.json"))) {
      return path.join(dir, "fixtures", "dtn");
    }
    const parent = path.dirname(dir);
    if (parent === dir) break;
    dir = parent;
  }
  throw new Error("fixtures/dtn/inputs.json not found from " + here);
}

function loadInputs(): DtnInput[] {
  return JSON.parse(readFileSync(path.join(fixturesDir(), "inputs.json"), "utf8"));
}

function hexToBytes(hex: string): Uint8Array {
  const b = new Uint8Array(hex.length / 2);
  for (let i = 0; i < b.length; i++) b[i] = parseInt(hex.substr(i * 2, 2), 16);
  return b;
}

function payloadFor(input: DtnInput): Uint8Array {
  if ((input.encrypted_payload_len ?? 0) > 0) {
    const b = new Uint8Array(input.encrypted_payload_len!);
    for (let i = 0; i < b.length; i++) b[i] = i % 256;
    return b;
  }
  return hexToBytes(input.encrypted_payload_hex ?? "");
}

function serialize(input: DtnInput): Uint8Array {
  switch (input.kind) {
    case "bundle": {
      const b: DtnBundle = {
        id: input.id!,
        senderUhid: input.sender_uhid ?? "",
        recipientUhid: input.recipient_uhid ?? "",
        encryptedPayload: payloadFor(input),
        priority: (input.priority ?? 0) as BundlePriority,
        status: (input.status ?? 0) as BundleStatus,
        copyCount: input.copy_count ?? 0,
        maxCopies: input.max_copies ?? 0,
        senderGeohash: input.sender_geohash ?? undefined,
        recipientLastGeohash: input.recipient_last_geohash ?? undefined,
        hopCount: input.hop_count ?? 0,
        createdAt: new Date(input.created_at_ms ?? 0),
        expiresAt: new Date(input.expires_at_ms ?? 0),
      };
      return serializeBundle(b);
    }
    case "custody_ack":
      return serializeCustodyAck(input.bundle_id!, input.accepted ?? false);
    case "delivery_receipt": {
      const r: DtnDeliveryReceipt = {
        bundleId: input.bundle_id!,
        recipientUhid: input.recipient_uhid ?? "",
        totalHops: input.total_hops ?? 0,
        totalCustodyTransfers: input.total_custody_transfers ?? 0,
        deliveredAt: new Date(input.delivered_at_ms ?? 0),
      };
      return serializeDeliveryReceipt(r);
    }
    default:
      throw new Error(`unknown kind ${input.kind}`);
  }
}

for (const input of loadInputs()) {
  test(`dtn fixture serialize ${input.name}`, () => {
    const got = serialize(input);
    const expected = new Uint8Array(readFileSync(path.join(fixturesDir(), "expected", input.name + ".bin")));
    assert.deepEqual([...got], [...expected]);
  });

  test(`dtn fixture deserialize ${input.name}`, () => {
    const data = new Uint8Array(readFileSync(path.join(fixturesDir(), "expected", input.name + ".bin")));
    if (input.kind === "bundle") {
      const b = deserializeBundle(data);
      assert.equal(b.id, input.id);
      assert.equal(b.priority, input.priority);
      assert.equal(b.status, input.status);
      assert.equal(b.copyCount, input.copy_count);
      assert.equal(b.maxCopies, input.max_copies);
      assert.equal(b.hopCount, input.hop_count);
      assert.equal(b.createdAt.getTime(), input.created_at_ms);
      assert.equal(b.expiresAt.getTime(), input.expires_at_ms);
      assert.equal(b.senderUhid, input.sender_uhid);
      assert.equal(b.recipientUhid, input.recipient_uhid);
      assert.equal(b.senderGeohash, input.sender_geohash ?? "");
      assert.equal(b.recipientLastGeohash, input.recipient_last_geohash ?? "");
      assert.deepEqual([...b.encryptedPayload], [...payloadFor(input)]);
    } else if (input.kind === "custody_ack") {
      const a = deserializeCustodyAck(data);
      assert.equal(a.bundleId, input.bundle_id);
      assert.equal(a.accepted, input.accepted);
    } else {
      const r = deserializeDeliveryReceipt(data);
      assert.equal(r.bundleId, input.bundle_id);
      assert.equal(r.recipientUhid, input.recipient_uhid);
      assert.equal(r.totalHops, input.total_hops);
      assert.equal(r.totalCustodyTransfers, input.total_custody_transfers);
      assert.equal(r.deliveredAt.getTime(), input.delivered_at_ms);
    }
  });
}
