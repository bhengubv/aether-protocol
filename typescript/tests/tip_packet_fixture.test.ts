/**
 * Cross-language tipping parity: TS must reproduce the C# reference vectors
 * (fixtures/tipping/tip_packet_basic.json) byte-for-byte — canonical_bytes AND
 * the deterministic Ed25519 signature. Mirrors the Go fixture test.
 *
 * Run with: tsx --test typescript/tests/tip_packet_fixture.test.ts
 * SPDX-License-Identifier: MIT
 */

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import nacl from "tweetnacl";

import { TipPacketPayload } from "../src/incentive/TipPacketPayload.js";
import {
  MeshTipService,
  NoopMeshTipSettlementProvider,
  type TipMeshSender,
  type TipPacketSigner,
  type IdentitySigner,
  type MeshTipSettlementProvider,
} from "../src/incentive/MeshTipService.js";
import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";

interface TipCase {
  tipper_uhid: string;
  recipient_uhid: string;
  amount: string;
  traffic_type: string;
  reference_id: string | null;
  timestamp_unix_ms: number;
  canonical_bytes: string;
  signature: string;
}

interface TipVectors {
  algorithm: string;
  ed25519_seed: string;
  public_key: string;
  cases: TipCase[];
}

const vectorsPath = fileURLToPath(
  new URL("../../fixtures/tipping/tip_packet_basic.json", import.meta.url),
);
const V = JSON.parse(readFileSync(vectorsPath, "utf8")) as TipVectors;

const hex = (b: Uint8Array): string => Buffer.from(b).toString("hex");
const fromHex = (s: string): Uint8Array => new Uint8Array(Buffer.from(s, "hex"));

/** Rebuilds a TipPacketPayload from a fixture case (without the signature). */
function caseToPayload(c: TipCase): TipPacketPayload {
  return new TipPacketPayload({
    tipperUhid: c.tipper_uhid,
    recipientUhid: c.recipient_uhid,
    amount: c.amount,
    trafficType: c.traffic_type,
    referenceId: c.reference_id,
    timestampUnixMs: BigInt(c.timestamp_unix_ms),
  });
}

/** Deterministic Ed25519 sign from a 32-byte seed (raw 64-byte signature). */
function signWithSeed(seed: Uint8Array, data: Uint8Array): Uint8Array {
  const kp = nacl.sign.keyPair.fromSeed(seed);
  return nacl.sign.detached(data, kp.secretKey);
}

test("tip: BuildCanonicalData reproduces the fixture canonical_bytes byte-for-byte", () => {
  for (const c of V.cases) {
    const got = hex(caseToPayload(c).buildCanonicalData());
    assert.equal(got, c.canonical_bytes, `canonical bytes for ${c.tipper_uhid}`);
  }
});

test("tip: deterministic Ed25519 sign reproduces the fixture signature; fixture sig verifies", () => {
  const seed = fromHex(V.ed25519_seed);
  assert.equal(seed.length, 32, "seed size");

  const kp = nacl.sign.keyPair.fromSeed(seed);
  // The derived public key must match the fixture's published key.
  assert.equal(hex(kp.publicKey), V.public_key, "derived public key");

  for (const c of V.cases) {
    const canonical = caseToPayload(c).buildCanonicalData();

    // Deterministic re-sign reproduces the exact fixture signature.
    const sig = signWithSeed(seed, canonical);
    assert.equal(hex(sig), c.signature, `signature for ${c.tipper_uhid}`);

    // The fixture signature verifies against the fixture public key.
    assert.ok(
      nacl.sign.detached.verify(canonical, fromHex(c.signature), kp.publicKey),
      `fixture signature verifies for ${c.tipper_uhid}`,
    );
  }
});

test("tip: null reference_id yields 16 zero bytes; present id uses .NET GUID order", () => {
  // Case 0 has a reference id; case 1 has null.
  const withId = V.cases.find((c) => c.reference_id);
  const withoutId = V.cases.find((c) => c.reference_id === null);
  assert.ok(withId && withoutId, "fixture must cover both id present and null");

  // null → 16 trailing zero bytes immediately before the 8-byte timestamp.
  const canonicalNull = caseToPayload(withoutId!).buildCanonicalData();
  const guidRegion = canonicalNull.subarray(
    canonicalNull.length - 8 - 16,
    canonicalNull.length - 8,
  );
  assert.deepEqual(
    Array.from(guidRegion),
    new Array(16).fill(0),
    "null reference_id is 16 zero bytes",
  );

  // "11112222-3333-4444-5555-666677778888" → mixed-endian .NET bytes.
  const canonicalId = caseToPayload(withId!).buildCanonicalData();
  const guidBytes = canonicalId.subarray(
    canonicalId.length - 8 - 16,
    canonicalId.length - 8,
  );
  assert.equal(
    hex(guidBytes),
    "22221111333344445555666677778888",
    ".NET mixed-endian GUID byte order",
  );
});

test("tip: amount is carried as the exact invariant decimal string (never a JS number)", () => {
  for (const c of V.cases) {
    const p = caseToPayload(c);
    assert.equal(typeof p.amount, "string", "amount must stay a string");
    assert.equal(p.amount, c.amount, "amount string is verbatim");
  }
  // "0.0001" would round-trip lossily through a JS number's default string form
  // in some locales — the canonical bytes prove it is encoded verbatim.
  const small = V.cases.find((c) => c.amount === "0.0001");
  assert.ok(small, "fixture covers a sub-unit amount");
  const canonical = caseToPayload(small!).buildCanonicalData();
  assert.ok(
    hex(canonical).includes(Buffer.from("0.0001", "utf8").toString("hex")),
    "the literal '0.0001' UTF-8 bytes appear in the canonical data",
  );
});

