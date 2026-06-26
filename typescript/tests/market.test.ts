// SPDX-License-Identifier: MIT
//
// Behavioural tests for the in-memory aether-market services: the marketplace
// (create/browse/search + the trade-escrow state machine) and the single-node
// Proof-of-Vicinity service (issue/verify/accept/score + defection penalty).

import test from "node:test";
import assert from "node:assert/strict";

import {
  InMemoryMarketService,
  MarketCategory,
  TradeRole,
  TradeState,
} from "../src/market/MarketService.js";
import { InMemoryPoVService } from "../src/market/PoVService.js";
import { PoVTransportType } from "../src/market/PoVToken.js";

test("marketplace: create -> browse -> search -> trade state machine -> dispute", async () => {
  const m = new InMemoryMarketService();
  let received: { listingId: string } | null = null;
  m.onListingReceived = (l) => {
    received = l;
  };

  const l = await m.createListing("seller1", "Bicycle", "Red mountain bike", 1500, "k3vf9z", MarketCategory.Goods);
  assert.ok(l.listingId);
  assert.equal(received!.listingId, l.listingId);

  assert.equal((await m.browseNearby("k3vf9z", 2)).length, 1);
  assert.equal((await m.browseNearby("xxxxxx", 2)).length, 0);
  assert.equal((await m.search("bike")).length, 1);
  assert.equal((await m.search("bike", MarketCategory.Services)).length, 0);

  let e = await m.initiateTrade(l, "buyer1");
  assert.equal(e.state, TradeState.Initiated);
  e = await m.confirmTrade(e, TradeRole.Buyer);
  assert.equal(e.state, TradeState.BuyerConfirmed);
  e = await m.confirmTrade(e, TradeRole.Seller);
  assert.equal(e.state, TradeState.Complete);

  const e2 = await m.initiateTrade(l, "buyer2");
  await m.dispute(e2, "item not as described");
  assert.equal(e2.state, TradeState.Disputed);
});

test("PoV: issue/verify/accept/score, tamper + self-vouch rejected, defection penalty", async () => {
  const p = new InMemoryPoVService();

  const tok = await p.issueToken("w1", "A", PoVTransportType.Ble);
  assert.equal(await p.verifyToken(tok), true);
  await p.acceptToken(tok);

  const sc = await p.getScore("A");
  assert.equal(sc.uniqueWitnesses, 1);
  assert.ok(Math.abs(sc.weightedScore - 0.5) < 1e-9);

  // Tampering with the body invalidates the signatures.
  tok.subjectUhid = "C";
  assert.equal(await p.verifyToken(tok), false);

  // A node cannot vouch for itself.
  const self = await p.issueToken("x", "x", PoVTransportType.Nfc);
  assert.equal(await p.verifyToken(self), false);

  // Defection penalty: A's score 0.5 -> 0.4.
  await p.reportDefection("A", "victim");
  assert.ok(Math.abs((await p.getScore("A")).weightedScore - 0.4) < 1e-9);
});
