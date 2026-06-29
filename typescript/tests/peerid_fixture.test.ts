/**
 * Cross-language PeerId parity: TS must reproduce the validated libp2p PeerID corpus
 * (fixtures/peerid/) byte-for-byte. Every AetherNet SDK derives the SAME PeerID from the
 * SAME Ed25519 public key. SPDX-License-Identifier: MIT
 */
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { fromEd25519PublicKey } from "../src/identity/PeerId.js";

interface PeerIdInput {
  name: string;
  description?: string;
  pubkey_hex: string;
}

const inputsPath = fileURLToPath(new URL("../../fixtures/peerid/inputs.json", import.meta.url));
const inputs: PeerIdInput[] = JSON.parse(readFileSync(inputsPath, "utf8"));

const expectedFor = (name: string): string =>
  readFileSync(
    fileURLToPath(new URL(`../../fixtures/peerid/expected/${name}.txt`, import.meta.url)),
    "utf8",
  ).trim();

test("PeerId byte-parity with the validated libp2p fixture corpus", () => {
  assert.ok(inputs.length > 0, "no PeerId inputs");

  for (const input of inputs) {
    const pubkey = Buffer.from(input.pubkey_hex, "hex");
    const peerId = fromEd25519PublicKey(pubkey);
    assert.equal(peerId, expectedFor(input.name), `PeerID for ${input.name}`);
    assert.ok(peerId.startsWith("12D3Koo"), `PeerID for ${input.name} must start with 12D3Koo`);
  }
});

test("PeerId rejects a public key that is not exactly 32 bytes", () => {
  assert.throws(() => fromEd25519PublicKey(new Uint8Array(31)), /must be 32 bytes/);
  assert.throws(() => fromEd25519PublicKey(new Uint8Array(33)), /must be 32 bytes/);
});
