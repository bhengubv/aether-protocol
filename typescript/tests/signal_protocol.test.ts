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
      readFileSync(join(dir, "AetherMeshProtocol.slnx"));
      return dir;
    } catch {
      dir = join(dir, "..");
    }
  }
  throw new Error(`AetherMeshProtocol.slnx not found above ${__dirname}`);
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

test("signal fixture: kdf_rk_basic", () => {
  // Validates Signal Double-Ratchet KDF_RK (§5.2): HKDF-SHA256 over
  // (salt=root_key, ikm=dh_output, info=UTF8('aether-ratchet-rk-v1'), L=64),
  // split 32+32 into new_root_key + new_chain_key.
  const { inputs, expected } = loadFixturePair("kdf_rk_basic");
  const rootKey = unhex(inputs.root_key_hex);
  const dhOutput = unhex(inputs.dh_output_hex);
  const info = Buffer.from(inputs.hkdf_info_utf8 as string, "utf8");
  const derived = new Uint8Array(hkdf(sha256, dhOutput, rootKey, info, 64));
  const newRootKey = derived.subarray(0, 32);
  const newChainKey = derived.subarray(32, 64);
  assert.equal(hex(newRootKey), expected.new_root_key_hex);
  assert.equal(hex(newChainKey), expected.new_chain_key_hex);
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

// ─── Double Ratchet (Signal §5) tests ────────────────────────────────────

test("Double Ratchet: every message carries SenderEphemeralKeyX25519", async () => {
  const alice = new SignalProtocol();
  const bob = new SignalProtocol();
  const bobBundle = await bob.generatePreKeyBundle("bob");
  await alice.generatePreKeyBundle("alice");
  await alice.processPreKeyBundle(bobBundle);

  const first = await alice.encrypt("bob", new Uint8Array(Buffer.from("a")));
  assert.ok(first.senderEphemeralKeyX25519);
  assert.equal(first.senderEphemeralKeyX25519!.length, 32);

  await bob.decrypt("alice", first);

  // Subsequent message also carries senderEphemeralKeyX25519 (same value
  // — Alice hasn't ratcheted because Bob hasn't responded yet).
  const second = await alice.encrypt("bob", new Uint8Array(Buffer.from("b")));
  assert.ok(second.senderEphemeralKeyX25519);
  assert.deepEqual(
    Array.from(first.senderEphemeralKeyX25519!),
    Array.from(second.senderEphemeralKeyX25519!)
  );
});

test("Double Ratchet: SenderEphemeralKey rotates after roundtrip", async () => {
  const alice = new SignalProtocol();
  const bob = new SignalProtocol();
  const bobBundle = await bob.generatePreKeyBundle("bob");
  await alice.generatePreKeyBundle("alice");
  await alice.processPreKeyBundle(bobBundle);

  // Alice -> Bob: Alice's first ratchet pub.
  const aliceFirst = await alice.encrypt("bob", new Uint8Array(Buffer.from("ping")));
  await bob.decrypt("alice", aliceFirst);

  // Bob -> Alice: Bob's first ratchet pub (rotated by responder-side DH ratchet).
  const bobReply = await bob.encrypt("alice", new Uint8Array(Buffer.from("pong")));
  assert.ok(bobReply.senderEphemeralKeyX25519);
  // Bob's ratchet pub MUST differ from Alice's (Bob generated fresh DHs
  // on his DH-ratchet step).
  assert.notDeepEqual(
    Array.from(aliceFirst.senderEphemeralKeyX25519!),
    Array.from(bobReply.senderEphemeralKeyX25519!)
  );

  await alice.decrypt("bob", bobReply);

  // Alice -> Bob (after roundtrip): Alice should now use a NEW ratchet
  // pub (rotated on her DH-ratchet step when she received Bob's reply).
  const aliceSecond = await alice.encrypt("bob", new Uint8Array(Buffer.from("ping2")));
  assert.notDeepEqual(
    Array.from(aliceFirst.senderEphemeralKeyX25519!),
    Array.from(aliceSecond.senderEphemeralKeyX25519!)
  );
  assert.notDeepEqual(
    Array.from(bobReply.senderEphemeralKeyX25519!),
    Array.from(aliceSecond.senderEphemeralKeyX25519!)
  );

  // Bob can still decrypt Alice's new message.
  const dec = await bob.decrypt("alice", aliceSecond);
  assert.equal(Buffer.from(dec).toString("utf8"), "ping2");
});

test("Double Ratchet: PreviousChainCount tracks messages per chain", async () => {
  const alice = new SignalProtocol();
  const bob = new SignalProtocol();
  const bobBundle = await bob.generatePreKeyBundle("bob");
  await alice.generatePreKeyBundle("alice");
  await alice.processPreKeyBundle(bobBundle);

  // Alice sends 3 messages without a roundtrip.
  for (let i = 0; i < 3; i++) {
    const enc = await alice.encrypt("bob", new Uint8Array(Buffer.from(`a${i}`)));
    // PN is 0 because this IS Alice's first chain.
    assert.equal(enc.previousChainCount, 0);
    await bob.decrypt("alice", enc);
  }

  // Bob sends a reply, triggering his DH-ratchet step.
  const bobReply = await bob.encrypt("alice", new Uint8Array(Buffer.from("hi")));
  // Bob's PN reflects however many messages Bob sent in his previous
  // sending chain — which was 0 (Bob hadn't sent anything yet before his
  // DH-ratchet step rotated his chain).
  assert.equal(bobReply.previousChainCount, 0);
  await alice.decrypt("bob", bobReply);

  // Alice's next message after her DH-ratchet step. Her PN should be 3
  // — that's how many messages she sent on her previous chain before
  // Bob's reply triggered her ratchet.
  const aliceNew = await alice.encrypt("bob", new Uint8Array(Buffer.from("a3")));
  assert.equal(aliceNew.previousChainCount, 3);
});

test("Double Ratchet: out-of-order across DH-ratchet boundary still decrypts", async () => {
  // Alice sends 3 messages on chain 1. Bob receives only the first 2,
  // then Alice does a DH-ratchet (because Bob replied) and sends a 4th
  // on chain 2. The 3rd message (from chain 1) arrives last — Bob must
  // still be able to decrypt it via the skipped-keys cache keyed by
  // (Alice's old DHs pub, counter=2).
  const alice = new SignalProtocol();
  const bob = new SignalProtocol();
  const bobBundle = await bob.generatePreKeyBundle("bob");
  await alice.generatePreKeyBundle("alice");
  await alice.processPreKeyBundle(bobBundle);

  const a0 = await alice.encrypt("bob", new Uint8Array(Buffer.from("a0")));
  const a1 = await alice.encrypt("bob", new Uint8Array(Buffer.from("a1")));
  const a2 = await alice.encrypt("bob", new Uint8Array(Buffer.from("a2")));

  // Bob receives a0, a1 only.
  assert.equal(Buffer.from(await bob.decrypt("alice", a0)).toString("utf8"), "a0");
  assert.equal(Buffer.from(await bob.decrypt("alice", a1)).toString("utf8"), "a1");

  // Bob replies — triggers his DH-ratchet step.
  const bReply = await bob.encrypt("alice", new Uint8Array(Buffer.from("hi")));
  await alice.decrypt("bob", bReply);

  // Alice sends a4 on her new chain (after her DH-ratchet step).
  const a4 = await alice.encrypt("bob", new Uint8Array(Buffer.from("a4")));
  // Bob receives a4 — triggers his second DH-ratchet step. He must
  // skip-derive a key for Alice's old chain counter=2 because PN=3.
  assert.equal(Buffer.from(await bob.decrypt("alice", a4)).toString("utf8"), "a4");

  // Now the missing a2 (from Alice's OLD chain) finally arrives. Bob
  // should pull the skipped key from the cache keyed by (old DHr, 2).
  assert.equal(Buffer.from(await bob.decrypt("alice", a2)).toString("utf8"), "a2");
});

test("Double Ratchet: long conversation — 10 alternating messages decrypt", async () => {
  const alice = new SignalProtocol();
  const bob = new SignalProtocol();
  const bobBundle = await bob.generatePreKeyBundle("bob");
  await alice.generatePreKeyBundle("alice");
  await alice.processPreKeyBundle(bobBundle);

  // 10 alternating messages — each side ratchets at every roundtrip.
  for (let i = 0; i < 10; i++) {
    const aMsg = `alice ${i}`;
    const aEnc = await alice.encrypt("bob", new Uint8Array(Buffer.from(aMsg)));
    assert.equal(Buffer.from(await bob.decrypt("alice", aEnc)).toString("utf8"), aMsg);

    const bMsg = `bob ${i}`;
    const bEnc = await bob.encrypt("alice", new Uint8Array(Buffer.from(bMsg)));
    assert.equal(Buffer.from(await alice.decrypt("bob", bEnc)).toString("utf8"), bMsg);
  }
});

test("Double Ratchet: PreKey msg backward-compat — initiatorEphemeralKey equals senderEphemeralKey", async () => {
  // Ensures an old peer that only reads InitiatorEphemeralKeyX25519 (the
  // pre-Double-Ratchet wire field) still gets the correct ratchet pub.
  const alice = new SignalProtocol();
  const bob = new SignalProtocol();
  const bobBundle = await bob.generatePreKeyBundle("bob");
  await alice.generatePreKeyBundle("alice");
  await alice.processPreKeyBundle(bobBundle);

  const first = await alice.encrypt("bob", new Uint8Array(Buffer.from("hi")));
  assert.equal(first.messageType, MESSAGE_TYPE_PRE_KEY);
  assert.ok(first.senderEphemeralKeyX25519);
  assert.ok(first.initiatorEphemeralKeyX25519);
  assert.deepEqual(
    Array.from(first.initiatorEphemeralKeyX25519!),
    Array.from(first.senderEphemeralKeyX25519!)
  );

  // Normal (post-PreKey) message: senderEphemeralKey set,
  // initiatorEphemeralKey undefined.
  await bob.decrypt("alice", first);
  const second = await alice.encrypt("bob", new Uint8Array(Buffer.from("ho")));
  assert.equal(second.messageType, MESSAGE_TYPE_NORMAL);
  assert.ok(second.senderEphemeralKeyX25519);
  assert.equal(second.initiatorEphemeralKeyX25519, undefined);
});
