/**
 * Cross-language market parity: TS must reproduce the C# reference vectors
 * (fixtures/market/pov_token_basic.json) byte-for-byte — canonical_body AND the
 * deterministic witness Ed25519 signature, across all three transports. Plus the
 * full on-mesh witness→subject exchange over PacketType.PoVTokenExchange (43)
 * with freshness/replay-dedup. Mirrors the Go fixture test.
 *
 * Run with: tsx --test typescript/tests/pov_token_fixture.test.ts
 * SPDX-License-Identifier: MIT
 */

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import nacl from "tweetnacl";

import {
  PoVToken,
  PoVTransportType,
  buildSignableTokenData,
  transportToString,
} from "../src/market/PoVToken.js";
import {
  PoVTokenExchangeService,
  type PoVMeshSender,
  type PoVPacketSigner,
  type PoVIdentitySigner,
} from "../src/market/PoVTokenExchangeService.js";
import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";

interface PoVCase {
  subject_uhid: string;
  /**
   * The TRUE .NET ticks as a lossless BigInt. NOT loaded via JSON.parse —
   * .NET DateTime.Ticks (~6.38e17 here) exceeds Number.MAX_SAFE_INTEGER, so the
   * raw integer literal is read straight from the fixture text. JSON.parse would
   * silently corrupt e.g. 638123456789012345 → 638123456789012352.
   */
  timestamp_ticks: bigint;
  transport: string;
  transport_byte: number;
  canonical_body: string;
  witness_signature: string;
}

interface PoVVectors {
  algorithm: string;
  witness_seed: string;
  witness_public_key: string;
  cases: PoVCase[];
}

const vectorsPath = fileURLToPath(
  new URL("../../fixtures/market/pov_token_basic.json", import.meta.url),
);
const vectorsText = readFileSync(vectorsPath, "utf8");
const Vraw = JSON.parse(vectorsText) as {
  algorithm: string;
  witness_seed: string;
  witness_public_key: string;
  cases: Omit<PoVCase, "timestamp_ticks">[];
};

// Read every timestamp_ticks integer literal in document order (lossless BigInt)
// and zip it onto the JSON.parse'd cases, replacing the rounded double.
const tickLiterals = [...vectorsText.matchAll(/"timestamp_ticks"\s*:\s*(-?\d+)/g)].map(
  (m) => BigInt(m[1]),
);
const V: PoVVectors = {
  ...Vraw,
  cases: Vraw.cases.map((c, i) => ({ ...c, timestamp_ticks: tickLiterals[i] })),
};

const hex = (b: Uint8Array): string => Buffer.from(b).toString("hex");
const fromHex = (s: string): Uint8Array => new Uint8Array(Buffer.from(s, "hex"));

function signWithSeed(seed: Uint8Array, data: Uint8Array): Uint8Array {
  const kp = nacl.sign.keyPair.fromSeed(seed);
  return nacl.sign.detached(data, kp.secretKey);
}

test("pov: BuildSignableTokenData reproduces the fixture canonical_body byte-for-byte", () => {
  for (const c of V.cases) {
    const got = hex(
      buildSignableTokenData(
        c.subject_uhid,
        BigInt(c.timestamp_ticks),
        c.transport_byte as PoVTransportType,
      ),
    );
    assert.equal(got, c.canonical_body, `canonical body for ${c.subject_uhid}`);
    // Transport enum byte must match the named transport.
    assert.equal(
      transportToString(c.transport_byte as PoVTransportType),
      c.transport,
      `transport name for byte ${c.transport_byte}`,
    );
  }
});

test("pov: deterministic Ed25519 witness sign reproduces the fixture signature; it verifies", () => {
  const seed = fromHex(V.witness_seed);
  assert.equal(seed.length, 32, "seed size");

  const kp = nacl.sign.keyPair.fromSeed(seed);
  assert.equal(hex(kp.publicKey), V.witness_public_key, "witness public key");

  for (const c of V.cases) {
    const body = buildSignableTokenData(
      c.subject_uhid,
      BigInt(c.timestamp_ticks),
      c.transport_byte as PoVTransportType,
    );

    const sig = signWithSeed(seed, body);
    assert.equal(hex(sig), c.witness_signature, `witness sig for ${c.subject_uhid}`);

    assert.ok(
      nacl.sign.detached.verify(
        body,
        fromHex(c.witness_signature),
        kp.publicKey,
      ),
      `fixture witness signature verifies for ${c.subject_uhid}`,
    );
  }
});

