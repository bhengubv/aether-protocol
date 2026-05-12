/**
 * Unit tests for StreamingService.
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/streaming.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import { StreamingService } from "../src/streaming/StreamingService.js";
import { FakeMeshSender } from "./fakes.js";

// ── Helpers ───────────────────────────────────────────────────────────────────

function makeSvc(uhid = "alice") {
  const sender = new FakeMeshSender(uhid);
  const svc = new StreamingService(sender);
  return { sender, svc };
}

function jsonPacket(from: string, type: PacketType, body: unknown): MeshPacket {
  const p = new MeshPacket();
  p.type = type;
  p.sourceUhid = from;
  p.payload = new TextEncoder().encode(JSON.stringify(body));
  return p;
}

// ── startStream ───────────────────────────────────────────────────────────────

describe("StreamingService — startStream", () => {
  it("broadcasts StreamAnnounce with state=live", async () => {
    const { sender, svc } = makeSvc();

    const streamId = await svc.startStream("My Stream", "video/h264", "h264", 2000);
    assert.ok(streamId.length > 0);
    assert.equal(sender.broadcasts.length, 1);
    assert.equal(sender.broadcasts[0].type, PacketType.StreamAnnounce);

    const body = JSON.parse(new TextDecoder().decode(sender.broadcasts[0].payload));
    assert.equal(body.state, "live");
    assert.equal(body.title, "My Stream");
    assert.equal(body.stream_id, streamId);
  });
});

// ── endStream ─────────────────────────────────────────────────────────────────

describe("StreamingService — endStream", () => {
  it("broadcasts ending then ended announces", async () => {
    const { sender, svc } = makeSvc();

    const streamId = await svc.startStream("Test", "video/h264", "h264", 1000);
    sender.clear();

    await svc.endStream(streamId);

    assert.ok(sender.broadcasts.length >= 2, "expected ≥2 broadcasts");
    const states = sender.broadcasts.map((p) => {
      const b = JSON.parse(new TextDecoder().decode(p.payload));
      return b.state;
    });
    assert.ok(states.includes("ending") || states.includes("ended"));
    assert.equal(states[states.length - 1], "ended");
  });

  it("fires onStreamEnded callback", async () => {
    const { svc } = makeSvc();
    const streamId = await svc.startStream("T", "video/h264", "h264", 1000);

    let endedId = "";
    svc.onStreamEnded = (id) => { endedId = id; };
    await svc.endStream(streamId);

    assert.equal(endedId, streamId);
  });
});

// ── subscribe / unsubscribe ───────────────────────────────────────────────────

describe("StreamingService — subscribe / unsubscribe", () => {
  it("subscribe sends StreamSubscribe packet to publisher", async () => {
    const { sender, svc } = makeSvc("alice");
    const fakeStreamId = crypto.randomUUID();

    await svc.subscribe(fakeStreamId, "bob", false);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.equal(toBob.length, 1);
    assert.equal(toBob[0].packet.type, PacketType.StreamSubscribe);
  });

  it("unsubscribe sends StreamUnsubscribe packet to publisher", async () => {
    const { sender, svc } = makeSvc("alice");
    const fakeStreamId = crypto.randomUUID();

    await svc.subscribe(fakeStreamId, "bob", false);
    sender.clear();

    await svc.unsubscribe(fakeStreamId, "bob");

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.equal(toBob.length, 1);
    assert.equal(toBob[0].packet.type, PacketType.StreamUnsubscribe);
  });
});

// ── handlePacket — subscribe flow ─────────────────────────────────────────────

describe("StreamingService — onPacket subscribe flow", () => {
  it("inbound subscribe adds subscriber and fires onSubscriberJoined", async () => {
    const { svc } = makeSvc("alice");
    const streamId = await svc.startStream("T", "video/h264", "h264", 1000);

    let joinedUhid = "";
    svc.onSubscriberJoined = (sid, uhid) => { joinedUhid = uhid; };

    const pkt = jsonPacket("bob", PacketType.StreamSubscribe, {
      stream_id: streamId,
      live_only: false,
    });
    await svc.onPacket(pkt);

    assert.equal(joinedUhid, "bob");
    assert.ok(svc.getSubscribers(streamId).includes("bob"));
  });

  it("inbound unsubscribe removes subscriber and fires onSubscriberLeft", async () => {
    const { svc } = makeSvc("alice");
    const streamId = await svc.startStream("T", "video/h264", "h264", 1000);

    // Subscribe first.
    await svc.onPacket(jsonPacket("bob", PacketType.StreamSubscribe, {
      stream_id: streamId, live_only: false,
    }));

    let leftUhid = "";
    svc.onSubscriberLeft = (_, uhid) => { leftUhid = uhid; };

    await svc.onPacket(jsonPacket("bob", PacketType.StreamUnsubscribe, {
      stream_id: streamId,
    }));

    assert.equal(leftUhid, "bob");
    assert.ok(!svc.getSubscribers(streamId).includes("bob"));
  });
});

// ── publishSegment ────────────────────────────────────────────────────────────

describe("StreamingService — publishSegment", () => {
  it("unicasts StreamSegment to subscriber after subscribe", async () => {
    const { sender, svc } = makeSvc("alice");
    const streamId = await svc.startStream("T", "video/h264", "h264", 1000);

    await svc.onPacket(jsonPacket("bob", PacketType.StreamSubscribe, {
      stream_id: streamId, live_only: false,
    }));
    sender.clear();

    await svc.publishSegment(streamId, new Uint8Array([1, 2, 3, 4]), true);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.ok(toBob.length > 0, "expected StreamSegment unicast to bob");
    assert.equal(toBob[0].packet.type, PacketType.StreamSegment);
  });

  it("fans out to multiple subscribers", async () => {
    const { sender, svc } = makeSvc("alice");
    const streamId = await svc.startStream("T", "video/h264", "h264", 1000);

    await svc.onPacket(jsonPacket("bob", PacketType.StreamSubscribe, {
      stream_id: streamId, live_only: false,
    }));
    await svc.onPacket(jsonPacket("carol", PacketType.StreamSubscribe, {
      stream_id: streamId, live_only: false,
    }));
    sender.clear();

    await svc.publishSegment(streamId, new Uint8Array([1, 2, 3]), false);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    const toCarol = sender.unicasts.filter((u) => u.nextHopUhid === "carol");
    assert.ok(toBob.length > 0, "bob should receive segment");
    assert.ok(toCarol.length > 0, "carol should receive segment");
  });

  it("unsubscribed peer receives no segments", async () => {
    const { sender, svc } = makeSvc("alice");
    const streamId = await svc.startStream("T", "video/h264", "h264", 1000);

    await svc.onPacket(jsonPacket("bob", PacketType.StreamSubscribe, {
      stream_id: streamId, live_only: false,
    }));
    await svc.onPacket(jsonPacket("bob", PacketType.StreamUnsubscribe, {
      stream_id: streamId,
    }));
    sender.clear();

    await svc.publishSegment(streamId, new Uint8Array([1, 2, 3]), false);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.equal(toBob.length, 0, "unsubscribed bob must not receive segments");
  });
});

// ── handlePacket — announce flow ──────────────────────────────────────────────

describe("StreamingService — onPacket announce flow", () => {
  it("live announce fires onStreamAnnounced and stores in knownStreams", async () => {
    const { svc } = makeSvc("alice");
    const remoteStreamId = crypto.randomUUID();

    let announced: unknown = null;
    svc.onStreamAnnounced = (info) => { announced = info; };

    await svc.onPacket(jsonPacket("bob", PacketType.StreamAnnounce, {
      stream_id: remoteStreamId,
      title: "Bob's Stream",
      content_type: "video/h264",
      codec: "h264",
      segment_duration_ms: 1000,
      state: "live",
      started_at_ms: Date.now(),
    }));

    assert.ok(announced !== null, "onStreamAnnounced was not fired");
    assert.ok(svc.getKnownStreams().some((s) => (s as { streamId: string }).streamId === remoteStreamId));
  });

  it("ended announce fires onStreamEnded and removes from knownStreams", async () => {
    const { svc } = makeSvc("alice");
    const remoteStreamId = crypto.randomUUID();

    // First announce as live.
    await svc.onPacket(jsonPacket("bob", PacketType.StreamAnnounce, {
      stream_id: remoteStreamId,
      title: "T",
      content_type: "video/h264",
      codec: "h264",
      segment_duration_ms: 1000,
      state: "live",
      started_at_ms: Date.now(),
    }));

    let endedId = "";
    svc.onStreamEnded = (id) => { endedId = id; };

    await svc.onPacket(jsonPacket("bob", PacketType.StreamAnnounce, {
      stream_id: remoteStreamId,
      title: "T",
      content_type: "video/h264",
      codec: "h264",
      segment_duration_ms: 1000,
      state: "ended",
      started_at_ms: Date.now(),
    }));

    assert.equal(endedId, remoteStreamId);
    assert.ok(!svc.getKnownStreams().some((s) => (s as { streamId: string }).streamId === remoteStreamId));
  });
});
