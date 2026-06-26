// SPDX-License-Identifier: MIT
//
// Behavioural tests for the in-memory aether-forge package cache: cache (with the
// new-entry announcement + idempotent first-write-wins), query hit/miss, fetch
// download-count increment, and aggregate stats.

import test from "node:test";
import assert from "node:assert/strict";

import { InMemoryForgeService } from "../src/forge/ForgeService.js";

test("forge cache/query/fetch/stats behaviour", async () => {
  const svc = new InMemoryForgeService();
  let fired = 0;
  svc.onNewEntryAnnounced = () => {
    fired++;
  };

  const e = await svc.cache("npm:react@18.2.0", "hash1", 1000);
  assert.equal(e.downloadCount, 0);
  assert.equal(fired, 1);

  // Idempotent re-cache: first write wins, no second announcement.
  const e2 = await svc.cache("npm:react@18.2.0", "hash2", 9999);
  assert.equal(e2.contentHash, "hash1");
  assert.equal(fired, 1);

  // Query hit + miss.
  assert.equal((await svc.query("npm:react@18.2.0"))?.contentHash, "hash1");
  assert.equal(await svc.query("missing"), null);

  // Fetch increments the download counter; miss returns null.
  assert.equal((await svc.fetch("npm:react@18.2.0"))?.downloadCount, 1);
  await svc.fetch("npm:react@18.2.0");
  assert.equal(await svc.fetch("missing"), null);

  // Stats: bytes-saved = downloads * size; one entry catalogued.
  const st = await svc.getStats();
  assert.equal(st.catalogueSize, 1);
  assert.equal(st.totalBytesSaved, 2000); // 2 downloads * 1000 bytes
});
