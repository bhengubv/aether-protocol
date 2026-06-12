/**
 * Cross-language ERID parity: TS must reproduce the C# reference vectors
 * (fixtures/erid/vectors.json) byte-for-byte. SPDX-License-Identifier: MIT
 */
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { deriveRoutingKey, deriveForEpoch, derive } from "../src/identity/EphemeralRoutingId.js";
import { encode } from "../src/identity/EridAnnouncementCodec.js";
import { EridDirectory } from "../src/identity/EridDirectory.js";

const vectorsPath = fileURLToPath(new URL("../../fixtures/erid/vectors.json", import.meta.url));
const V = JSON.parse(readFileSync(vectorsPath, "utf8"));

const hex = (b: Uint8Array) => Buffer.from(b).toString("hex");

test("ERID byte-parity with the C# reference fixture", () => {
  const rk = deriveRoutingKey(new TextEncoder().encode(V.secret_ascii));
  assert.equal(hex(rk), V.routing_key_hex, "routingKey");

  for (const v of V.erids_by_epoch) {
    assert.equal(deriveForEpoch(rk, v.epoch), v.erid, `epoch ${v.epoch}`);
  }
  for (const v of V.derive_by_unixseconds) {
    assert.equal(derive(rk, v.unix), v.erid, `unix ${v.unix}`);
  }
  assert.equal(hex(encode(rk)), V.announcement_encode_hex, "announcement frame");
});

test("EridDirectory: an established peer resolves a rotating ERID; an outsider cannot", () => {
  const aKey = deriveRoutingKey(new TextEncoder().encode("identity-A"));
  const bKey = deriveRoutingKey(new TextEncoder().encode("identity-B"));
  const alice = new EridDirectory(aKey);
  const bob = new EridDirectory(bKey);
  alice.rememberPeer("bob", bKey);
  bob.rememberPeer("alice", aKey);
  const t = 1_700_000_000;

  assert.equal(alice.eridForPeer("bob", t), bob.myErid(t));
  assert.equal(bob.resolvePeer(alice.myErid(t), t), "alice");

  const outsider = new EridDirectory(deriveRoutingKey(new TextEncoder().encode("identity-X")));
  assert.equal(outsider.resolvePeer(alice.myErid(t), t), null);
});
