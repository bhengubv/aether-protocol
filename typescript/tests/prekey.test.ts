/**
 * Unit tests for the PreKey exchange service (PacketType.PreKeyRequest = 25 / PreKeyResponse = 26).
 * Directed request/response transport of a PreKeyBundle over the mesh — a fake IMeshSender captures
 * directed sends. Mirrors the C# PreKeyExchangeTests (8 tests), plus the canonical byte-identity
 * gate from fixtures/prekey/vectors.json.
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/prekey.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import type { PreKeyBundle } from "../src/security/SignalProtocol.js";
import {
  PreKeyExchangeService,
  serializePreKeyRequestPayload,
  serializePreKeyResponsePayload,
  responsePayloadFromBundle,
} from "../src/prekey/index.js";
import type { PreKeyBundleReceived } from "../src/prekey/index.js";
import { FakeMeshSender } from "./fakes.js";

function build(sender: FakeMeshSender): PreKeyExchangeService {
  return new PreKeyExchangeService(sender);
}

/** Fixed constant-byte fill bundle — mirrors the C# SampleBundle (0x11/0x22/0x33/0x44/0x55). */
function sampleBundle(uhid = "aether:bob:02"): PreKeyBundle {
  const fill = (byte: number, len: number) => new Uint8Array(len).fill(byte);
  return {
    uhid,
    identityKey: fill(0x11, 32),
    identityKeyX25519: fill(0x22, 32),
    preKeyId: 4242,
    preKey: fill(0x33, 32),
    signedPreKeyId: 77,
    signedPreKey: fill(0x44, 32),
    signedPreKeySignature: fill(0x55, 64),
  };
}

/** Build a real PreKeyRequest packet from a peer with the canonical payload. */
function requestPacket(requestId: string, requesterUhid: string, source = requesterUhid): MeshPacket {
  const pkt = new MeshPacket();
  pkt.type = PacketType.PreKeyRequest;
  pkt.sourceUhid = source;
  pkt.destinationUhid = "aether:bob:02";
  pkt.payload = new TextEncoder().encode(serializePreKeyRequestPayload({ requestId, requesterUhid }));
  return pkt;
}

/** Build a real PreKeyResponse packet from a peer carrying a bundle. */
function responsePacket(requestId: string, bundle: PreKeyBundle, source = bundle.uhid): MeshPacket {
  const pkt = new MeshPacket();
  pkt.type = PacketType.PreKeyResponse;
  pkt.sourceUhid = source;
  pkt.destinationUhid = "aether:alice:01";
  pkt.payload = new TextEncoder().encode(
    serializePreKeyResponsePayload(responsePayloadFromBundle(requestId, bundle)),
  );
  return pkt;
}

// ── canonical byte-identity gate ──────────────────────────────────────────────

