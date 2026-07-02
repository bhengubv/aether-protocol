/**
 * Unit tests for the ForgeAnnounce WIRE binding (PacketType.ForgeAnnounce = 41). Uses a fake
 * IMeshSender — no transport needed. Mirrors the C# WirePacketsTests ForgeAnnounce cases, plus
 * the canonical byte-identity gate from fixtures/forge/vectors.json.
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/forgeAnnounce.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import {
  ForgeAnnounceService,
  serializeForgeAnnouncePayload,
  type ForgeAnnouncePayload,
} from "../src/forge/index.js";
import { FakeMeshSender } from "./fakes.js";

// ── canonical byte-identity gate ──────────────────────────────────────────────

describe("ForgeAnnouncePayload — canonical byte-identity", () => {
  // Mirrors ForgeAnnounce_SerializesToCanonicalBytes.
  it("serializes basic to canonical bytes", () => {
    assert.equal(
      serializeForgeAnnouncePayload({
        packageId: "npm:react@18.2.0",
        contentHash: "QmForgeHash456",
        sizeBytes: 294912,
        announcedAtMs: 1_700_000_000_000,
      }),
      '{"package_id":"npm:react@18.2.0","content_hash":"QmForgeHash456","size_bytes":294912,"announced_at_ms":1700000000000}',
    );
  });

  // Cross-language parity: reproduce every vector in fixtures/forge/vectors.json.
  it("reproduces every fixture vector byte-for-byte", () => {
    const vectorsPath = fileURLToPath(
      new URL("../../fixtures/forge/vectors.json", import.meta.url),
    );
    const V = JSON.parse(readFileSync(vectorsPath, "utf8")) as {
      vectors: {
        name: string;
        package_id: string;
        content_hash: string;
        size_bytes: number;
        announced_at_ms: number;
        expected_json: string;
      }[];
    };
    assert.ok(V.vectors.length >= 1, "fixture must carry at least the reference vector");
    for (const vec of V.vectors) {
      assert.equal(
        serializeForgeAnnouncePayload({
          packageId: vec.package_id,
          contentHash: vec.content_hash,
          sizeBytes: vec.size_bytes,
          announcedAtMs: vec.announced_at_ms,
        }),
        vec.expected_json,
        `canonical bytes for vector "${vec.name}"`,
      );
    }
  });
});

// ── broadcast + handle ────────────────────────────────────────────────────────

describe("ForgeAnnounceService — broadcast + handle", () => {
  // Mirrors Forge_Broadcast_EmitsAnnouncePacket_AndHandleRaisesEvent.
  it("broadcasts an announce packet and handle raises the received event", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    const svc = new ForgeAnnounceService(sender);

    const reached = await svc.broadcast("npm:react@18.2.0", "QmForgeHash456", 294912, 1_700_000_000_000);
    assert.equal(reached, 0); // fake has no peers registered
    assert.equal(sender.broadcasts.length, 1);
    const sent = sender.broadcasts[0]!;
    assert.equal(sent.type, PacketType.ForgeAnnounce);
    assert.equal(sent.sourceUhid, "aether:alice:01");
    assert.equal(sent.destinationUhid, "*");

    let got: ForgeAnnouncePayload | undefined;
    svc.onAnnounceReceived = (e) => { got = e; };
    assert.equal(await svc.handle(sent), true);
    assert.ok(got);
    assert.equal(got!.packageId, "npm:react@18.2.0");
    assert.equal(got!.sizeBytes, 294912);
    assert.equal(got!.contentHash, "QmForgeHash456");
    assert.equal(got!.announcedAtMs, 1_700_000_000_000);
  });

  it("returns the delivered peer count", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    sender.addPeer({ uhid: "aether:peer:aa" } as never);
    sender.addPeer({ uhid: "aether:peer:bb" } as never);
    const delivered = await new ForgeAnnounceService(sender).broadcast(
      "pip:numpy@1.26.0",
      "QmH",
      1024,
      1_700_000_000_000,
    );
    assert.equal(delivered, 2);
  });

  // Mirrors Forge_Handle_WrongType_ReturnsFalse.
  it("rejects the wrong packet type", async () => {
    const svc = new ForgeAnnounceService(new FakeMeshSender("aether:local:01"));
    const pkt = new MeshPacket();
    pkt.type = PacketType.Data;
    pkt.payload = new Uint8Array(0);
    assert.equal(await svc.handle(pkt), false);
  });

  it("drops a malformed payload", async () => {
    const svc = new ForgeAnnounceService(new FakeMeshSender("aether:local:01"));
    const pkt = new MeshPacket();
    pkt.type = PacketType.ForgeAnnounce;
    pkt.sourceUhid = "aether:bob:02";
    pkt.payload = new TextEncoder().encode("{not valid json");
    assert.equal(await svc.handle(pkt), false);
  });
});
