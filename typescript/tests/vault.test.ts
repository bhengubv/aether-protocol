// SPDX-License-Identifier: MIT
//
// Behavioural tests for the in-memory aether-vault service: erasure-coded
// store/recover round-trip, any-K-of-N recovery, unrecoverable below K, and the
// empty-blob edge case.

import test from "node:test";
import assert from "node:assert/strict";

import { InMemoryVaultService, type VaultManifest } from "../src/vault/VaultService.js";

// Reach the private shard store for white-box loss simulation (it is a plain Map).
function shardStore(svc: InMemoryVaultService): Map<string, Uint8Array> {
  return (svc as unknown as { shards: Map<string, Uint8Array> }).shards;
}

test("vault store -> recover round-trips and reports full health", async () => {
  const svc = new InMemoryVaultService();
  const data = new Uint8Array(3333);
  for (let i = 0; i < data.length; i++) data[i] = (i * 7) % 256;

  const m: VaultManifest = await svc.store(data, "doc.bin");
  assert.equal(m.shardHashes.length, 14);
  assert.equal(m.sizeBytes, 3333);
  assert.equal(m.contentHash.length, 64);

  assert.deepEqual([...(await svc.recover(m))], [...data]);

  const h = svc.checkHealth(m);
  assert.equal(h.reachableShards, 14);
  assert.equal(h.isRecoverable, true);
  assert.ok(h.redundancyScore > 0.99);
});

test("vault recovers from any K shards; unrecoverable below K", async () => {
  const svc = new InMemoryVaultService();
  const data = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]);
  const m = await svc.store(data, "x");
  const shards = shardStore(svc);

  // Drop M (=4) shards: K (=10) survive -> still recoverable.
  for (let i = 0; i < 4; i++) shards.delete(m.shardHashes[i]);
  assert.equal(svc.checkHealth(m).reachableShards, 10);
  assert.equal(svc.checkHealth(m).isRecoverable, true);
  assert.deepEqual([...(await svc.recover(m))], [...data]);

  // Drop one more -> only K-1 remain -> unrecoverable.
  shards.delete(m.shardHashes[4]);
  assert.equal(svc.checkHealth(m).isRecoverable, false);
  await assert.rejects(() => svc.recover(m));
});

test("vault empty blob round-trips", async () => {
  const svc = new InMemoryVaultService();
  const m = await svc.store(new Uint8Array(0), "empty");
  assert.equal(m.sizeBytes, 0);
  assert.equal((await svc.recover(m)).length, 0);
});