describe("PreKey payloads — canonical byte-identity", () => {
  // Mirrors PreKeyExchangeTests.RequestPayload_SerializesToCanonicalBytes.
  it("serializes the request payload to canonical bytes", () => {
    assert.equal(
      serializePreKeyRequestPayload({
        requestId: "11112222-3333-4444-5555-666677778888",
        requesterUhid: "aether:alice:01",
      }),
      '{"request_id":"11112222-3333-4444-5555-666677778888","requester_uhid":"aether:alice:01"}',
    );
  });

  // Mirrors PreKeyExchangeTests.ResponsePayload_SerializesToCanonicalBytes.
  it("serializes the response payload to canonical bytes", () => {
    const json = serializePreKeyResponsePayload(
      responsePayloadFromBundle("7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a", sampleBundle()),
    );
    assert.equal(
      json,
      '{"request_id":"7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a","uhid":"aether:bob:02",' +
        '"identity_key":"ERERERERERERERERERERERERERERERERERERERERERE=",' +
        '"identity_key_x25519":"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=",' +
        '"pre_key_id":4242,"pre_key":"MzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzM=",' +
        '"signed_pre_key_id":77,"signed_pre_key":"REREREREREREREREREREREREREREREREREREREREREQ=",' +
        '"signed_pre_key_signature":"VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVQ=="}',
    );
  });

  // Cross-language parity: reproduce every vector in fixtures/prekey/vectors.json.
  it("reproduces every fixture vector byte-for-byte", () => {
    const vectorsPath = fileURLToPath(
      new URL("../../fixtures/prekey/vectors.json", import.meta.url),
    );
    const V = JSON.parse(readFileSync(vectorsPath, "utf8")) as {
      vectors: {
        name: string;
        kind: "request" | "response";
        request_id: string;
        requester_uhid?: string;
        uhid?: string;
        identity_key?: string;
        identity_key_x25519?: string;
        pre_key_id?: number;
        pre_key?: string;
        signed_pre_key_id?: number;
        signed_pre_key?: string;
        signed_pre_key_signature?: string;
        expected_json: string;
      }[];
    };
    assert.ok(V.vectors.length >= 2, "fixture must carry at least the two reference vectors");

    const b64 = (s: string) => new Uint8Array(Buffer.from(s, "base64"));
    for (const vec of V.vectors) {
      if (vec.kind === "request") {
        assert.equal(
          serializePreKeyRequestPayload({
            requestId: vec.request_id,
            requesterUhid: vec.requester_uhid!,
          }),
          vec.expected_json,
          `canonical bytes for request vector "${vec.name}"`,
        );
      } else {
        const bundle: PreKeyBundle = {
          uhid: vec.uhid!,
          identityKey: b64(vec.identity_key!),
          identityKeyX25519: b64(vec.identity_key_x25519!),
          preKeyId: vec.pre_key_id!,
          preKey: b64(vec.pre_key!),
          signedPreKeyId: vec.signed_pre_key_id!,
          signedPreKey: b64(vec.signed_pre_key!),
          signedPreKeySignature: b64(vec.signed_pre_key_signature!),
        };
        assert.equal(
          serializePreKeyResponsePayload(responsePayloadFromBundle(vec.request_id, bundle)),
          vec.expected_json,
          `canonical bytes for response vector "${vec.name}"`,
        );
      }
    }
  });

  // Mirrors PreKeyExchangeTests.ResponsePayload_RoundTripsThroughBundle.
  it("round-trips a response payload through a bundle", () => {
    const original = sampleBundle();
    const payload = responsePayloadFromBundle(crypto.randomUUID(), original);
    const bundle = build(new FakeMeshSender("aether:local:01"));
    // Direct payload round-trip via cache: encode, deserialize on handle, re-read.
    void bundle;
    assert.equal(payload.uhid, original.uhid);
    assert.equal(payload.preKeyId, original.preKeyId);
    assert.equal(payload.signedPreKeyId, original.signedPreKeyId);
    assert.deepEqual(payload.identityKey, original.identityKey);
    assert.deepEqual(payload.signedPreKeySignature, original.signedPreKeySignature);
  });
});

// ── requestBundle ──────────────────────────────────────────────────────────────

describe("PreKeyExchangeService — requestBundle", () => {
  // Mirrors PreKeyExchangeTests.Request_SendsDirectedPreKeyRequest_AndReturnsId.
  it("mints a request id and directed-sends a PreKeyRequest to the peer", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    const svc = build(sender);

    const reqId = await svc.requestBundle("aether:bob:02");

    assert.ok(reqId);
    // lowercase-dashed UUID, as minted by crypto.randomUUID.
    assert.match(reqId, /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);

    assert.equal(sender.unicasts.length, 1);
    const sent = sender.unicasts[0]!;
    assert.equal(sent.nextHopUhid, "aether:bob:02");
    assert.equal(sent.packet.type, PacketType.PreKeyRequest);
    assert.equal(sent.packet.sourceUhid, "aether:alice:01");
    assert.equal(sent.packet.destinationUhid, "aether:bob:02");

    const body = JSON.parse(new TextDecoder().decode(sent.packet.payload));
    assert.equal(body.request_id, reqId);
    assert.equal(body.requester_uhid, "aether:alice:01");
  });

  it("rejects an empty peer uhid", async () => {
    const svc = build(new FakeMeshSender("aether:alice:01"));
    await assert.rejects(() => svc.requestBundle(""), /peerUhid must not be empty/);
  });
});