test("tip: signed payload survives a JSON round-trip with canonical bytes + signature intact", () => {
  const seed = fromHex(V.ed25519_seed);
  for (const c of V.cases) {
    const p = caseToPayload(c);
    p.signature = signWithSeed(seed, p.buildCanonicalData());

    const back = TipPacketPayload.parse(p.toJSON());

    assert.equal(
      hex(back.buildCanonicalData()),
      hex(p.buildCanonicalData()),
      "canonical bytes unchanged across JSON round-trip",
    );
    assert.equal(
      hex(back.signature!),
      hex(p.signature),
      "signature unchanged across JSON round-trip",
    );
    assert.equal(back.amount, c.amount, "amount unchanged");
    assert.equal(
      back.referenceId,
      c.reference_id,
      "reference_id nullity/value unchanged",
    );
  }
});

// ── service-level dispatch ────────────────────────────────────────────────────

class FakeSender implements TipMeshSender {
  sent: MeshPacket[] = [];
  broadcasts: MeshPacket[] = [];
  constructor(readonly localUhid: string) {}
  async send(packet: MeshPacket, _nextHop: string): Promise<boolean> {
    this.sent.push(packet);
    return true;
  }
  async broadcast(packet: MeshPacket): Promise<number> {
    this.broadcasts.push(packet);
    return 1;
  }
}

class FakeSigner implements TipPacketSigner {
  signPacket(packet: MeshPacket): MeshPacket {
    packet.signature = new TextEncoder().encode("envelope-sig");
    packet.packetNonce = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]);
    return packet;
  }
}

class SeedIdentity implements IdentitySigner {
  constructor(private readonly seed: Uint8Array) {}
  signData(data: Uint8Array): Uint8Array {
    return signWithSeed(this.seed, data);
  }
}

class RecordingSettler implements MeshTipSettlementProvider {
  calls: TipPacketPayload[] = [];
  async settleMeshTip(payload: TipPacketPayload): Promise<void> {
    this.calls.push(payload);
  }
}

test("tip service: SendTip emits a TipPacket(24) carrying the exact fixture signature", async () => {
  const seed = fromHex(V.ed25519_seed);
  const c = V.cases[0];
  const sender = new FakeSender(c.tipper_uhid);
  const svc = new MeshTipService(sender, new FakeSigner(), new SeedIdentity(seed));

  const signed = await svc.sendTip(
    c.recipient_uhid,
    c.amount,
    c.traffic_type,
    c.reference_id,
    BigInt(c.timestamp_unix_ms),
  );

  assert.equal(signed.type, PacketType.TipPacket, "emitted packet type");

  const payload = TipPacketPayload.parse(signed.payload);
  assert.equal(
    hex(payload.signature!),
    c.signature,
    "service-emitted signature is byte-identical to the fixture",
  );
  // With no route resolver, the tip must have been broadcast.
  assert.equal(sender.broadcasts.length, 1, "one broadcast");
  assert.equal(sender.sent.length, 0, "no unicast");
});

test("tip service: inbound TipPacket(24) reaches the settlement hook; malformed sig is dropped first", async () => {
  const seed = fromHex(V.ed25519_seed);
  const c = V.cases[0];

  // Local node is the addressed recipient, so no onward relay happens.
  const sender = new FakeSender(c.recipient_uhid);
  const settler = new RecordingSettler();
  const svc = new MeshTipService(
    sender,
    new FakeSigner(),
    new SeedIdentity(seed),
    null,
    settler,
  );

  // Well-formed, signed tip payload.
  const p = caseToPayload(c);
  p.signature = signWithSeed(seed, p.buildCanonicalData());
  const pkt = new MeshPacket();
  pkt.type = PacketType.TipPacket;
  pkt.sourceUhid = c.tipper_uhid;
  pkt.destinationUhid = c.recipient_uhid;
  pkt.payload = new TextEncoder().encode(p.toJSON());

  const handled = await svc.handleTipPacket(pkt);
  assert.ok(handled, "the tip was handled");
  assert.equal(settler.calls.length, 1, "settlement hook fired once");
  assert.equal(
    settler.calls[0].tipperUhid,
    c.tipper_uhid,
    "settlement hook got the right payload",
  );

  // A malformed signature (wrong length) must be dropped before the hook fires.
  settler.calls = [];
  p.signature = new Uint8Array([0x00, 0x01, 0x02]);
  const badPkt = new MeshPacket();
  badPkt.type = PacketType.TipPacket;
  badPkt.sourceUhid = c.tipper_uhid;
  badPkt.destinationUhid = c.recipient_uhid;
  badPkt.payload = new TextEncoder().encode(p.toJSON());

  const handledBad = await svc.handleTipPacket(badPkt);
  assert.equal(handledBad, false, "a malformed-signature tip is dropped");
  assert.equal(
    settler.calls.length,
    0,
    "settlement hook must NOT fire for a malformed-signature tip",
  );
});

test("tip service: default no-op settlement provider settles nothing without throwing", async () => {
  await new NoopMeshTipSettlementProvider().settleMeshTip(
    caseToPayload(V.cases[0]),
  );
  assert.ok(true);
});
