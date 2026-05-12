/**
 * Unit tests for VoiceCallService and GroupVoiceCallService.
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/voice.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import {
  VoiceCallService,
  encodeVoiceFrame,
} from "../src/voice/VoiceCallService.js";
import { GroupVoiceCallService } from "../src/voice/GroupVoiceCallService.js";
import { FakeMeshSender } from "./fakes.js";

// ── Helpers ───────────────────────────────────────────────────────────────────

function makeVoiceSvc(uhid = "alice") {
  const sender = new FakeMeshSender(uhid);
  const svc = new VoiceCallService(sender);
  return { sender, svc };
}

function makeGroupSvc(uhid = "alice") {
  const sender = new FakeMeshSender(uhid);
  const svc = new GroupVoiceCallService(sender);
  return { sender, svc };
}

function signalingPacket(from: string, body: unknown): MeshPacket {
  const p = new MeshPacket();
  p.type = PacketType.VoiceSignaling;
  p.sourceUhid = from;
  p.payload = new TextEncoder().encode(JSON.stringify(body));
  return p;
}

function voiceCallPacket(from: string, callId: string): MeshPacket {
  const p = new MeshPacket();
  p.type = PacketType.VoiceCall;
  p.sourceUhid = from;
  p.payload = encodeVoiceFrame(callId, new Uint8Array([0xCC, 0xDD]), false);
  return p;
}

// ── VoiceCallService — sendOffer ──────────────────────────────────────────────

describe("VoiceCallService — sendOffer", () => {
  it("sends VoiceSignaling with kind=offer to callee", async () => {
    const { sender, svc } = makeVoiceSvc("alice");

    const callId = await svc.sendOffer("bob", ["opus"], 48000);
    assert.ok(callId.length > 0);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.equal(toBob.length, 1);
    assert.equal(toBob[0].packet.type, PacketType.VoiceSignaling);

    const msg = JSON.parse(new TextDecoder().decode(toBob[0].packet.payload));
    assert.equal(msg.kind, "offer");
    assert.equal(msg.from_uhid, "alice");
    assert.equal(msg.to_uhid, "bob");
    assert.equal(msg.call_id, callId);
  });

  it("throws for empty toUhid", async () => {
    const { svc } = makeVoiceSvc();
    await assert.rejects(() => svc.sendOffer("", ["opus"], 48000));
  });
});

// ── VoiceCallService — inbound signaling ─────────────────────────────────────

describe("VoiceCallService — inbound offer", () => {
  it("fires onIncomingCall with state=ringing", async () => {
    const { svc } = makeVoiceSvc("alice");
    const callId = crypto.randomUUID();

    let incoming = null as null | unknown;
    svc.onIncomingCall = (info) => { incoming = info; };

    await svc.onPacket(signalingPacket("bob", {
      kind: "offer",
      call_id: callId,
      from_uhid: "bob",
      to_uhid: "alice",
      proposed_codecs: ["opus"],
    }));

    assert.ok(incoming !== null, "onIncomingCall not fired");
    assert.equal((incoming as { state: string }).state, "ringing");
    assert.equal((incoming as { callId: string }).callId, callId);
  });
});

describe("VoiceCallService — inbound answer", () => {
  it("fires onCallConnected and sets state=connected", async () => {
    const { sender, svc } = makeVoiceSvc("alice");

    const callId = await svc.sendOffer("bob", ["opus"], 48000);
    sender.clear();

    let connected = null as null | unknown;
    svc.onCallConnected = (info) => { connected = info; };

    await svc.onPacket(signalingPacket("bob", {
      kind: "answer",
      call_id: callId,
      from_uhid: "bob",
      to_uhid: "alice",
    }));

    assert.ok(connected !== null, "onCallConnected not fired");
    assert.equal((connected as { state: string }).state, "connected");
  });
});

describe("VoiceCallService — inbound hangup", () => {
  it("fires onCallEnded and sets state=ended", async () => {
    const { svc } = makeVoiceSvc("alice");
    const callId = crypto.randomUUID();

    // Create an inbound call first.
    await svc.onPacket(signalingPacket("bob", {
      kind: "offer",
      call_id: callId,
      from_uhid: "bob",
      to_uhid: "alice",
    }));

    let ended = null as null | unknown;
    svc.onCallEnded = (info) => { ended = info; };

    await svc.onPacket(signalingPacket("bob", {
      kind: "hangup",
      call_id: callId,
      from_uhid: "bob",
      to_uhid: "alice",
    }));

    assert.ok(ended !== null, "onCallEnded not fired");
    assert.equal((ended as { state: string }).state, "ended");
  });
});

// ── VoiceCallService — acceptCall ─────────────────────────────────────────────

describe("VoiceCallService — acceptCall", () => {
  it("sends answer signaling and sets state=connected", async () => {
    const { sender, svc } = makeVoiceSvc("alice");
    const callId = crypto.randomUUID();

    // Inbound offer → ringing.
    await svc.onPacket(signalingPacket("bob", {
      kind: "offer", call_id: callId, from_uhid: "bob", to_uhid: "alice",
    }));
    sender.clear();

    let connected = null as null | unknown;
    svc.onCallConnected = (info) => { connected = info; };

    await svc.acceptCall(callId);

    assert.ok(connected !== null, "onCallConnected not fired after acceptCall");
    assert.equal((connected as { state: string }).state, "connected");

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.ok(toBob.length > 0, "expected answer unicast to bob");
    const msg = JSON.parse(new TextDecoder().decode(toBob[0].packet.payload));
    assert.equal(msg.kind, "answer");
  });
});

// ── VoiceCallService — hangUp ─────────────────────────────────────────────────

describe("VoiceCallService — hangUp", () => {
  it("sends cancel when call is still ringing (outbound)", async () => {
    const { sender, svc } = makeVoiceSvc("alice");
    const callId = await svc.sendOffer("bob", ["opus"], 48000);
    sender.clear();

    await svc.hangUp(callId);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.ok(toBob.length > 0);
    const msg = JSON.parse(new TextDecoder().decode(toBob[0].packet.payload));
    assert.equal(msg.kind, "cancel");
  });

  it("sends hangup when call is connected", async () => {
    const { sender, svc } = makeVoiceSvc("alice");
    const callId = await svc.sendOffer("bob", ["opus"], 48000);

    // Answer to make it connected.
    await svc.onPacket(signalingPacket("bob", {
      kind: "answer", call_id: callId, from_uhid: "bob", to_uhid: "alice",
    }));
    sender.clear();

    await svc.hangUp(callId);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.ok(toBob.length > 0);
    const msg = JSON.parse(new TextDecoder().decode(toBob[0].packet.payload));
    assert.equal(msg.kind, "hangup");
  });
});

// ── VoiceCallService — sendFrame ──────────────────────────────────────────────

describe("VoiceCallService — sendFrame", () => {
  it("sends VoiceCall packet when call is connected", async () => {
    const { sender, svc } = makeVoiceSvc("alice");
    const callId = await svc.sendOffer("bob", ["opus"], 48000);

    await svc.onPacket(signalingPacket("bob", {
      kind: "answer", call_id: callId, from_uhid: "bob", to_uhid: "alice",
    }));
    sender.clear();

    const sent = await svc.sendFrame(callId, new Uint8Array([1, 2, 3, 4]), false);

    assert.equal(sent, true);
    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    assert.ok(toBob.length > 0, "expected VoiceCall unicast to bob");
    assert.equal(toBob[0].packet.type, PacketType.VoiceCall);
  });

  it("returns false when call is not connected", async () => {
    const { svc } = makeVoiceSvc("alice");
    const callId = await svc.sendOffer("bob", ["opus"], 48000);
    // Still in ringing state — not connected.
    const sent = await svc.sendFrame(callId, new Uint8Array([1, 2, 3]), false);
    assert.equal(sent, false);
  });
});

// ── VoiceCallService — inbound frame ─────────────────────────────────────────

describe("VoiceCallService — onPacket VoiceCall frame", () => {
  it("fires onFrameReceived when call is connected", async () => {
    const { svc } = makeVoiceSvc("alice");
    const callId = await svc.sendOffer("bob", ["opus"], 48000);

    await svc.onPacket(signalingPacket("bob", {
      kind: "answer", call_id: callId, from_uhid: "bob", to_uhid: "alice",
    }));

    let gotFrame = null as null | unknown;
    svc.onFrameReceived = (evt) => { gotFrame = evt; };

    await svc.onPacket(voiceCallPacket("bob", callId));

    assert.ok(gotFrame !== null, "onFrameReceived not fired");
    assert.equal((gotFrame as { callId: string }).callId, callId);
  });
});

// ── GroupVoiceCallService — invite ────────────────────────────────────────────

describe("GroupVoiceCallService — invite", () => {
  it("sends invite unicast to each member", async () => {
    const { sender, svc } = makeGroupSvc("alice");
    const callId = crypto.randomUUID();

    await svc.invite(callId, ["bob", "carol"]);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    const toCarol = sender.unicasts.filter((u) => u.nextHopUhid === "carol");
    assert.ok(toBob.length > 0, "expected invite to bob");
    assert.ok(toCarol.length > 0, "expected invite to carol");

    const msg = JSON.parse(new TextDecoder().decode(toBob[0].packet.payload));
    assert.equal(msg.kind, "invite");
  });

  it("throws for empty members list", async () => {
    const { svc } = makeGroupSvc();
    await assert.rejects(() => svc.invite(crypto.randomUUID(), []));
  });
});

// ── GroupVoiceCallService — inbound signaling ─────────────────────────────────

describe("GroupVoiceCallService — inbound invite", () => {
  it("fires onGroupCallInvited with correct host uhid", async () => {
    const { svc } = makeGroupSvc("alice");
    const callId = crypto.randomUUID();

    let hostUhid = "";
    svc.onGroupCallInvited = (info) => { hostUhid = info.hostUhid; };

    await svc.onPacket(signalingPacket("bob", {
      kind: "invite",
      call_id: callId,
      from_uhid: "bob",
      to_uhid: "alice",
      invited_uhids: ["alice", "carol"],
    }));

    assert.equal(hostUhid, "bob");
  });
});

describe("GroupVoiceCallService — inbound join", () => {
  it("fires onMembershipChanged when someone joins existing call", async () => {
    const { svc } = makeGroupSvc("alice");
    const callId = crypto.randomUUID();

    // Create session by hosting an invite.
    await svc.invite(callId, ["bob"]);

    let carolJoined = false;
    svc.onMembershipChanged = (info) => { carolJoined = info.members.has("carol"); };

    await svc.onPacket(signalingPacket("carol", {
      kind: "join",
      call_id: callId,
      from_uhid: "carol",
      to_uhid: "alice",
    }));

    assert.ok(carolJoined, "carol should appear in members after join");
  });
});

describe("GroupVoiceCallService — inbound leave", () => {
  it("fires onMembershipChanged and removes member on leave", async () => {
    const { svc } = makeGroupSvc("alice");
    const callId = crypto.randomUUID();

    await svc.invite(callId, ["bob", "carol"]);

    let bobRemoved = false;
    svc.onMembershipChanged = (info) => { bobRemoved = !info.members.has("bob"); };

    await svc.onPacket(signalingPacket("bob", {
      kind: "leave",
      call_id: callId,
      from_uhid: "bob",
      to_uhid: "alice",
    }));

    assert.ok(bobRemoved, "bob should be removed from members after leave");
  });
});

describe("GroupVoiceCallService — inbound kick", () => {
  it("fires onMembershipChanged and removes kicked member", async () => {
    const { svc } = makeGroupSvc("alice");
    const callId = crypto.randomUUID();

    await svc.invite(callId, ["bob", "carol"]);

    let bobKicked = false;
    svc.onMembershipChanged = (info) => { bobKicked = !info.members.has("bob"); };

    await svc.onPacket(signalingPacket("alice", {
      kind: "kick",
      call_id: callId,
      from_uhid: "alice",
      to_uhid: "bob",
      kicked_uhid: "bob",
    }));

    assert.ok(bobKicked, "bob should be removed from members after kick");
  });
});

// ── GroupVoiceCallService — sendFrame ─────────────────────────────────────────

describe("GroupVoiceCallService — sendFrame", () => {
  it("fans out VoiceCall to all members except self", async () => {
    const { sender, svc } = makeGroupSvc("alice");
    const callId = crypto.randomUUID();

    await svc.invite(callId, ["bob", "carol"]);
    sender.clear();

    await svc.sendFrame(callId, new Uint8Array([1, 2, 3]), false, 0);

    const toBob = sender.unicasts.filter((u) => u.nextHopUhid === "bob");
    const toCarol = sender.unicasts.filter((u) => u.nextHopUhid === "carol");
    const toAlice = sender.unicasts.filter((u) => u.nextHopUhid === "alice");
    assert.ok(toBob.length > 0, "bob should receive frame");
    assert.ok(toCarol.length > 0, "carol should receive frame");
    assert.equal(toAlice.length, 0, "alice (self) must not receive frame");
  });
});