test("pov: timestamp_ticks is encoded as an 8-byte LE i64 .NET DateTime.Ticks field", () => {
  for (const c of V.cases) {
    const body = buildSignableTokenData(
      c.subject_uhid,
      BigInt(c.timestamp_ticks),
      c.transport_byte as PoVTransportType,
    );
    // Last 9 bytes = 8-byte ticks (LE) + 1 transport byte.
    const ticksLE = body.subarray(body.length - 9, body.length - 1);
    const view = new DataView(
      ticksLE.buffer,
      ticksLE.byteOffset,
      ticksLE.byteLength,
    );
    assert.equal(
      view.getBigInt64(0, true),
      BigInt(c.timestamp_ticks),
      `ticks round-trip for ${c.subject_uhid}`,
    );
    assert.equal(
      body[body.length - 1],
      c.transport_byte,
      "final transport byte",
    );
  }
});

test("pov: token with signatures survives a JSON round-trip with canonical body intact", () => {
  const seed = fromHex(V.witness_seed);
  for (const c of V.cases) {
    const tok = new PoVToken({
      witnessUhid: "aether:witness:zz",
      subjectUhid: c.subject_uhid,
      timestampTicks: BigInt(c.timestamp_ticks),
      transportUsed: c.transport_byte as PoVTransportType,
      witnessSignature: signWithSeed(
        seed,
        buildSignableTokenData(
          c.subject_uhid,
          BigInt(c.timestamp_ticks),
          c.transport_byte as PoVTransportType,
        ),
      ),
    });

    const back = PoVToken.parse(tok.toJSON());
    assert.equal(
      hex(back.signableData()),
      hex(tok.signableData()),
      "canonical body unchanged across JSON round-trip",
    );
    assert.equal(
      hex(back.witnessSignature!),
      hex(tok.witnessSignature!),
      "witness signature unchanged across JSON round-trip",
    );
    assert.equal(back.transportUsed, tok.transportUsed, "transport unchanged");
    // The i64 ticks must survive EXACTLY — including values beyond
    // Number.MAX_SAFE_INTEGER (the production JSON splices the literal, never a
    // lossy double).
    assert.equal(
      back.timestampTicks,
      c.timestamp_ticks,
      "timestamp_ticks survives the JSON round-trip losslessly",
    );
  }
});

test("pov: a ticks value beyond Number.MAX_SAFE_INTEGER round-trips through JSON losslessly", () => {
  // 638123456789012345 > 9007199254740991 — JSON.parse would round it to
  // 638123456789012352. The production wire path must preserve it exactly.
  const big = 638123456789012345n;
  assert.ok(big > BigInt(Number.MAX_SAFE_INTEGER), "test value exceeds safe range");

  const tok = new PoVToken({
    witnessUhid: "aether:witness:big",
    subjectUhid: "aether:subject:big",
    timestampTicks: big,
    transportUsed: PoVTransportType.Nfc,
  });

  const back = PoVToken.parse(tok.toJSON());
  assert.equal(back.timestampTicks, big, "exact i64 ticks preserved");
  // And the canonical body built from the round-tripped value is byte-identical.
  assert.equal(
    hex(back.signableData()),
    hex(tok.signableData()),
    "canonical body identical after lossless round-trip",
  );
});

// ── on-mesh exchange flow (packet 43) ─────────────────────────────────────────

class FakeSender implements PoVMeshSender {
  sent: MeshPacket[] = [];
  constructor(readonly localUhid: string) {}
  async send(packet: MeshPacket, _subject: string): Promise<boolean> {
    this.sent.push(packet);
    return true;
  }
}

/** Real-Ed25519 identity signer/verifier — the local node's identity key. */
class RealIdentity implements PoVIdentitySigner {
  constructor(private readonly seed: Uint8Array) {}
  signData(data: Uint8Array): Uint8Array {
    return signWithSeed(this.seed, data);
  }
  verifySignature(
    publicKey: Uint8Array,
    data: Uint8Array,
    sig: Uint8Array,
  ): boolean {
    return nacl.sign.detached.verify(data, sig, publicKey);
  }
}

/**
 * Stamps a real Ed25519 envelope signature with the node's key and enforces nonce
 * replay-dedup + a fresh-signature check (mirrors the C# IPacketSigningService
 * contract; freshness windowing is exercised in the C# layer — here we focus on
 * the body crypto and replay).
 */
