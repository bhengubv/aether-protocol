/**
 * Unit tests for the ProfileSync service (PacketType.ProfileSync = 23). Directed exchange — a
 * fake IMeshSender captures the directed send. Mirrors the C# ProfileSyncTests, plus the
 * canonical byte-identity gate from fixtures/profiles/vectors.json.
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/profiles.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import {
  ProfileService,
  serializeProfileSyncPayload,
} from "../src/profiles/index.js";
import type { ProfileSyncPayload } from "../src/profiles/index.js";
import { FakeMeshSender } from "./fakes.js";

const LOCAL = "aether:local:01";

function build(sender: FakeMeshSender): ProfileService {
  return new ProfileService(sender);
}

/** Build a real ProfileSync packet from a peer with the canonical payload. */
function profilePacket(
  uhid: string,
  name: string,
  avatar: string,
  status: string,
  updatedAtMs: number,
): MeshPacket {
  const pkt = new MeshPacket();
  pkt.type = PacketType.ProfileSync;
  pkt.sourceUhid = uhid;
  pkt.destinationUhid = LOCAL;
  pkt.payload = new TextEncoder().encode(
    serializeProfileSyncPayload({
      uhid,
      displayName: name,
      avatarRef: avatar,
      statusMessage: status,
      updatedAtMs,
    }),
  );
  return pkt;
}

// ── canonical byte-identity gate ──────────────────────────────────────────────

describe("ProfileSyncPayload — canonical byte-identity", () => {
  // Mirrors [InlineData] in ProfileSyncTests.ProfileSyncPayload_SerializesToCanonicalBytes.
  it("serializes vector 1 (basic) to canonical bytes", () => {
    assert.equal(
      serializeProfileSyncPayload({
        uhid: "aether:alice:01",
        displayName: "Alice",
        avatarRef: "blake3:abc",
        statusMessage: "available",
        updatedAtMs: 1_700_000_000_000,
      }),
      '{"uhid":"aether:alice:01","display_name":"Alice","avatar_ref":"blake3:abc","status_message":"available","updated_at_ms":1700000000000}',
    );
  });

  it("serializes vector 2 (minimal) to canonical bytes", () => {
    assert.equal(
      serializeProfileSyncPayload({
        uhid: "n",
        displayName: "",
        avatarRef: "",
        statusMessage: "",
        updatedAtMs: 0,
      }),
      '{"uhid":"n","display_name":"","avatar_ref":"","status_message":"","updated_at_ms":0}',
    );
  });

  // Cross-language parity: reproduce every vector in fixtures/profiles/vectors.json.
  it("reproduces every fixture vector byte-for-byte", () => {
    const vectorsPath = fileURLToPath(
      new URL("../../fixtures/profiles/vectors.json", import.meta.url),
    );
    const V = JSON.parse(readFileSync(vectorsPath, "utf8")) as {
      vectors: {
        name: string;
        uhid: string;
        display_name: string;
        avatar_ref: string;
        status_message: string;
        updated_at_ms: number;
        expected_json: string;
      }[];
    };
    assert.ok(V.vectors.length >= 2, "fixture must carry at least the two reference vectors");
    for (const vec of V.vectors) {
      assert.equal(
        serializeProfileSyncPayload({
          uhid: vec.uhid,
          displayName: vec.display_name,
          avatarRef: vec.avatar_ref,
          statusMessage: vec.status_message,
          updatedAtMs: vec.updated_at_ms,
        }),
        vec.expected_json,
        `canonical bytes for vector "${vec.name}"`,
      );
    }
  });
});

// ── publishProfileTo ──────────────────────────────────────────────────────────

describe("ProfileService — publishProfileTo", () => {
  it("sends a directed profile to the peer", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    const svc = build(sender);
    svc.setLocalProfile("Alice", "blake3:abc", "available");

    const ok = await svc.publishProfileTo("aether:bob:02");

    assert.equal(ok, true);
    assert.equal(sender.unicasts.length, 1);
    const sent = sender.unicasts[0]!;
    assert.equal(sent.packet.type, PacketType.ProfileSync);
    assert.equal(sent.nextHopUhid, "aether:bob:02");
    assert.equal(sent.packet.destinationUhid, "aether:bob:02");

    const body = JSON.parse(new TextDecoder().decode(sent.packet.payload));
    assert.equal(body.uhid, "aether:alice:01");
    assert.equal(body.display_name, "Alice");
  });
});

// ── handle ────────────────────────────────────────────────────────────────────

describe("ProfileService — handle", () => {
  it("caches the peer profile and raises onProfileUpdated", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    let updated: ProfileSyncPayload | undefined;
    svc.onProfileUpdated = (e) => { updated = e; };

    const ok = await svc.handle(
      profilePacket("aether:bob:02", "Bob", "blake3:xyz", "busy", 1_700_000_000_000),
    );

    assert.equal(ok, true);
    assert.ok(updated);
    assert.equal(updated!.displayName, "Bob");

    const cached = svc.getProfile("aether:bob:02");
    assert.ok(cached);
    assert.equal(cached!.statusMessage, "busy");
    assert.equal(svc.getKnownProfiles().length, 1);
  });

  it("refreshes an existing profile (no duplicate entry)", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    await svc.handle(profilePacket("aether:bob:02", "Bob", "", "here", 1000));
    await svc.handle(profilePacket("aether:bob:02", "Bob", "", "away", 2000));

    const cached = svc.getProfile("aether:bob:02");
    assert.equal(cached!.statusMessage, "away");
    assert.equal(svc.getKnownProfiles().length, 1);
  });

  it("ignores our own profile echoed back", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    const ok = await svc.handle(profilePacket(LOCAL, "Me", "", "", 1));
    assert.equal(ok, false);
    assert.equal(svc.getKnownProfiles().length, 0);
  });

  it("rejects the wrong packet type", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    const pkt = profilePacket("aether:bob:02", "Bob", "", "", 1);
    pkt.type = PacketType.Data;
    assert.equal(await svc.handle(pkt), false);
  });

  it("drops a malformed payload", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    const pkt = new MeshPacket();
    pkt.type = PacketType.ProfileSync;
    pkt.sourceUhid = "aether:bob:02";
    pkt.payload = new TextEncoder().encode("{not valid json");
    assert.equal(await svc.handle(pkt), false);
  });
});

// ── local profile ─────────────────────────────────────────────────────────────

describe("ProfileService — local profile", () => {
  it("defaults to the local uhid with empty fields", () => {
    const svc = build(new FakeMeshSender(LOCAL));
    const local = svc.getLocalProfile();
    assert.equal(local.uhid, LOCAL);
    assert.equal(local.displayName, "");
    assert.equal(local.avatarRef, "");
    assert.equal(local.statusMessage, "");
  });

  it("setLocalProfile stamps updatedAtMs and keeps the local uhid", () => {
    const svc = build(new FakeMeshSender("aether:alice:01"));
    const before = Date.now();
    svc.setLocalProfile("Alice", "blake3:abc", "available");
    const local = svc.getLocalProfile();
    assert.equal(local.uhid, "aether:alice:01");
    assert.equal(local.displayName, "Alice");
    assert.equal(local.avatarRef, "blake3:abc");
    assert.equal(local.statusMessage, "available");
    assert.ok(local.updatedAtMs >= before);
  });
});
