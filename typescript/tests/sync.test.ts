/**
 * Decentralised multi-device sync parity tests (TypeScript).
 *
 * Drives the SyncRecord binary envelope, deterministic last-write-wins
 * reconciliation, and signed DeviceLink records through the shared vectors at
 * `fixtures/sync/vectors.json`:
 *   • sync_records → serialize == serialized_hex, and deserialize round-trips.
 *   • reconcile    → winner(records).recordId == winner_record_id, in both the
 *                    given order and reversed (order-independence).
 *   • device_links → signedBody hex, deterministic signature hex, serialize hex,
 *                    verify(identity_public) === true, verify(wrong) === false,
 *                    and deserialize round-trips.
 * Every AetherNet SDK drives the SAME vectors and MUST reproduce these bytes.
 *
 * Run with: tsx --test typescript/tests/sync.test.ts
 * SPDX-License-Identifier: MIT
 */
import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync, existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import {
  SyncOp,
  SyncRecord,
  serializeSyncRecord,
  deserializeSyncRecord,
} from "../src/sync/SyncRecord.js";
import { winner, merge } from "../src/sync/SyncReconciler.js";
import {
  DeviceLink,
  signedBody,
  createDeviceLink,
  verifyDeviceLink,
  serializeDeviceLink,
  deserializeDeviceLink,
} from "../src/sync/DeviceLink.js";

// ── Fixture types ────────────────────────────────────────────────────────────

interface SyncRecordVector {
  record_id: string;
  device_id: string;
  op: number;
  item_id: string;
  logical_clock: number;
  created_at_ms: number;
  payload_hex: string;
  serialized_hex: string;
}
interface ReconcileVector {
  name: string;
  records: SyncRecordVector[];
  winner_record_id: string;
}
interface DeviceLinkVector {
  device_id: string;
  device_public_key: string;
  issued_at_ms: number;
  signed_body_hex: string;
  signature_hex: string;
  serialized_hex: string;
}
interface Corpus {
  identity_private: string;
  identity_public: string;
  wrong_identity_public: string;
  sync_records: SyncRecordVector[];
  reconcile: ReconcileVector[];
  device_links: DeviceLinkVector[];
}

// ── Helpers ──────────────────────────────────────────────────────────────────

function hexToBytes(s: string): Uint8Array {
  const out = new Uint8Array(s.length / 2);
  for (let i = 0; i < out.length; i++) {
    out[i] = parseInt(s.substring(i * 2, i * 2 + 2), 16);
  }
  return out;
}

function toHex(b: Uint8Array): string {
  return Buffer.from(b).toString("hex");
}

/** Build a SyncRecord DTO from a fixture vector. */
function recordFrom(v: SyncRecordVector): SyncRecord {
  return {
    recordId: v.record_id,
    deviceId: v.device_id,
    op: v.op as SyncOp,
    itemId: v.item_id,
    logicalClock: BigInt(v.logical_clock),
    createdAtMs: BigInt(v.created_at_ms),
    encryptedPayload: hexToBytes(v.payload_hex),
  };
}

/** Walk up from this file to the repo root and load fixtures/sync/vectors.json. */
function loadCorpus(): Corpus {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (;;) {
    const candidate = join(dir, "fixtures", "sync", "vectors.json");
    if (existsSync(candidate)) {
      return JSON.parse(readFileSync(candidate, "utf8")) as Corpus;
    }
    const parent = dirname(dir);
    if (parent === dir) {
      throw new Error(
        "fixtures/sync/vectors.json not found walking up from " +
          dirname(fileURLToPath(import.meta.url)),
      );
    }
    dir = parent;
  }
}

const corpus = loadCorpus();

// ── SyncRecord binary envelope ───────────────────────────────────────────────

