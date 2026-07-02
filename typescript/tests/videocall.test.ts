/**
 * Unit tests for the VideoCall call-control service (PacketType.VideoCall = 27). Directed
 * signalling — a fake IMeshSender captures directed sends. Mirrors the C#
 * VideoCallControlTests, plus the canonical byte-identity gate from
 * fixtures/videocall/vectors.json.
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/videocall.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import {
  VideoCallControlService,
  serializeVideoCallControlPayload,
} from "../src/videocall/index.js";
import type { VideoCallStateChanged } from "../src/videocall/index.js";
import { FakeMeshSender } from "./fakes.js";

const LOCAL = "aether:local:01";

function build(sender: FakeMeshSender): VideoCallControlService {
  return new VideoCallControlService(sender);
}

/** Build a real VideoCall control packet from a peer with the canonical payload. */
function controlPacket(
  callId: string,
  action: string,
  fromUhid: string,
  sentAtMs = 1,
): MeshPacket {
  const pkt = new MeshPacket();
  pkt.type = PacketType.VideoCall;
  pkt.sourceUhid = fromUhid;
  pkt.destinationUhid = LOCAL;
  pkt.payload = new TextEncoder().encode(
    serializeVideoCallControlPayload({ callId, action, sentAtMs }),
  );
  return pkt;
}

// ── canonical byte-identity gate ──────────────────────────────────────────────

describe("VideoCallControlPayload — canonical byte-identity", () => {
  // Mirrors [InlineData] in VideoCallControlTests.VideoCallControlPayload_SerializesToCanonicalBytes.
  it("serializes vector 1 (ring) to canonical bytes", () => {
    assert.equal(
      serializeVideoCallControlPayload({
        callId: "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f",
        action: "ring",
        sentAtMs: 1_700_000_000_000,
      }),
      '{"call_id":"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f","action":"ring","sent_at_ms":1700000000000}',
    );
  });

  it("serializes vector 2 (hangup) to canonical bytes", () => {
    assert.equal(
      serializeVideoCallControlPayload({
        callId: "00000000-0000-0000-0000-000000000000",
        action: "hangup",
        sentAtMs: 0,
      }),
      '{"call_id":"00000000-0000-0000-0000-000000000000","action":"hangup","sent_at_ms":0}',
    );
  });

  // Cross-language parity: reproduce every vector in fixtures/videocall/vectors.json.
  it("reproduces every fixture vector byte-for-byte", () => {
    const vectorsPath = fileURLToPath(
      new URL("../../fixtures/videocall/vectors.json", import.meta.url),
    );
    const V = JSON.parse(readFileSync(vectorsPath, "utf8")) as {
      vectors: {
        name: string;
        call_id: string;
        action: string;
        sent_at_ms: number;
        expected_json: string;
      }[];
    };
    assert.ok(V.vectors.length >= 2, "fixture must carry at least the two reference vectors");
    for (const vec of V.vectors) {
      assert.equal(
        serializeVideoCallControlPayload({
          callId: vec.call_id,
          action: vec.action,
          sentAtMs: vec.sent_at_ms,
        }),
        vec.expected_json,
        `canonical bytes for vector "${vec.name}"`,
      );
    }
  });
});

// ── ring ──────────────────────────────────────────────────────────────────────

describe("VideoCallControlService — ring", () => {
  it("mints a call id and directed-sends a ring to the peer", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    const svc = build(sender);

    const callId = await svc.ring("aether:bob:02");

    assert.ok(callId);
    // lowercase-dashed UUID, as minted by crypto.randomUUID.
    assert.match(callId, /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/);

    assert.equal(sender.unicasts.length, 1);
    const sent = sender.unicasts[0]!;
    assert.equal(sent.nextHopUhid, "aether:bob:02");
    assert.equal(sent.packet.type, PacketType.VideoCall);
    assert.equal(sent.packet.sourceUhid, "aether:alice:01");
    assert.equal(sent.packet.destinationUhid, "aether:bob:02");

    const body = JSON.parse(new TextDecoder().decode(sent.packet.payload));
    assert.equal(body.action, "ring");
    assert.equal(body.call_id, callId);
  });

  it("rejects an empty peer uhid", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    await assert.rejects(() => svc.ring(""), /peerUhid must not be empty/);
  });
});

// ── accept / decline / hangup ─────────────────────────────────────────────────

describe("VideoCallControlService — respond", () => {
  for (const action of ["accept", "decline", "hangup"] as const) {
    it(`directed-sends a ${action} for the call id to the peer`, async () => {
      const sender = new FakeMeshSender("aether:alice:01");
      const svc = build(sender);
      const callId = crypto.randomUUID();

      const ok =
        action === "accept"
          ? await svc.accept(callId, "aether:bob:02")
          : action === "decline"
            ? await svc.decline(callId, "aether:bob:02")
            : await svc.hangup(callId, "aether:bob:02");

      assert.equal(ok, true);
      assert.equal(sender.unicasts.length, 1);
      const sent = sender.unicasts[0]!;
      assert.equal(sent.nextHopUhid, "aether:bob:02");
      assert.equal(sent.packet.type, PacketType.VideoCall);

      const body = JSON.parse(new TextDecoder().decode(sent.packet.payload));
      assert.equal(body.action, action);
      assert.equal(body.call_id, callId);
    });
  }

  it("returns false when the directed send fails", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    sender.failSendsTo("aether:bob:02");
    const ok = await build(sender).hangup(crypto.randomUUID(), "aether:bob:02");
    assert.equal(ok, false);
  });
});

// ── handle ────────────────────────────────────────────────────────────────────

describe("VideoCallControlService — handle", () => {
  it("raises onCallStateChanged with the packet source as fromUhid", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    let got: VideoCallStateChanged | undefined;
    svc.onCallStateChanged = (e) => { got = e; };

    const callId = crypto.randomUUID();
    const ok = await svc.handle(controlPacket(callId, "ring", "aether:bob:02"));

    assert.equal(ok, true);
    assert.ok(got);
    assert.equal(got!.callId, callId);
    assert.equal(got!.action, "ring");
    assert.equal(got!.fromUhid, "aether:bob:02");
  });

  it("rejects the wrong packet type", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    const pkt = controlPacket(crypto.randomUUID(), "ring", "aether:bob:02");
    pkt.type = PacketType.Data;
    assert.equal(await svc.handle(pkt), false);
  });

  it("drops a malformed payload", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    const pkt = new MeshPacket();
    pkt.type = PacketType.VideoCall;
    pkt.sourceUhid = "aether:bob:02";
    pkt.payload = new TextEncoder().encode("{not valid json");
    assert.equal(await svc.handle(pkt), false);
  });

  it("drops a payload with no action", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    let raised = false;
    svc.onCallStateChanged = () => { raised = true; };

    const pkt = new MeshPacket();
    pkt.type = PacketType.VideoCall;
    pkt.sourceUhid = "aether:bob:02";
    pkt.payload = new TextEncoder().encode(
      JSON.stringify({ call_id: crypto.randomUUID(), sent_at_ms: 1 }),
    );
    assert.equal(await svc.handle(pkt), false);
    assert.equal(raised, false);
  });
});
