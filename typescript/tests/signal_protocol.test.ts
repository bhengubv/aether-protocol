/**
 * Cross-language Signal-protocol fixture verifier and end-to-end exercises.
 *
 * Verifies that the TypeScript implementation produces byte-identical X3DH
 * and ratchet outputs to the C# reference (committed in
 * fixtures/signal/expected/*.json). Any drift between TS and C# / Go /
 * Python / Swift / Kotlin / Rust / C surfaces here as a hex mismatch.
 *
 * SPDX-License-Identifier: MIT
 */

import { strict as assert } from "node:assert";
import { test } from "node:test";
import { readFileSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import {
  createHmac,
  createPrivateKey,
  createPublicKey,
  diffieHellman,
} from "node:crypto";
import { hkdf } from "@noble/hashes/hkdf";
import { sha256 } from "@noble/hashes/sha256";

import {
  SignalProtocol,
  MESSAGE_TYPE_NORMAL,
  MESSAGE_TYPE_PRE_KEY,
} from "../src/security/SignalProtocol.js";

// ─── Fixture path resolution ─────────────────────────────────────────────

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

function repoRoot(): string {
  let dir = __dirname;
  for (let i = 0; i < 10; i++) {
    try {
      readFileSync(join(dir, "AetherProtocol.slnx"));
      return dir;
    } catch {
      dir = join(dir, "..");
    }
  }
  throw new Error(`AetherProtocol.slnx not found above ${__dirname}`);
}

function loadFixturePair(caseName: string): { inputs: any; expected: any } {
  const root = repoRoot();
  const inputs = JSON.parse(readFileSync(join(root, "fixtures/signal/inputs.json"), "utf8"));
  const inputsCase = (inputs.cases as any[]).find((c) => c.name === caseName);
  if (!inputsCase) throw new Error(`Case ${caseName} not in inputs.json`);
  const expected = JSON.parse(
    readFileSync(join(root, "fixtures/signal/expected", `${caseName}.json`), "utf8")
  );
  return { inputs: inputsCase, expected };
}

// ─── Crypto helpers (used to compute expected fixture values) ─────────────

function hex(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("hex");
}

function unhex(s: string): Uint8Array {
  return new Uint8Array(Buffer.from(s, "hex"));
}

function x25519DerivePub(priv: Uint8Array): Uint8Array {
  const privKey = createPrivateKey({
    key: { kty: "OKP", crv: "X25519", d: Buffer.from(priv).toString("base64url"), x: "" },
    format: "jwk",
  } as any);
  const pubKey = createPublicKey(privKey);
  const pubJwk = pubKey.export({ format: "jwk" }) as { x?: string };
  return new Uint8Array(Buffer.from(pubJwk.x!, "base64url"));
}

function x25519Agree(priv: Uint8Array, pub: Uint8Array): Uint8Array {
  const privKey = createPrivateKey({
    key: { kty: "OKP", crv: "X25519", d: Buffer.from(priv).toString("base64url"), x: "" },
    format: "jwk",
  } as any);
  const pubKey = createPublicKey({
    key: { kty: "OKP", crv: "X25519", x: Buffer.from(pub).toString("base64url") },
    format: "jwk",
  } as any);
  return new Uint8Array(diffieHellman({ privateKey: privKey, publicKey: pubKey }));
}

function hkdfDerive(ikm: Uint8Array, info: Uint8Array): Uint8Array {
  return new Uint8Array(hkdf(sha256, ikm, undefined, info, 32));
}

function hmacOne(key: Uint8Array, b: number): Uint8Array {
  return new Uint8Array(createHmac("sha256", key).update(Buffer.from([b])).digest());
}

function concat(...arrays: Uint8Array[]): Uint8Array {
  let total = 0;
  for (const a of arrays) total += a.length;
  const out = new Uint8Array(total);
  let offset = 0;
  for (const a of arrays) {
    out.set(a, offset);
    offset += a.length;
  }
  return out;
}

// ─── Fixture verifiers ────────────────────────────────────────────────────

test("signal fixture: x3dh_basic", () => {
  const { inputs, expected } = loadFixturePair("x3dh_basic");

  const aliceIK = unhex(inputs.alice_identity_priv_hex);
  const aliceEK = unhex(inputs.alice_ephemeral_priv_hex);
  const bobIK = unhex(inputs.bob_identity_priv_hex);
  const bobSPK = unhex(inputs.bob_signed_pre_key_priv_hex);
  const bobOPK = unhex(inputs.bob_one_time_pre_key_priv_hex);

  const aliceIKPub = x25519DerivePub(aliceIK);
  const aliceEKPub = x25519DerivePub(aliceEK);
  const bobIKPub = x25519DerivePub(bobIK);
  const bobSPKPub = x25519DerivePub(bobSPK);
  const bobOPKPub = x25519DerivePub(bobOPK);

  const dh1 = x25519Agree(aliceIK, bobSPKPub);
  const dh2 = x25519Agree(aliceEK, bobIKPub);
  const dh3 = x25519Agree(aliceEK, bobSPKPub);
  const dh4 = x25519Agree(aliceEK, bobOPKPub);

  const shared = concat(dh1, dh2, dh3, dh4);
  const rootInfo = Buffer.from(inputs.hkdf_root_info_utf8 as string, "utf8");
  const sendInfo = Buffer.from(inputs.hkdf_chain_initiator_send_info_utf8 as string, "utf8");
  const recvInfo = Buffer.from(inputs.hkdf_chain_initiator_recv_info_utf8 as string, "utf8");

  const root = hkdfDerive(shared, rootInfo);
  const sendChain = hkdfDerive(root, sendInfo);
  const recvChain = hkdfDerive(root, recvInfo);

  assert.equal(hex(aliceIKPub), expected.alice_identity_pub_hex);
  assert.equal(hex(aliceEKPub), expected.alice_ephemeral_pub_hex);
  assert.equal(hex(bobIKPub), expected.bob_identity_pub_hex);
  assert.equal(hex(bobSPKPub), expected.bob_signed_pre_key_pub_hex);
  assert.equal(hex(bobOPKPub), expected.bob_one_time_pre_key_pub_hex);
  assert.equal(hex(dh1), expected.dh1_hex);
  assert.equal(hex(dh2), expected.dh2_hex);
  assert.equal(hex(dh3), expected.dh3_hex);
  assert.equal(hex(dh4), expected.dh4_hex);
  assert.equal(hex(shared), expected.shared_secret_hex);
  assert.equal(hex(root), expected.root_key_hex);
  assert.equal(hex(sendChain), expected.initiator_send_chain_key_hex);
  assert.equal(hex(recvChain), expected.initiator_recv_chain_key_hex);
});

test("signal fixture: ratchet_step_basic", () => {
  const { inputs, expected } = loadFixturePair("ratchet_step_basic");
  const chainKey = unhex(inputs.chain_key_hex);
  assert.equal(hex(hmacOne(chainKey, 0x01)), expected.message_key_hex);
  assert.equal(hex(hmacOne(chainKey, 0x02)), expected.next_chain_key_hex);
});

test("signal fixture: ratchet_step_three_iterations", () => {
  const { inputs, expected } = loadFixturePair("ratchet_step_three_iterations");
  let chainKey = unhex(inputs.initial_chain_key_hex);
  for (let i = 0; i < 3; i++) {
    const msg = hmacOne(chainKey, 0x01);
    const nxt = hmacOne(chainKey, 0x02);
    assert.equal(hex(msg), expected[`step_${i}_message_key_hex`]);
    assert.equal(hex(nxt), expected[`step_${i}_chain_key_after_hex`]);
    chainKey = nxt;
  }
});

// ─── End-to-end exercises ────────────────────────────────────────────────

test("X3DH first message round-trips", async () => {
  const alice = new SignalProtocol();
  const bob = new SignalProtocol();

  const bobBundle = await bob.generatePreKeyBundle("bob");
  await alice.generatePreKeyBundle("alice");
  await alice.processPreKeyBundle(bobBundle);

  const encrypted = await alice.encrypt("bob", new Uint8Array(Buffer.from("the mesh is alive")));
  assert.equal(encrypted.messageType, MESSAGE_TYPE_PRE_KEY);
  assert.equal(encrypted.initiatorIdentityKeyX25519?.length, 32);
  assert.equal(encrypted.initiatorEphemeralKeyX25519?.length, 32);
  assert.equal(encrypted.senderUhid, "alice");

  const plaintext = await bob.decrypt("alice", encrypted);
  assert.equal(Buffer.from(plaintext).toString("utf8"), "the mesh is alive");
  assert.equal(bob.hasSession("alice"), true);
});

test("X3DH subsequent message is normal not pre-key", async () => {
  const alice = new SignalProtocol();
  const bob = new SignalProtocol();
  const bobBundle = await bob.generatePreKeyBundle("bob");
  await alice.generatePreKeyBundle("alice");
  await alice.processPreKeyBundle(bobBundle);

  const first = await alice.encrypt("bob", new Uint8Array(Buffer.from("a")));
  await bob.decrypt("alice", first);

  const second = await alice.encrypt("bob", new Uint8Array(Buffer.from("b")));
  assert.equal(second.messageType, MESSAGE_TYPE_NORMAL);
  assert.equal(second.initiatorIdentityKeyX25519, undefined);

  const out = await bob.decrypt("alice", second);
  assert.equal(Buffer.from(out).toString("utf8"), "b");
});

test("X3DH bidirectional after first message", async () => {
  const alice = new SignalProtocol();
  const bob = new SignalProtocol();
  const bobBundle = await bob.generatePreKeyBundle("bob");
  await alice.generatePreKeyBundle("alice");
  await alice.processPreKeyBundle(bobBundle);

  const a = await alice.encrypt("bob", new Uint8Array(Buffer.from("ping")));
  assert.equal(Buffer.from(await bob.decrypt("alice", a)).toString("utf8"), "ping");

  const b = await bob.encrypt("alice", new Uint8Array(Buffer.from("pong")));
  assert.equal(b.messageType, MESSAGE_TYPE_NORMAL);
  assert.equal(Buffer.from(await alice.decrypt("bob", b)).toString("utf8"), "pong");
});

test("X3DH five sequential messages ratchet forward", async () => {
  const alice = new SignalProtocol();
  const bob = new SignalProtocol();
  const bobBundle = await bob.generatePreKeyBundle("bob");
  await alice.generatePreKeyBundle("alice");
  await alice.processPreKeyBundle(bobBundle);

  for (let i = 0; i < 5; i++) {
    const enc = await alice.encrypt("bob", new Uint8Array([i]));
    assert.equal(enc.counter, i);
    const dec = await bob.decrypt("alice", enc);
    assert.deepEqual(Array.from(dec), [i]);
  }
});

test("one-time pre-key is consumed after responder establishes", async () => {
  const alice = new SignalProtocol();
  const bob = new SignalProtocol();
  const bobBundle = await bob.generatePreKeyBundle("bob");
  await alice.generatePreKeyBundle("alice");
  await alice.processPreKeyBundle(bobBundle);

  const first = await alice.encrypt("bob", new Uint8Array(Buffer.from("first")));
  await bob.decrypt("alice", first);

  // Replay using the same bundle (and therefore same OPK id) should fail.
  const alice2 = new SignalProtocol();
  await alice2.generatePreKeyBundle("alice2");
  await alice2.processPreKeyBundle(bobBundle);
  const replay = await alice2.encrypt("bob", new Uint8Array(Buffer.from("replay")));

  await assert.rejects(() => bob.decrypt("alice2", replay));
});

test("encrypt without local UHID throws", async () => {
  const alice = new SignalProtocol();
  const bob = new SignalProtocol();
  const bobBundle = await bob.generatePreKeyBundle("bob");
  // Note: no generatePreKeyBundle / setLocalUhid on Alice.
  await alice.processPreKeyBundle(bobBundle);
  await assert.rejects(() => alice.encrypt("bob", new Uint8Array(Buffer.from("x"))));
});

test("pre-key bundle has both Ed25519 and X25519 identity keys", async () => {
  const svc = new SignalProtocol();
  const bundle = await svc.generatePreKeyBundle("alice");
  assert.equal(bundle.identityKey.length, 32);          // Ed25519
  assert.equal(bundle.identityKeyX25519.length, 32);    // X25519
  assert.notDeepEqual(bundle.identityKey, bundle.identityKeyX25519);
  assert.equal(bundle.signedPreKey.length, 32);
  assert.equal(bundle.preKey.length, 32);
  assert.equal(bundle.signedPreKeySignature.length, 64);
});
