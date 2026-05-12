/**
 * Unit tests for VideoCallService and WatchTogetherService.
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/video_watch.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import {
  VideoCallService,
  encodeVideoFrame,
} from "../src/streaming/VideoCallService.js";
import { WatchTogetherService } from "../src/streaming/WatchTogetherService.js";
import { FakeMeshSender } from "./fakes.js";

// ── Helpers ───────────────────────────────────────────────────────────────────

function makeVideoSvc(uhid = "alice") {
  const sender = new FakeMeshSender(uhid);
  const svc = new VideoCallService(sender);
  return { sender, svc };
}

function makeWatchSvc(uhid = "alice") {
  const sender = new FakeMeshSender(uhid);
  const svc = new WatchTogetherService(sender);
  return { sender, svc };
}

function videoSignalingPacket(from: string, body: unknown): MeshPacket {
  const p = new MeshPacket();
  p.type = PacketType.VideoSignaling;
  p.sourceUhid = from;
  p.payload = new TextEncoder().encode(JSON.stringify(body));
  return p;
}

function videoFramePacket(from: string, callId: string, isKeyframe: boolean): MeshPacket {
  const p = new MeshPacket();
  p.type = PacketType.VideoFrame;
  p.sourceUhid = from;
  p.payload = encodeVideoFrame(callId, new Uint8Array([0xDE, 0xAD]), isKeyframe, 1);
  return p;
}

function watchSyncPacket(from: string, body: unknown): MeshPacket {
  const p = new MeshPacket();
  p.type = PacketType.WatchSync;
  p.sourceUhid = from;
  p.payload = new TextEncoder().encode(JSON.stringify(body));
  return p;
}

function watchReactionPacket(from: string, sessionId: string, reaction: string): MeshPacket {
  const p = new MeshPacket();
  p.type = PacketType.WatchReaction;
  p.sourceUhid = from;
  p.payload = new TextEncoder().encode(JSON.stringify({ session_id: sessionId, reaction }));
  return p;
}

// ── VideoCallService — sendOffer ──────────────────────────────────────────────

describe("VideoCallService — sendOffer", () => {
  it("returns a non-empty call ID string", async () => {
    const { svc } = makeVideoSvc("alice");
    const callId = await svc.sendOffer("bob", ["h264"]);
    assert.ok(callId.length > 0);
  });

  it("throws for empty toUhid", async () => {
    const { svc } = makeVideoSvc();
    await assert.rejects(() => svc.sendOffer("", ["h264"]));
  });

  it("sends VideoSignaling with kind=offer to peer", async () => {
    const { sender, svc } = makeVideoSvc("alice");
    const callId = await svc.sendOffer("bob", ["h264", "vp8"]);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.equal(toBob.length, 1);
    assert.equal(toBob[0].packet.type, PacketType.VideoSignaling);

    const msg = JSON.parse(new TextDecoder().decode(toBob[0].packet.payload));
    assert.equal(msg.kind, "offer");
    assert.equal(msg.from_uhid, "alice");
    assert.equal(msg.to_uhid, "bob");
    assert.equal(msg.call_id, callId);
    assert.deepEqual(msg.proposed_codecs, ["h264", "vp8"]);
  });

  it("puts call in ringing state after outbound offer", async () => {
    const { svc } = makeVideoSvc("alice");
    const callId = await svc.sendOffer("bob", ["h264"]);
    const info = svc.getCallInfo(callId);
    assert.ok(info, "call info must exist");
    assert.equal(info!.state, "ringing");
    assert.equal(info!.isOutgoing, true);
  });
});

// ── VideoCallService — inbound signaling ─────────────────────────────────────

describe("VideoCallService — inbound offer", () => {
  it("fires onIncomingCall with state=ringing", async () => {
    const { svc } = makeVideoSvc("alice");
    const callId = crypto.randomUUID();

    let incoming = null as null | unknown;
    svc.onIncomingCall = (info) => { incoming = info; };

    await svc.onPacket(videoSignalingPacket("bob", {
      kind: "offer",
      call_id: callId,
      from_uhid: "bob",
      to_uhid: "alice",
      proposed_codecs: ["h264"],
    }));

    assert.ok(incoming !== null, "onIncomingCall not fired");
    assert.equal((incoming as { state: string }).state, "ringing");
    assert.equal((incoming as { callId: string }).callId, callId);
    assert.equal((incoming as { isOutgoing: boolean }).isOutgoing, false);
  });
});

describe("VideoCallService — inbound answer", () => {
  it("fires onCallConnected and sets state=connected", async () => {
    const { svc } = makeVideoSvc("alice");
    const callId = await svc.sendOffer("bob", ["h264"]);

    let connected = null as null | unknown;
    svc.onCallConnected = (info) => { connected = info; };

    await svc.onPacket(videoSignalingPacket("bob", {
      kind: "answer",
      call_id: callId,
      from_uhid: "bob",
      to_uhid: "alice",
    }));

    assert.ok(connected !== null, "onCallConnected not fired");
    assert.equal((connected as { state: string }).state, "connected");
  });
});

describe("VideoCallService — inbound hangup", () => {
  it("fires onCallEnded and sets state=ended", async () => {
    const { svc } = makeVideoSvc("alice");
    const callId = crypto.randomUUID();

    await svc.onPacket(videoSignalingPacket("bob", {
      kind: "offer", call_id: callId, from_uhid: "bob", to_uhid: "alice",
    }));

    let ended = null as null | unknown;
    svc.onCallEnded = (info) => { ended = info; };

    await svc.onPacket(videoSignalingPacket("bob", {
      kind: "hangup", call_id: callId, from_uhid: "bob", to_uhid: "alice",
    }));

    assert.ok(ended !== null, "onCallEnded not fired");
    assert.equal((ended as { state: string }).state, "ended");
  });
});

// ── VideoCallService — acceptCall ─────────────────────────────────────────────

describe("VideoCallService — acceptCall", () => {
  it("sends answer signaling to caller", async () => {
    const { sender, svc } = makeVideoSvc("alice");
    const callId = crypto.randomUUID();

    await svc.onPacket(videoSignalingPacket("bob", {
      kind: "offer", call_id: callId, from_uhid: "bob", to_uhid: "alice",
    }));
    sender.clear();

    await svc.acceptCall(callId);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.ok(toBob.length > 0, "expected answer unicast to bob");
    const msg = JSON.parse(new TextDecoder().decode(toBob[0].packet.payload));
    assert.equal(msg.kind, "answer");
  });

  it("fires onCallConnected after acceptCall", async () => {
    const { svc } = makeVideoSvc("alice");
    const callId = crypto.randomUUID();

    await svc.onPacket(videoSignalingPacket("bob", {
      kind: "offer", call_id: callId, from_uhid: "bob", to_uhid: "alice",
    }));

    let connected = null as null | unknown;
    svc.onCallConnected = (info) => { connected = info; };

    await svc.acceptCall(callId);

    assert.ok(connected !== null, "onCallConnected not fired");
    assert.equal((connected as { state: string }).state, "connected");
  });
});

// ── VideoCallService — hangUp ─────────────────────────────────────────────────

describe("VideoCallService — hangUp", () => {
  it("sends hangup signaling to peer", async () => {
    const { sender, svc } = makeVideoSvc("alice");
    const callId = await svc.sendOffer("bob", ["h264"]);
    sender.clear();

    await svc.hangUp(callId);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.ok(toBob.length > 0, "expected hangup unicast to bob");
    const msg = JSON.parse(new TextDecoder().decode(toBob[0].packet.payload));
    assert.equal(msg.kind, "hangup");
  });
});

// ── VideoCallService — sendFrame ──────────────────────────────────────────────

describe("VideoCallService — sendFrame", () => {
  it("sends VideoFrame packet when connected, returns true", async () => {
    const { sender, svc } = makeVideoSvc("alice");
    const callId = await svc.sendOffer("bob", ["h264"]);

    await svc.onPacket(videoSignalingPacket("bob", {
      kind: "answer", call_id: callId, from_uhid: "bob", to_uhid: "alice",
    }));
    sender.clear();

    const video = new Uint8Array([0xDE, 0xAD, 0xBE, 0xEF]);
    const sent = await svc.sendFrame(callId, video, true);

    assert.equal(sent, true);
    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.ok(toBob.length > 0, "expected VideoFrame unicast to bob");
    assert.equal(toBob[0].packet.type, PacketType.VideoFrame);

    // Wire format: [16 callId][4 seq][8 ts][1 isKeyframe][N payload] = 29-byte header
    assert.ok(toBob[0].packet.payload.length >= 29 + video.length, "payload too short");
    assert.equal(toBob[0].packet.payload[28], 1, "isKeyframe byte must be 1 at offset 28");
  });

  it("returns false when call is not connected", async () => {
    const { svc } = makeVideoSvc("alice");
    const callId = await svc.sendOffer("bob", ["h264"]);
    // Still ringing — not connected.
    const sent = await svc.sendFrame(callId, new Uint8Array([1, 2, 3]), false);
    assert.equal(sent, false);
  });
});

// ── VideoCallService — requestKeyframe / notifyQualityChange ──────────────────

describe("VideoCallService — requestKeyframe", () => {
  it("sends keyframe_request when call is connected", async () => {
    const { sender, svc } = makeVideoSvc("alice");
    const callId = await svc.sendOffer("bob", ["h264"]);

    await svc.onPacket(videoSignalingPacket("bob", {
      kind: "answer", call_id: callId, from_uhid: "bob", to_uhid: "alice",
    }));
    sender.clear();

    await svc.requestKeyframe(callId);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.ok(toBob.length > 0, "expected keyframe_request unicast to bob");
    const msg = JSON.parse(new TextDecoder().decode(toBob[0].packet.payload));
    assert.equal(msg.kind, "keyframe_request");
  });

  it("does not send when call is not connected", async () => {
    const { sender, svc } = makeVideoSvc("alice");
    const callId = await svc.sendOffer("bob", ["h264"]);
    // Still ringing.
    await svc.requestKeyframe(callId);
    assert.equal(sender.unicasts.filter((u) => u.nextHopUhid === "bob").length, 1,
      "only the offer must be sent — no keyframe_request when not connected");
  });
});

describe("VideoCallService — notifyQualityChange", () => {
  it("sends quality_change with correct params when connected", async () => {
    const { sender, svc } = makeVideoSvc("alice");
    const callId = await svc.sendOffer("bob", ["h264"]);

    await svc.onPacket(videoSignalingPacket("bob", {
      kind: "answer", call_id: callId, from_uhid: "bob", to_uhid: "alice",
    }));
    sender.clear();

    await svc.notifyQualityChange(callId, 640, 480, 15, 500);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.ok(toBob.length > 0, "expected quality_change unicast to bob");
    const msg = JSON.parse(new TextDecoder().decode(toBob[0].packet.payload));
    assert.equal(msg.kind, "quality_change");
    assert.equal(msg.width, 640);
    assert.equal(msg.height, 480);
    assert.equal(msg.fps, 15);
    assert.equal(msg.bitrate_kbps, 500);
  });

  it("does not send when call is not connected", async () => {
    const { sender, svc } = makeVideoSvc("alice");
    const callId = await svc.sendOffer("bob", ["h264"]);
    // Still ringing.
    await svc.notifyQualityChange(callId, 640, 480, 15, 500);
    assert.equal(sender.unicasts.filter((u) => u.nextHopUhid === "bob").length, 1,
      "only the offer must be sent — no quality_change when not connected");
  });
});

// ── VideoCallService — inbound VideoFrame ─────────────────────────────────────

describe("VideoCallService — inbound VideoFrame", () => {
  it("fires onFrameReceived with correct callId and isKeyframe", async () => {
    const { svc } = makeVideoSvc("alice");
    const callId = await svc.sendOffer("bob", ["h264"]);

    await svc.onPacket(videoSignalingPacket("bob", {
      kind: "answer", call_id: callId, from_uhid: "bob", to_uhid: "alice",
    }));

    let gotFrame = null as null | unknown;
    svc.onFrameReceived = (evt) => { gotFrame = evt; };

    await svc.onPacket(videoFramePacket("bob", callId, true));

    assert.ok(gotFrame !== null, "onFrameReceived not fired");
    assert.equal((gotFrame as { callId: string }).callId, callId);
    assert.equal((gotFrame as { isKeyframe: boolean }).isKeyframe, true);
    assert.equal((gotFrame as { fromUhid: string }).fromUhid, "bob");
  });

  it("does not fire onFrameReceived when call is not connected", async () => {
    const { svc } = makeVideoSvc("alice");
    const callId = crypto.randomUUID();

    let fired = false;
    svc.onFrameReceived = () => { fired = true; };

    // No call registered — frame should be silently ignored.
    await svc.onPacket(videoFramePacket("bob", callId, false));

    assert.equal(fired, false, "onFrameReceived must not fire for unknown call");
  });
});

// ── WatchTogetherService — inviteToSession ────────────────────────────────────

describe("WatchTogetherService — inviteToSession", () => {
  it("sends WatchSync with kind=join to each invitee", async () => {
    const { sender, svc } = makeWatchSvc("alice");
    const sid = crypto.randomUUID();

    await svc.inviteToSession(sid, "content-1", ["bob", "carol"]);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    const toCarol = sender.unicasts.filter((u) => u.nextHopUhid === "carol");
    assert.ok(toBob.length > 0, "expected WatchSync to bob");
    assert.ok(toCarol.length > 0, "expected WatchSync to carol");
    assert.equal(toBob[0].packet.type, PacketType.WatchSync);

    const payload = JSON.parse(new TextDecoder().decode(toBob[0].packet.payload));
    assert.equal(payload.kind, "join");
    assert.equal(payload.content_id, "content-1");
    assert.equal(payload.session_id, sid);
  });

  it("throws for empty sessionId", async () => {
    const { svc } = makeWatchSvc();
    await assert.rejects(() => svc.inviteToSession("", "content", ["bob"]));
  });

  it("does not send to self", async () => {
    const { sender, svc } = makeWatchSvc("alice");
    const sid = crypto.randomUUID();

    await svc.inviteToSession(sid, "c1", ["bob"]);

    const toSelf = sender.unicasts.filter((u) => u.nextHopUhid === "alice");
    assert.equal(toSelf.length, 0, "must not send join to self");
  });
});

// ── WatchTogetherService — play / pause / seek / setSpeed ─────────────────────

describe("WatchTogetherService — play", () => {
  it("sends WatchSync with kind=play and positionMs", async () => {
    const { sender, svc } = makeWatchSvc("alice");
    const sid = crypto.randomUUID();
    await svc.inviteToSession(sid, "c1", ["bob"]);
    sender.clear();

    await svc.play(sid, 5000);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.ok(toBob.length > 0, "expected WatchSync to bob");
    const msg = JSON.parse(new TextDecoder().decode(toBob[0].packet.payload));
    assert.equal(msg.kind, "play");
    assert.equal(msg.position_ms, 5000);
  });

  it("does nothing for unknown session", async () => {
    const { sender, svc } = makeWatchSvc("alice");
    await svc.play(crypto.randomUUID(), 0);
    assert.equal(sender.unicasts.length, 0);
  });
});

describe("WatchTogetherService — pause", () => {
  it("sends WatchSync with kind=pause", async () => {
    const { sender, svc } = makeWatchSvc("alice");
    const sid = crypto.randomUUID();
    await svc.inviteToSession(sid, "c1", ["bob"]);
    sender.clear();

    await svc.pause(sid, 12000);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.ok(toBob.length > 0, "expected WatchSync to bob");
    const msg = JSON.parse(new TextDecoder().decode(toBob[0].packet.payload));
    assert.equal(msg.kind, "pause");
    assert.equal(msg.position_ms, 12000);
  });
});

describe("WatchTogetherService — seek", () => {
  it("sends WatchSync with kind=seek and correct positionMs", async () => {
    const { sender, svc } = makeWatchSvc("alice");
    const sid = crypto.randomUUID();
    await svc.inviteToSession(sid, "c1", ["bob"]);
    sender.clear();

    await svc.seek(sid, 30000);

    const msg = JSON.parse(new TextDecoder().decode(
      sender.unicasts.find((u) => u.nextHopUhid === "bob")!.packet.payload,
    ));
    assert.equal(msg.kind, "seek");
    assert.equal(msg.position_ms, 30000);
  });
});

describe("WatchTogetherService — setSpeed", () => {
  it("sends WatchSync with kind=speed and correct playback_speed", async () => {
    const { sender, svc } = makeWatchSvc("alice");
    const sid = crypto.randomUUID();
    await svc.inviteToSession(sid, "c1", ["bob"]);
    sender.clear();

    await svc.setSpeed(sid, 1.5);

    const msg = JSON.parse(new TextDecoder().decode(
      sender.unicasts.find((u) => u.nextHopUhid === "bob")!.packet.payload,
    ));
    assert.equal(msg.kind, "speed");
    assert.equal(msg.playback_speed, 1.5);
  });
});

// ── WatchTogetherService — sendReaction ──────────────────────────────────────

describe("WatchTogetherService — sendReaction", () => {
  it("sends WatchReaction to all members except self", async () => {
    const { sender, svc } = makeWatchSvc("alice");
    const sid = crypto.randomUUID();
    await svc.inviteToSession(sid, "c1", ["bob", "carol"]);
    sender.clear();

    await svc.sendReaction(sid, "🔥");

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    const toCarol = sender.unicasts.filter((u) => u.nextHopUhid === "carol");
    const toSelf = sender.unicasts.filter((u) => u.nextHopUhid === "alice");

    assert.ok(toBob.length > 0, "bob should receive reaction");
    assert.ok(toCarol.length > 0, "carol should receive reaction");
    assert.equal(toSelf.length, 0, "self must not receive reaction");

    assert.equal(toBob[0].packet.type, PacketType.WatchReaction);
    const payload = JSON.parse(new TextDecoder().decode(toBob[0].packet.payload));
    assert.equal(payload.reaction, "🔥");
  });

  it("does nothing for unknown session", async () => {
    const { sender, svc } = makeWatchSvc("alice");
    await svc.sendReaction(crypto.randomUUID(), "❤️");
    assert.equal(sender.unicasts.length, 0);
  });
});

// ── WatchTogetherService — onPacket / inbound "join" ─────────────────────────

describe("WatchTogetherService — inbound join (new session)", () => {
  it("fires onSessionInvited with host and contentId", async () => {
    const { svc } = makeWatchSvc("alice");
    const sid = crypto.randomUUID();

    let invitedSession = null as null | unknown;
    svc.onSessionInvited = (s) => { invitedSession = s; };

    await svc.onPacket(watchSyncPacket("bob", {
      session_id: sid,
      kind: "join",
      content_id: "movie-42",
      sent_at_ms: Date.now(),
    }));

    assert.ok(invitedSession !== null, "onSessionInvited not fired");
    assert.equal((invitedSession as { hostUhid: string }).hostUhid, "bob");
    assert.equal((invitedSession as { contentId: string }).contentId, "movie-42");
    assert.equal((invitedSession as { sessionId: string }).sessionId, sid);
  });
});

describe("WatchTogetherService — inbound join (existing session adds member)", () => {
  it("adds new member who can then receive reactions", async () => {
    const { sender, svc } = makeWatchSvc("alice");
    const sid = crypto.randomUUID();
    await svc.inviteToSession(sid, "c1", ["bob"]);

    // Carol joins via inbound join sync.
    await svc.onPacket(watchSyncPacket("carol", {
      session_id: sid, kind: "join", sent_at_ms: Date.now(),
    }));
    sender.clear();

    await svc.sendReaction(sid, "👍");
    const toCarol = sender.unicasts.filter((u) => u.nextHopUhid === "carol");
    assert.ok(toCarol.length > 0, "carol joined so she must receive reaction");
  });
});

// ── WatchTogetherService — onPacket / inbound play / pause ───────────────────

describe("WatchTogetherService — inbound play", () => {
  it("fires onSyncApplied with RTT-compensated position >= raw position", async () => {
    const { svc } = makeWatchSvc("alice");
    const sid = crypto.randomUUID();
    await svc.inviteToSession(sid, "c1", ["bob"]);

    let gotEvent = null as null | unknown;
    svc.onSyncApplied = (evt) => { gotEvent = evt; };

    const now = Date.now();
    await svc.onPacket(watchSyncPacket("bob", {
      session_id: sid,
      kind: "play",
      position_ms: 10000,
      playback_speed: 1.0,
      sent_at_ms: now,
    }));

    assert.ok(gotEvent !== null, "onSyncApplied not fired");
    const evt = gotEvent as { kind: string; positionMs: number };
    assert.equal(evt.kind, "play");
    assert.ok(evt.positionMs >= 10000, `compensated position ${evt.positionMs} must be >= 10000`);
  });
});

describe("WatchTogetherService — inbound pause", () => {
  it("fires onSyncApplied with exact raw position (no RTT compensation for pause)", async () => {
    const { svc } = makeWatchSvc("alice");
    const sid = crypto.randomUUID();
    await svc.inviteToSession(sid, "c1", ["bob"]);

    let gotEvent = null as null | unknown;
    svc.onSyncApplied = (evt) => { gotEvent = evt; };

    await svc.onPacket(watchSyncPacket("bob", {
      session_id: sid,
      kind: "pause",
      position_ms: 12345,
      playback_speed: 0,
      sent_at_ms: Date.now(),
    }));

    assert.ok(gotEvent !== null, "onSyncApplied not fired");
    const evt = gotEvent as { kind: string; positionMs: number };
    assert.equal(evt.kind, "pause");
    assert.equal(evt.positionMs, 12345, "pause must use exact raw position — no RTT compensation");
  });
});

// ── WatchTogetherService — onPacket / inbound leave ──────────────────────────

describe("WatchTogetherService — inbound leave", () => {
  it("removes the leaving member from the session", async () => {
    const { sender, svc } = makeWatchSvc("alice");
    const sid = crypto.randomUUID();
    await svc.inviteToSession(sid, "c1", ["bob"]);

    await svc.onPacket(watchSyncPacket("bob", {
      session_id: sid, kind: "leave", sent_at_ms: Date.now(),
    }));
    sender.clear();

    // Bob left — sendReaction must not reach him.
    await svc.sendReaction(sid, "👋");
    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.equal(toBob.length, 0, "bob left so must not receive reaction");
  });
});

// ── WatchTogetherService — onPacket / inbound reaction ───────────────────────

describe("WatchTogetherService — inbound WatchReaction", () => {
  it("fires onReactionReceived with fromUhid and reaction", async () => {
    const { svc } = makeWatchSvc("alice");
    const sid = crypto.randomUUID();
    await svc.inviteToSession(sid, "c1", ["bob"]);

    let gotEvent = null as null | unknown;
    svc.onReactionReceived = (evt) => { gotEvent = evt; };

    await svc.onPacket(watchReactionPacket("bob", sid, "❤️"));

    assert.ok(gotEvent !== null, "onReactionReceived not fired");
    const evt = gotEvent as { fromUhid: string; reaction: string };
    assert.equal(evt.fromUhid, "bob");
    assert.equal(evt.reaction, "❤️");
  });
});

// ── WatchTogetherService — onPacket / inbound end ────────────────────────────

describe("WatchTogetherService — inbound end", () => {
  it("fires onSessionEnded and removes session", async () => {
    const { svc } = makeWatchSvc("alice");
    const sid = crypto.randomUUID();
    await svc.inviteToSession(sid, "c1", ["bob"]);

    let endedId = "";
    svc.onSessionEnded = (id) => { endedId = id; };

    await svc.onPacket(watchSyncPacket("bob", {
      session_id: sid, kind: "end", sent_at_ms: Date.now(),
    }));

    assert.equal(endedId, sid, "onSessionEnded must fire with correct sessionId");
    assert.equal(svc.getSession(sid), undefined, "session must be removed after end");
  });
});