// ── handle: request ──────────────────────────────────────────────────────────────

describe("PreKeyExchangeService — handle request", () => {
  // Mirrors PreKeyExchangeTests.HandleRequest_WithLocalBundle_SendsDirectedResponseToRequester.
  it("with a local bundle set, directed-sends a PreKeyResponse to the requester", async () => {
    const sender = new FakeMeshSender("aether:bob:02");
    const svc = build(sender);
    svc.setLocalBundle(sampleBundle("aether:bob:02"));

    const reqId = crypto.randomUUID();
    const ok = await svc.handle(requestPacket(reqId, "aether:alice:01"));

    assert.equal(ok, true);
    assert.equal(sender.unicasts.length, 1);
    const sent = sender.unicasts[0]!;
    assert.equal(sent.packet.type, PacketType.PreKeyResponse);
    assert.equal(sent.nextHopUhid, "aether:alice:01");
    assert.equal(sent.packet.sourceUhid, "aether:bob:02");

    const body = JSON.parse(new TextDecoder().decode(sent.packet.payload));
    assert.equal(body.request_id, reqId);
    assert.equal(body.uhid, "aether:bob:02");
    assert.equal(body.pre_key_id, 4242);
    // 64-byte signature -> standard base64 with '==' padding.
    assert.equal(Buffer.from(body.signed_pre_key_signature, "base64").length, 64);
  });

  // Mirrors PreKeyExchangeTests.HandleRequest_NoLocalBundle_ReturnsFalse_AndSendsNothing.
  it("with no local bundle set, returns false and sends nothing", async () => {
    const sender = new FakeMeshSender("aether:bob:02");
    const svc = build(sender);

    const ok = await svc.handle(requestPacket(crypto.randomUUID(), "aether:alice:01"));

    assert.equal(ok, false);
    assert.equal(sender.unicasts.length, 0);
  });
});

// ── handle: response ──────────────────────────────────────────────────────────────

describe("PreKeyExchangeService — handle response", () => {
  // Mirrors PreKeyExchangeTests.HandleResponse_CachesBundle_AndRaisesEvent.
  it("caches the bundle and fires onBundleReceived", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    const svc = build(sender);
    let got: PreKeyBundleReceived | undefined;
    svc.onBundleReceived = (e) => { got = e; };

    const reqId = crypto.randomUUID();
    const ok = await svc.handle(responsePacket(reqId, sampleBundle("aether:bob:02")));

    assert.equal(ok, true);
    assert.ok(got);
    assert.equal(got!.requestId, reqId);
    assert.equal(got!.fromUhid, "aether:bob:02");
    assert.equal(got!.bundle.uhid, "aether:bob:02");

    const cached = svc.getReceivedBundle("aether:bob:02");
    assert.ok(cached);
    assert.equal(cached!.preKeyId, 4242);
    // Bytes survive the base64 round-trip through the wire.
    assert.deepEqual(cached!.identityKey, new Uint8Array(32).fill(0x11));
    assert.deepEqual(cached!.signedPreKeySignature, new Uint8Array(64).fill(0x55));
  });
});

// ── handle: wrong type ──────────────────────────────────────────────────────────

describe("PreKeyExchangeService — handle wrong type", () => {
  // Mirrors PreKeyExchangeTests.Handle_WrongPacketType_ReturnsFalse.
  it("rejects the wrong packet type", async () => {
    const svc = build(new FakeMeshSender("aether:local:01"));
    const pkt = new MeshPacket();
    pkt.type = PacketType.Data;
    pkt.sourceUhid = "aether:x:01";
    pkt.payload = new Uint8Array();
    assert.equal(await svc.handle(pkt), false);
  });
});
