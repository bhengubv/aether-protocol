/**
 * Unit tests for the ChannelMessage service (PacketType.ChannelMessage = 7). Uses a fake
 * IMeshSender — no transport needed. Mirrors the C# ChannelMessageTests, plus the canonical
 * byte-identity gate from fixtures/channels/vectors.json.
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/channels.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import {
  ChannelMessageService,
  serializeChannelMessagePayload,
} from "../src/channels/index.js";
import type { ChannelMessageReceived } from "../src/channels/index.js";
import { FakeMeshSender } from "./fakes.js";

const LOCAL = "aether:local:01";

function build(sender: FakeMeshSender): ChannelMessageService {
  return new ChannelMessageService(sender);
}

/** Build a real ChannelMessage packet from a peer with the canonical payload. */
function channelPacket(
  channelId: string,
  messageId: string,
  senderUhid: string,
  content: string,
  sentAtMs: number,
  ttl = 7,
): MeshPacket {
  const pkt = new MeshPacket();
  pkt.type = PacketType.ChannelMessage;
  pkt.sourceUhid = senderUhid;
  pkt.destinationUhid = "*";
  pkt.ttl = ttl;
  pkt.payload = new TextEncoder().encode(
    serializeChannelMessagePayload({ channelId, messageId, senderUhid, content, sentAtMs }),
  );
  return pkt;
}

// ── canonical byte-identity gate ──────────────────────────────────────────────

describe("ChannelMessagePayload — canonical byte-identity", () => {
  // Mirrors [InlineData] in ChannelMessageTests.ChannelMessagePayload_SerializesToCanonicalBytes.
  it("serializes vector 1 (basic) to canonical bytes", () => {
    assert.equal(
      serializeChannelMessagePayload({
        channelId: "res-floor-3",
        messageId: "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f",
        senderUhid: "aether:alice:01",
        content: "meeting at 6",
        sentAtMs: 1_700_000_000_000,
      }),
      '{"channel_id":"res-floor-3","message_id":"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f","sender_uhid":"aether:alice:01","content":"meeting at 6","sent_at_ms":1700000000000}',
    );
  });

  it("serializes vector 2 (minimal) to canonical bytes", () => {
    assert.equal(
      serializeChannelMessagePayload({
        channelId: "g",
        messageId: "00000000-0000-0000-0000-000000000000",
        senderUhid: "n",
        content: "",
        sentAtMs: 0,
      }),
      '{"channel_id":"g","message_id":"00000000-0000-0000-0000-000000000000","sender_uhid":"n","content":"","sent_at_ms":0}',
    );
  });

  // Cross-language parity: reproduce every vector in fixtures/channels/vectors.json.
  it("reproduces every fixture vector byte-for-byte", () => {
    const vectorsPath = fileURLToPath(
      new URL("../../fixtures/channels/vectors.json", import.meta.url),
    );
    const V = JSON.parse(readFileSync(vectorsPath, "utf8")) as {
      vectors: {
        name: string;
        channel_id: string;
        message_id: string;
        sender_uhid: string;
        content: string;
        sent_at_ms: number;
        expected_json: string;
      }[];
    };
    assert.ok(V.vectors.length >= 2, "fixture must carry at least the two reference vectors");
    for (const vec of V.vectors) {
      assert.equal(
        serializeChannelMessagePayload({
          channelId: vec.channel_id,
          messageId: vec.message_id,
          senderUhid: vec.sender_uhid,
          content: vec.content,
          sentAtMs: vec.sent_at_ms,
        }),
        vec.expected_json,
        `canonical bytes for vector "${vec.name}"`,
      );
    }
  });
});

// ── publish ─────────────────────────────────────────────────────────────────

describe("ChannelMessageService — publish", () => {
  it("broadcasts a ChannelMessage carrying channel, content, and sender", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    const svc = build(sender);

    await svc.publish("res-floor-3", "meeting at 6");

    assert.equal(sender.broadcasts.length, 1);
    const pkt = sender.broadcasts[0]!;
    assert.equal(pkt.type, PacketType.ChannelMessage);
    assert.equal(pkt.sourceUhid, "aether:alice:01");
    assert.equal(pkt.destinationUhid, "*");
    assert.equal(pkt.ttl, 7);

    const body = JSON.parse(new TextDecoder().decode(pkt.payload));
    assert.equal(body.channel_id, "res-floor-3");
    assert.equal(body.content, "meeting at 6");
    assert.equal(body.sender_uhid, "aether:alice:01");
  });

  it("returns the delivered peer count", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    sender.addPeer({ uhid: "aether:peer:aa" } as any);
    sender.addPeer({ uhid: "aether:peer:bb" } as any);
    const delivered = await build(sender).publish("res-floor-3", "hi");
    assert.equal(delivered, 2);
  });
});