describe("SyncRecord — binary envelope parity", () => {
  assert.ok(corpus.sync_records.length >= 4, "expected sync_record vectors");

  for (const v of corpus.sync_records) {
    it(`serialize ${v.record_id.slice(0, 8)}… == serialized_hex`, () => {
      assert.equal(toHex(serializeSyncRecord(recordFrom(v))), v.serialized_hex);
    });

    it(`deserialize ${v.record_id.slice(0, 8)}… round-trips`, () => {
      const parsed = deserializeSyncRecord(hexToBytes(v.serialized_hex));
      assert.equal(parsed.recordId, v.record_id);
      assert.equal(parsed.deviceId, v.device_id);
      assert.equal(parsed.op, v.op);
      assert.equal(parsed.itemId, v.item_id);
      assert.equal(parsed.logicalClock, BigInt(v.logical_clock));
      assert.equal(parsed.createdAtMs, BigInt(v.created_at_ms));
      assert.equal(toHex(parsed.encryptedPayload), v.payload_hex);
      // Re-serializing the parsed record reproduces the exact bytes.
      assert.equal(toHex(serializeSyncRecord(parsed)), v.serialized_hex);
    });
  }

  it("rejects an unknown format version", () => {
    const bad = hexToBytes(corpus.sync_records[0].serialized_hex);
    bad[0] = 0x02;
    assert.throws(() => deserializeSyncRecord(bad), /version/i);
  });

  it("rejects an unknown op", () => {
    const bad = hexToBytes(corpus.sync_records[0].serialized_hex);
    bad[17] = 0x03; // op byte lives right after version(1) + record_id(16)
    assert.throws(() => deserializeSyncRecord(bad), /op/i);
  });
});

// ── Reconciliation (deterministic last-write-wins) ───────────────────────────

describe("SyncReconciler — deterministic last-write-wins", () => {
  for (const v of corpus.reconcile) {
    it(`${v.name}: winner == ${v.winner_record_id.slice(0, 8)}…`, () => {
      const records = v.records.map(recordFrom);
      assert.equal(winner(records).recordId, v.winner_record_id);

      // Order-independence: reversed input must pick the same winner.
      const reversed = [...records].reverse();
      assert.equal(winner(reversed).recordId, v.winner_record_id);

      // merge() over a single item yields that same winner.
      const merged = merge(records);
      assert.equal(merged.size, 1);
      assert.equal([...merged.values()][0].recordId, v.winner_record_id);
    });
  }
});

// ── DeviceLink (signed device-membership record) ─────────────────────────────

describe("DeviceLink — signed membership parity", () => {
  const identitySeed = hexToBytes(corpus.identity_private);
  const identityPublic = hexToBytes(corpus.identity_public);
  const wrongPublic = hexToBytes(corpus.wrong_identity_public);

  for (const v of corpus.device_links) {
    const devicePublicKey = hexToBytes(v.device_public_key);
    const issuedAtMs = BigInt(v.issued_at_ms);

    it(`${v.device_id}: signedBody == signed_body_hex`, () => {
      assert.equal(
        toHex(signedBody(v.device_id, devicePublicKey, issuedAtMs)),
        v.signed_body_hex,
      );
    });

    it(`${v.device_id}: signature is deterministic and == signature_hex`, () => {
      const link = createDeviceLink(v.device_id, devicePublicKey, issuedAtMs, identitySeed);
      assert.equal(toHex(link.signature), v.signature_hex);
    });

    it(`${v.device_id}: serialize == serialized_hex`, () => {
      const link = createDeviceLink(v.device_id, devicePublicKey, issuedAtMs, identitySeed);
      assert.equal(toHex(serializeDeviceLink(link)), v.serialized_hex);
    });

    it(`${v.device_id}: verify(identity_public) === true`, () => {
      const link = createDeviceLink(v.device_id, devicePublicKey, issuedAtMs, identitySeed);
      assert.equal(verifyDeviceLink(link, identityPublic), true);
    });

    it(`${v.device_id}: verify(wrong_identity_public) === false`, () => {
      const link = createDeviceLink(v.device_id, devicePublicKey, issuedAtMs, identitySeed);
      assert.equal(verifyDeviceLink(link, wrongPublic), false);
    });

    it(`${v.device_id}: deserialize round-trips and re-verifies`, () => {
      const parsed: DeviceLink = deserializeDeviceLink(hexToBytes(v.serialized_hex));
      assert.equal(parsed.deviceId, v.device_id);
      assert.equal(toHex(parsed.devicePublicKey), v.device_public_key);
      assert.equal(parsed.issuedAtMs, issuedAtMs);
      assert.equal(toHex(parsed.signature), v.signature_hex);
      assert.equal(toHex(serializeDeviceLink(parsed)), v.serialized_hex);
      assert.equal(verifyDeviceLink(parsed, identityPublic), true);
    });
  }
});
