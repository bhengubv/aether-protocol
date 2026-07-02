/**
 * Unit tests for the SpaceBreadcrumb WIRE binding (PacketType.SpaceBreadcrumb = 40). Uses a fake
 * IMeshSender — no transport needed. Mirrors the C# WirePacketsTests SpaceBreadcrumb cases, plus
 * the canonical byte-identity gate from fixtures/space/vectors.json.
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/spaceBreadcrumb.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import { BreadcrumbType, type SpaceBreadcrumb } from "../src/space/SpaceService.js";
import {
  SpaceBreadcrumbService,
  serializeSpaceBreadcrumbPayload,
  breadcrumbToPayload,
} from "../src/space/index.js";
import { FakeMeshSender } from "./fakes.js";

// ── canonical byte-identity gate ──────────────────────────────────────────────

describe("SpaceBreadcrumbPayload — canonical byte-identity", () => {
  // Mirrors SpaceBreadcrumb_Emergency_SerializesToCanonicalBytes.
  it("serializes emergency (signed) to canonical bytes", () => {
    assert.equal(
      serializeSpaceBreadcrumbPayload({
        contentHash: "QmContentHashExample123",
        geoHash: "u4pruy",
        anchorUhid: "aether:alice:01",
        createdAtMs: 1_700_000_000_000,
        ttlHours: 720,
        type: BreadcrumbType.Emergency,
        signatureBase64: Buffer.from(new Uint8Array(64).fill(0x99)).toString("base64"),
      }),
      '{"content_hash":"QmContentHashExample123","geo_hash":"u4pruy","anchor_uhid":"aether:alice:01",' +
        '"created_at_ms":1700000000000,"ttl_hours":720,"type":1,' +
        '"signature":"mZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmQ=="}',
    );
  });

  // Mirrors SpaceBreadcrumb_NoticeUnsigned_SerializesToCanonicalBytes.
  it("serializes notice (unsigned) to canonical bytes", () => {
    assert.equal(
      serializeSpaceBreadcrumbPayload({
        contentHash: "QmNotice777",
        geoHash: "gcpvj0",
        anchorUhid: "aether:bob:02",
        createdAtMs: 0,
        ttlHours: 72,
        type: BreadcrumbType.Notice,
        signatureBase64: Buffer.from(new Uint8Array(0)).toString("base64"),
      }),
      '{"content_hash":"QmNotice777","geo_hash":"gcpvj0","anchor_uhid":"aether:bob:02",' +
        '"created_at_ms":0,"ttl_hours":72,"type":0,"signature":""}',
    );
  });

  // Cross-language parity: reproduce every vector in fixtures/space/vectors.json.
  it("reproduces every fixture vector byte-for-byte", () => {
    const vectorsPath = fileURLToPath(
      new URL("../../fixtures/space/vectors.json", import.meta.url),
    );
    const V = JSON.parse(readFileSync(vectorsPath, "utf8")) as {
      vectors: {
        name: string;
        content_hash: string;
        geo_hash: string;
        anchor_uhid: string;
        created_at_ms: number;
        ttl_hours: number;
        type: number;
        signature: string;
        expected_json: string;
      }[];
    };
    assert.ok(V.vectors.length >= 2, "fixture must carry at least the two reference vectors");
    for (const vec of V.vectors) {
      assert.equal(
        serializeSpaceBreadcrumbPayload({
          contentHash: vec.content_hash,
          geoHash: vec.geo_hash,
          anchorUhid: vec.anchor_uhid,
          createdAtMs: vec.created_at_ms,
          ttlHours: vec.ttl_hours,
          type: vec.type as BreadcrumbType,
          signatureBase64: vec.signature,
        }),
        vec.expected_json,
        `canonical bytes for vector "${vec.name}"`,
      );
    }
  });
});

// ── broadcast + handle ────────────────────────────────────────────────────────

describe("SpaceBreadcrumbService — broadcast + handle", () => {
  // Mirrors Space_Broadcast_EmitsBreadcrumbPacket_AndHandleRaisesEvent.
  it("broadcasts a SpaceBreadcrumb packet and handle raises the received event", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    const svc = new SpaceBreadcrumbService(sender);

    const crumb: SpaceBreadcrumb = {
      contentHash: "QmX",
      geoHash: "u4pruy",
      anchorUhid: "aether:alice:01",
      createdAtUtc: new Date(1_700_000_000_000),
      ttlHours: 720,
      type: BreadcrumbType.Emergency,
      signature: new Uint8Array(64).fill(0x99),
    };

    const reached = await svc.broadcast(crumb);
    assert.equal(reached, 0); // fake has no peers registered
    assert.equal(sender.broadcasts.length, 1);
    const sent = sender.broadcasts[0]!;
    assert.equal(sent.type, PacketType.SpaceBreadcrumb);
    assert.equal(sent.sourceUhid, "aether:alice:01");
    assert.equal(sent.destinationUhid, "*");

    let got: SpaceBreadcrumb | undefined;
    svc.onBreadcrumbReceived = (e) => { got = e; };
    const ok = await svc.handle(sent);
    assert.equal(ok, true);
    assert.ok(got);
    assert.equal(got!.geoHash, "u4pruy");
    assert.equal(got!.type, BreadcrumbType.Emergency);
    assert.equal(got!.ttlHours, 720);
    assert.equal(got!.signature.length, 64);
  });

  it("returns the delivered peer count", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    sender.addPeer({ uhid: "aether:peer:aa" } as never);
    sender.addPeer({ uhid: "aether:peer:bb" } as never);
    const svc = new SpaceBreadcrumbService(sender);
    const crumb: SpaceBreadcrumb = {
      contentHash: "QmX",
      geoHash: "u4pruy",
      anchorUhid: "aether:alice:01",
      createdAtUtc: new Date(1_700_000_000_000),
      ttlHours: 24,
      type: BreadcrumbType.Notice,
      signature: new Uint8Array(0),
    };
    assert.equal(await svc.broadcast(crumb), 2);
  });

  // round-trip: a broadcast payload deserializes back to equal field values.
  it("round-trips created_at_ms + type + signature through the wire", async () => {
    const crumb: SpaceBreadcrumb = {
      contentHash: "QmRoundTrip",
      geoHash: "gcpvj0",
      anchorUhid: "aether:carol:03",
      createdAtUtc: new Date(1_700_000_000_000),
      ttlHours: 48,
      type: BreadcrumbType.Event,
      signature: new Uint8Array([1, 2, 3, 4]),
    };
    const payload = breadcrumbToPayload(crumb);
    assert.equal(payload.createdAtMs, 1_700_000_000_000);
    assert.equal(payload.type, BreadcrumbType.Event); // 3
    assert.equal(payload.signatureBase64, Buffer.from([1, 2, 3, 4]).toString("base64"));
  });

  // Mirrors Space_Handle_WrongType_ReturnsFalse.
  it("rejects the wrong packet type", async () => {
    const svc = new SpaceBreadcrumbService(new FakeMeshSender("aether:local:01"));
    const pkt = new MeshPacket();
    pkt.type = PacketType.Data;
    pkt.payload = new Uint8Array(0);
    assert.equal(await svc.handle(pkt), false);
  });

  it("drops a malformed payload", async () => {
    const svc = new SpaceBreadcrumbService(new FakeMeshSender("aether:local:01"));
    const pkt = new MeshPacket();
    pkt.type = PacketType.SpaceBreadcrumb;
    pkt.sourceUhid = "aether:bob:02";
    pkt.payload = new TextEncoder().encode("{not valid json");
    assert.equal(await svc.handle(pkt), false);
  });
});
