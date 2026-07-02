/**
 * Unit tests for the VaultShardRequest WIRE binding (PacketType.VaultShardRequest = 42). Uses a
 * fake IMeshSender — no transport needed. Mirrors the C# WirePacketsTests VaultShardRequest cases,
 * plus the canonical byte-identity gate from fixtures/vaultshard/vectors.json.
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/vaultShardRequest.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import {
  VaultShardRequestService,
  serializeVaultShardRequestPayload,
  deserializeVaultShardRequestPayload,
  type VaultShardRequest,
} from "../src/vaultshard/index.js";
import { FakeMeshSender } from "./fakes.js";

// ── canonical byte-identity gate ──────────────────────────────────────────────

describe("VaultShardRequestPayload — canonical byte-identity", () => {
  // Mirrors VaultShardRequest_SerializesToCanonicalBytes.
  it("serializes basic to canonical bytes", () => {
    assert.equal(
      serializeVaultShardRequestPayload({
        shardHash: "QmShardHash789",
        requesterUhid: "aether:bob:02",
      }),
      '{"shard_hash":"QmShardHash789","requester_uhid":"aether:bob:02"}',
    );
  });

  // Cross-language parity: reproduce every vector in fixtures/vaultshard/vectors.json.
  it("reproduces every fixture vector byte-for-byte", () => {
    const vectorsPath = fileURLToPath(
      new URL("../../fixtures/vaultshard/vectors.json", import.meta.url),
    );
    const V = JSON.parse(readFileSync(vectorsPath, "utf8")) as {
      vectors: {
        name: string;
        shard_hash: string;
        requester_uhid: string;
        expected_json: string;
      }[];
    };
    assert.ok(V.vectors.length >= 1, "fixture must carry at least the reference vector");
    for (const vec of V.vectors) {
      assert.equal(
        serializeVaultShardRequestPayload({
          shardHash: vec.shard_hash,
          requesterUhid: vec.requester_uhid,
        }),
        vec.expected_json,
        `canonical bytes for vector "${vec.name}"`,
      );
    }
  });
});

// ── requestShard + handle ─────────────────────────────────────────────────────

describe("VaultShardRequestService — requestShard + handle", () => {
  // Mirrors Vault_Request_EmitsShardRequestPacket_AndHandleRaisesEvent.
  it("broadcasts a shard-request packet (requester = local) and handle raises the event", async () => {
    const sender = new FakeMeshSender("aether:bob:02");
    const svc = new VaultShardRequestService(sender);

    const reached = await svc.requestShard("QmShardHash789");
    assert.equal(reached, 0); // fake has no peers registered
    assert.equal(sender.broadcasts.length, 1);
    const sent = sender.broadcasts[0]!;
    assert.equal(sent.type, PacketType.VaultShardRequest);
    assert.equal(sent.sourceUhid, "aether:bob:02");
    assert.equal(sent.destinationUhid, "*");

    const body = deserializeVaultShardRequestPayload(sent.payload);
    assert.equal(body.shardHash, "QmShardHash789");
    assert.equal(body.requesterUhid, "aether:bob:02"); // requester = sender.localUhid

    let got: VaultShardRequest | undefined;
    svc.onShardRequested = (e) => { got = e; };
    assert.equal(await svc.handle(sent), true);
    assert.ok(got);
    assert.equal(got!.shardHash, "QmShardHash789");
    assert.equal(got!.requesterUhid, "aether:bob:02");
  });

  it("returns the delivered peer count", async () => {
    const sender = new FakeMeshSender("aether:bob:02");
    sender.addPeer({ uhid: "aether:peer:aa" } as never);
    sender.addPeer({ uhid: "aether:peer:bb" } as never);
    assert.equal(await new VaultShardRequestService(sender).requestShard("QmShard"), 2);
  });

  // Mirrors Vault_Handle_WrongType_ReturnsFalse.
  it("rejects the wrong packet type", async () => {
    const svc = new VaultShardRequestService(new FakeMeshSender("aether:local:01"));
    const pkt = new MeshPacket();
    pkt.type = PacketType.Data;
    pkt.payload = new Uint8Array(0);
    assert.equal(await svc.handle(pkt), false);
  });

  it("drops a malformed payload", async () => {
    const svc = new VaultShardRequestService(new FakeMeshSender("aether:local:01"));
    const pkt = new MeshPacket();
    pkt.type = PacketType.VaultShardRequest;
    pkt.sourceUhid = "aether:bob:02";
    pkt.payload = new TextEncoder().encode("{not valid json");
    assert.equal(await svc.handle(pkt), false);
  });
});