class PassSigner implements PoVPacketSigner {
  private readonly seen = new Set<string>();
  private nonceCounter = 0;
  constructor(private readonly seed: Uint8Array) {}
  signPacket(packet: MeshPacket): MeshPacket {
    // Unique nonce per emitted packet.
    const n = ++this.nonceCounter;
    packet.packetNonce = new Uint8Array([n, 9, 9, 9, 9, 9, 9, 9]);
    packet.signature = signWithSeed(
      this.seed,
      new TextEncoder().encode(`${packet.sourceUhid}:${packet.destinationUhid}`),
    );
    return packet;
  }
  verifyPacket(packet: MeshPacket, senderPub: Uint8Array): boolean {
    const key = `${packet.sourceUhid}:${hex(packet.packetNonce)}`;
    if (this.seen.has(key)) {
      return false; // replay
    }
    this.seen.add(key);
    return nacl.sign.detached.verify(
      new TextEncoder().encode(`${packet.sourceUhid}:${packet.destinationUhid}`),
      packet.signature,
      senderPub,
    );
  }
}

test("pov exchange: witness issues over packet 43; subject verifies, countersigns, records; replay rejected", () => {
  const witnessSeed = nacl.randomBytes(32);
  const subjectSeed = nacl.randomBytes(32);
  const witnessPub = nacl.sign.keyPair.fromSeed(witnessSeed).publicKey;
  const subjectPub = nacl.sign.keyPair.fromSeed(subjectSeed).publicKey;

  const witnessUhid = "aether:node:witness";
  const subjectUhid = "aether:node:subject";

  // Witness side.
  const wSender = new FakeSender(witnessUhid);
  const witness = new PoVTokenExchangeService(
    wSender,
    new PassSigner(witnessSeed),
    new RealIdentity(witnessSeed),
  );

  // Issue is async (sender.send is async).
  return witness
    .issueToken(subjectUhid, PoVTransportType.Ble)
    .then((token) => {
      assert.ok(token, "witness issued a valid token");
      assert.equal(wSender.sent.length, 1, "exactly one directed send");

      const exchangePkt = wSender.sent[0];
      assert.equal(
        exchangePkt.type,
        PacketType.PoVTokenExchange,
        "issued packet type is PoVTokenExchange(43)",
      );
      assert.equal(exchangePkt.ttl, 1, "issued packet TTL is 1 (one short-range hop)");

      // Subject side receives the witness's packet.
      const sSender = new FakeSender(subjectUhid);
      const subject = new PoVTokenExchangeService(
        sSender,
        new PassSigner(subjectSeed),
        new RealIdentity(subjectSeed),
      );

      let received: PoVToken | null = null;
      subject.onTokenReceived = (tok) => {
        received = tok;
      };

      const accepted = subject.handleTokenExchange(exchangePkt, witnessPub);
      assert.ok(accepted, "subject accepted a valid witness token");
      assert.ok(received, "onTokenReceived fired");

      // BOTH signatures must now verify over the same canonical body.
      const acceptedToken = received as unknown as PoVToken;
      const body = acceptedToken.signableData();
      assert.ok(
        nacl.sign.detached.verify(
          body,
          acceptedToken.witnessSignature!,
          witnessPub,
        ),
        "witness signature verifies on the accepted token",
      );
      assert.ok(
        nacl.sign.detached.verify(
          body,
          acceptedToken.subjectSignature!,
          subjectPub,
        ),
        "subject countersignature verifies on the accepted token",
      );

      // Score reflects one unique witness for the subject.
      const score = subject.getScore(subjectUhid);
      assert.equal(score.uniqueWitnesses, 1, "one unique witness");

      // Replaying the same packet is rejected by the signer's nonce dedup.
      const replay = subject.handleTokenExchange(exchangePkt, witnessPub);
      assert.equal(replay, false, "a replayed PoV exchange packet is rejected");
    });
});

test("pov exchange: refuses self-vouch and non-short-range minting", async () => {
  const seed = nacl.randomBytes(32);
  const sender = new FakeSender("aether:node:self");
  const svc = new PoVTokenExchangeService(
    sender,
    new PassSigner(seed),
    new RealIdentity(seed),
  );

  // Self-vouch refused.
  assert.equal(
    await svc.issueToken("aether:node:self", PoVTransportType.Ble),
    null,
    "a node must not be able to vouch for itself",
  );
  // Non-short-range refused (transport byte 9 is not BLE/NFC/NearLink).
  assert.equal(
    await svc.issueToken("aether:node:other", 9 as PoVTransportType),
    null,
    "PoV must refuse to mint over a non-short-range transport",
  );
  assert.equal(sender.sent.length, 0, "no packet sent for refused issuances");
});
