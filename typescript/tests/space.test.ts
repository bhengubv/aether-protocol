// SPDX-License-Identifier: MIT
//
// Behavioural tests for the in-memory aether-space breadcrumb noticeboard: drop
// (TTL clamp + emergency override + received callback), geohash-prefix scan,
// creator-only delete, and prune.

import test from "node:test";
import assert from "node:assert/strict";

import { InMemorySpaceService, BreadcrumbType } from "../src/space/SpaceService.js";

test("space drop/scan/delete/prune behaviour", async () => {
  const svc = new InMemorySpaceService();
  let received = 0;
  svc.onBreadcrumbReceived = () => {
    received++;
  };

  const a = await svc.drop("k3vf9z", "hashA", "anchor1", BreadcrumbType.Notice, 24);
  assert.equal(a.ttlHours, 24);
  assert.equal(received, 1);

  // Emergency breadcrumbs get the fixed 720h TTL.
  const e = await svc.drop("k3vf9z", "hashE", "anchor1", BreadcrumbType.Emergency, 1);
  assert.equal(e.ttlHours, 720);

  // Scan: prefix-proximity hit vs a far cell.
  assert.equal((await svc.scan("k3vf9z", 1)).length, 2);
  assert.equal((await svc.scan("xxxxxx", 1)).length, 0);

  // Creator-only delete.
  assert.equal(await svc.delete(a, "wrong"), false);
  assert.equal(await svc.delete(a, "anchor1"), true);
  assert.equal((await svc.scan("k3vf9z", 1)).length, 1);

  // Nothing is past its TTL yet.
  assert.equal(svc.pruneExpired(), 0);
});