// ── subscriptions ─────────────────────────────────────────────────────────────

describe("ChannelMessageService — subscriptions", () => {
  it("tracks subscribe / unsubscribe / getSubscriptions", () => {
    const svc = build(new FakeMeshSender(LOCAL));
    assert.deepEqual(svc.getSubscriptions(), []);

    svc.subscribe("a");
    svc.subscribe("b");
    svc.subscribe("a"); // idempotent
    assert.deepEqual(svc.getSubscriptions().sort(), ["a", "b"]);

    svc.unsubscribe("a");
    assert.deepEqual(svc.getSubscriptions(), ["b"]);
  });
});

// ── handle ────────────────────────────────────────────────────────────────────

describe("ChannelMessageService — handle", () => {
  it("subscribed channel raises onMessageReceived", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    svc.subscribe("res-floor-3");

    let got: ChannelMessageReceived | undefined;
    svc.onMessageReceived = (e) => { got = e; };

    const ok = await svc.handle(
      channelPacket("res-floor-3", crypto.randomUUID(), "aether:bob:02", "hello floor", 1_700_000_000_000),
    );

    assert.equal(ok, true);
    assert.ok(got);
    assert.equal(got!.channelId, "res-floor-3");
    assert.equal(got!.content, "hello floor");
    assert.equal(got!.senderUhid, "aether:bob:02");
  });

  it("unsubscribed channel is processed + relayed but not surfaced", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    let raised = false;
    svc.onMessageReceived = () => { raised = true; };

    const ok = await svc.handle(
      channelPacket("society-x", crypto.randomUUID(), "aether:bob:02", "hi", 1),
    );

    assert.equal(ok, true);   // processed + relayed
    assert.equal(raised, false); // but not surfaced — we aren't subscribed
  });

  it("duplicate message id returns false and fires the event once", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    svc.subscribe("res-floor-3");
    const id = crypto.randomUUID();

    let events = 0;
    svc.onMessageReceived = () => { events++; };

    assert.equal(await svc.handle(channelPacket("res-floor-3", id, "aether:bob:02", "one", 1)), true);
    assert.equal(await svc.handle(channelPacket("res-floor-3", id, "aether:bob:02", "one", 1)), false);
    assert.equal(events, 1);
  });

  it("does not surface our own message flooded back", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    svc.subscribe("res-floor-3");
    let raised = false;
    svc.onMessageReceived = () => { raised = true; };

    // A message whose author is us — even on a subscribed channel — must not surface.
    const ok = await svc.handle(
      channelPacket("res-floor-3", crypto.randomUUID(), LOCAL, "mine", 1),
    );
    assert.equal(ok, true); // still de-duped/processed
    assert.equal(raised, false);
  });

  it("rejects the wrong packet type", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    const pkt = channelPacket("res-floor-3", crypto.randomUUID(), "aether:bob:02", "x", 1);
    pkt.type = PacketType.Data;
    assert.equal(await svc.handle(pkt), false);
  });

  it("drops a malformed payload", async () => {
    const svc = build(new FakeMeshSender(LOCAL));
    const pkt = new MeshPacket();
    pkt.type = PacketType.ChannelMessage;
    pkt.sourceUhid = "aether:bob:02";
    pkt.payload = new TextEncoder().encode("{not valid json");
    assert.equal(await svc.handle(pkt), false);
  });
});

// ── re-flood ────────────────────────────────────────────────────────────────

describe("ChannelMessageService — re-flood", () => {
  it("relays with decremented TTL when TTL allows (pure relay, not subscribed)", async () => {
    const relaySender = new FakeMeshSender("aether:relay:09");
    const svc = build(relaySender); // not subscribed — pure relay

    await svc.handle(
      channelPacket("res-floor-3", crypto.randomUUID(), "aether:bob:02", "hop", 1, 5),
    );

    assert.equal(relaySender.broadcasts.length, 1);
    const relayed = relaySender.broadcasts[0]!;
    assert.equal(relayed.type, PacketType.ChannelMessage);
    assert.equal(relayed.ttl, 4);
  });

  it("does not relay when TTL is 1", async () => {
    const relaySender = new FakeMeshSender("aether:relay:09");
    const svc = build(relaySender);

    await svc.handle(
      channelPacket("res-floor-3", crypto.randomUUID(), "aether:bob:02", "last hop", 1, 1),
    );

    assert.equal(relaySender.broadcasts.length, 0);
  });
});
