/**
 * Security acceptance tests for fail-closed RREP verification (Gap 3).
 * Mirrors the C# tests/AetherNet.Core.Tests/RouteReplyVerificationTests.cs.
 *
 * Proves the properties of the hardened routing layer:
 *   (a) a RoutingService with NO verifier supplied REJECTS an RREP — no
 *       forward route installed (fail-closed default);
 *   (b) an Ed25519RouteReplyVerifier whose resolver returns the correct public
 *       key ACCEPTS a validly-signed RREP — forward route installed;
 *   (c) a forged RREP (signed by a DIFFERENT key), an unsigned RREP, and an
 *       unknown-signer RREP are ALL rejected.
 *
 * Signed RREPs are built with a real Ed25519 keypair via the production signing
 * path (signPacket), so this exercises the actual signature verification, not a
 * stub. Assertions are on the observable side effect: presence/absence of the
 * forward route in the store.
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/routeReplyVerification.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { DEFAULT_TTL } from "../src/constants.js";
import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import {
  Ed25519RouteReplyVerifier,
  InMemoryRouteStore,
  IRouteReplyKeyResolver,
  RoutingService,
} from "../src/routing/index.js";
import { signPacket } from "../src/security/PacketSigning.js";
import { Ed25519Service } from "../src/security/Ed25519Service.js";
import { FakeMeshSender } from "./fakes.js";

const LOCAL = "local-uhid";
const SOURCE = "carol";

function newRrep(source = SOURCE, destination = LOCAL, ttl = DEFAULT_TTL): MeshPacket {
  const p = new MeshPacket();
  p.type = PacketType.RouteReply;
  p.sourceUhid = source;
  p.destinationUhid = destination;
  p.ttl = ttl;
  return p;
}

/** Sign an RREP in-place with the given Ed25519 private key (real signing path). */
function signRrep(rrep: MeshPacket, privateKey: Uint8Array): MeshPacket {
  signPacket(rrep, privateKey);
  return rrep;
}

/** Minimal in-test UHID→public-key map for the routing verifier. */
class StubKeyResolver implements IRouteReplyKeyResolver {
  private readonly keys = new Map<string, Uint8Array>();
  constructor(uhid?: string, publicKey?: Uint8Array) {
    if (uhid !== undefined && publicKey !== undefined) this.keys.set(uhid, publicKey);
  }
  resolvePublicKey(sourceUhid: string): Uint8Array | undefined {
    return this.keys.get(sourceUhid);
  }
}

describe("RouteReplyVerification — fail-closed RREP (Gap 3)", () => {
  // ── (a) No verifier ⇒ fail-closed reject ──────────────────────────────────
  it("no verifier rejects RREP — no route installed", async () => {
    const sender = new FakeMeshSender(LOCAL);
    const store = new InMemoryRouteStore();
    // No verifier argument at all — the fail-closed default (RejectAll) applies.
    const svc = new RoutingService(sender, store);

    await svc.handleRouteReply(newRrep());

    assert.equal(await store.get(SOURCE), null); // route rejected — not installed
    assert.equal(svc.getCachedRoute(SOURCE), null);
  });

  // ── (b) Ed25519 verifier + correct key + valid signature ⇒ accept ─────────
  it("Ed25519 verifier installs a validly-signed forward route", async () => {
    const sender = new FakeMeshSender(LOCAL);
    const store = new InMemoryRouteStore();

    // The source node's real identity. Its public key is registered with the resolver.
    const sourceKeys = Ed25519Service.generateKeyPair();
    const resolver = new StubKeyResolver(SOURCE, sourceKeys.publicKey);
    const verifier = new Ed25519RouteReplyVerifier(resolver);
    const svc = new RoutingService(sender, store, verifier);

    const signedRrep = signRrep(newRrep(), sourceKeys.privateKey);
    await svc.handleRouteReply(signedRrep);

    const route = await store.get(SOURCE);
    assert.ok(route);
    assert.equal(route!.nextHopUhid, SOURCE);
  });

  // ── (c) Forged (wrong-key) signature ⇒ reject ─────────────────────────────
  it("forged RREP signed by a different key is rejected", async () => {
    const sender = new FakeMeshSender(LOCAL);
    const store = new InMemoryRouteStore();

    // Resolver knows the LEGITIMATE source key...
    const legitimate = Ed25519Service.generateKeyPair();
    const resolver = new StubKeyResolver(SOURCE, legitimate.publicKey);
    const verifier = new Ed25519RouteReplyVerifier(resolver);
    const svc = new RoutingService(sender, store, verifier);

    // ...but the attacker signs the RREP (claiming to be "carol") with a DIFFERENT key.
    const attacker = Ed25519Service.generateKeyPair();
    const forgedRrep = signRrep(newRrep(), attacker.privateKey);

    await svc.handleRouteReply(forgedRrep);

    assert.equal(await store.get(SOURCE), null); // forged signature rejected — no route
  });

  // ── (c) Unsigned RREP ⇒ reject ────────────────────────────────────────────
  it("unsigned RREP is rejected", async () => {
    const sender = new FakeMeshSender(LOCAL);
    const store = new InMemoryRouteStore();

    const sourceKeys = Ed25519Service.generateKeyPair();
    const resolver = new StubKeyResolver(SOURCE, sourceKeys.publicKey);
    const verifier = new Ed25519RouteReplyVerifier(resolver);
    const svc = new RoutingService(sender, store, verifier);

    // RREP with an empty signature (the MeshPacket default) — must be rejected.
    await svc.handleRouteReply(newRrep());

    assert.equal(await store.get(SOURCE), null);
  });

  // ── (c') Unknown signer (resolver returns undefined) ⇒ reject ─────────────
  it("unknown-signer RREP is rejected", async () => {
    const sender = new FakeMeshSender(LOCAL);
    const store = new InMemoryRouteStore();

    // Resolver knows nobody — even a validly self-signed RREP is rejected.
    const resolver = new StubKeyResolver(); // empty
    const verifier = new Ed25519RouteReplyVerifier(resolver);
    const svc = new RoutingService(sender, store, verifier);

    const sourceKeys = Ed25519Service.generateKeyPair();
    const signedRrep = signRrep(newRrep(), sourceKeys.privateKey);

    await svc.handleRouteReply(signedRrep);

    assert.equal(await store.get(SOURCE), null);
  });
});
