/**
 * Unit tests for the VoicePtt(15) + ScreenShare(32) media-frame bindings. BINARY frames sharing
 * the 29-byte header (call_id big-endian, sequence/timestamp little-endian, flag). A fake
 * IMeshSender captures directed sends. Mirrors the C# MediaFrameTests (10 tests), plus the
 * canonical byte-identity gate from fixtures/media/vectors.json (2 voice_ptt + 2 screen_share).
 *
 * SPDX-License-Identifier: MIT
 *
 * Run with: tsx --test typescript/tests/mediaFrame.test.ts
 */

import { describe, it } from "node:test";
import { strict as assert } from "node:assert";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PacketType } from "../src/protocol/PacketType.js";
import {
  MediaFrameCodec,
  VoicePttService,
  ScreenShareService,
} from "../src/media/index.js";
import type {
  VoicePttFrame,
  ScreenShareFrame,
  VoicePttFrameReceived,
  ScreenShareFrameReceived,
} from "../src/media/index.js";
import { FakeMeshSender } from "./fakes.js";

const CALL_ID = "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f";

/** Byte-identity gate: hex of the serialized frame, matching each vectors.json expected_hex. */
function hex(bytes: Uint8Array): string {
  return Buffer.from(bytes).toString("hex");
}

// ── Byte-identity gates ───────────────────────────────────────────────────────

describe("MediaFrameCodec — canonical byte-identity", () => {
  // Mirrors VoicePtt_Frame_SerializesToCanonicalBytes.
  it("serializes a VoicePtt frame to canonical bytes", () => {
    const f: VoicePttFrame = {
      callId: CALL_ID,
      sequence: 42,
      timestampMs: 1_700_000_000_000,
      isSilence: false,
      encodedPayload: new Uint8Array([0xaa, 0xbb, 0xcc]),
    };
    assert.equal(
      hex(MediaFrameCodec.serializeVoicePtt(f)),
      "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2a0000000068e5cf8b01000000aabbcc",
    );
  });

  // Mirrors VoicePtt_SilenceEmpty_SerializesToCanonicalBytes.
  it("serializes an empty silence VoicePtt frame to canonical bytes", () => {
    const f: VoicePttFrame = {
      callId: CALL_ID,
      sequence: 43,
      timestampMs: 1_700_000_000_020,
      isSilence: true,
      encodedPayload: new Uint8Array(0),
    };
    assert.equal(
      hex(MediaFrameCodec.serializeVoicePtt(f)),
      "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2b0000001468e5cf8b01000001",
    );
  });

  // Mirrors ScreenShare_Keyframe_SerializesToCanonicalBytes.
  it("serializes a ScreenShare keyframe to canonical bytes", () => {
    const f: ScreenShareFrame = {
      callId: CALL_ID,
      sequence: 7,
      timestampMs: 1_700_000_000_000,
      isKeyframe: true,
      encodedPayload: new Uint8Array([0x11, 0x22, 0x33, 0x44]),
    };
    assert.equal(
      hex(MediaFrameCodec.serializeScreenShare(f)),
      "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f070000000068e5cf8b0100000111223344",
    );
  });

  // Mirrors ScreenShare_DeltaEmpty_SerializesToCanonicalBytes.
  it("serializes an empty delta ScreenShare frame (nil call id) to canonical bytes", () => {
    const f: ScreenShareFrame = {
      callId: "00000000-0000-0000-0000-000000000000",
      sequence: 0,
      timestampMs: 0,
      isKeyframe: false,
      encodedPayload: new Uint8Array(0),
    };
    assert.equal(
      hex(MediaFrameCodec.serializeScreenShare(f)),
      "0000000000000000000000000000000000000000000000000000000000",
    );
  });

  // Cross-language parity: reproduce every vector in fixtures/media/vectors.json byte-for-byte.
  it("reproduces every fixture vector byte-for-byte (2 voice_ptt + 2 screen_share)", () => {
    const vectorsPath = fileURLToPath(
      new URL("../../fixtures/media/vectors.json", import.meta.url),
    );
    const V = JSON.parse(readFileSync(vectorsPath, "utf8")) as {
      voice_ptt_vectors: {
        name: string;
        call_id: string;
        sequence: number;
        timestamp_ms: number;
        is_silence: boolean;
        payload_hex: string;
        expected_hex: string;
      }[];
      screen_share_vectors: {
        name: string;
        call_id: string;
        sequence: number;
        timestamp_ms: number;
        is_keyframe: boolean;
        payload_hex: string;
        expected_hex: string;
      }[];
    };

    assert.equal(V.voice_ptt_vectors.length, 2, "expected 2 voice_ptt vectors");
    assert.equal(V.screen_share_vectors.length, 2, "expected 2 screen_share vectors");

    for (const vec of V.voice_ptt_vectors) {
      const bytes = MediaFrameCodec.serializeVoicePtt({
        callId: vec.call_id,
        sequence: vec.sequence,
        timestampMs: vec.timestamp_ms,
        isSilence: vec.is_silence,
        encodedPayload: fromHex(vec.payload_hex),
      });
      assert.equal(hex(bytes), vec.expected_hex, `voice_ptt vector "${vec.name}"`);
    }

    for (const vec of V.screen_share_vectors) {
      const bytes = MediaFrameCodec.serializeScreenShare({
        callId: vec.call_id,
        sequence: vec.sequence,
        timestampMs: vec.timestamp_ms,
        isKeyframe: vec.is_keyframe,
        encodedPayload: fromHex(vec.payload_hex),
      });
      assert.equal(hex(bytes), vec.expected_hex, `screen_share vector "${vec.name}"`);
    }
  });
});

// ── Round-trips ───────────────────────────────────────────────────────────────

describe("MediaFrameCodec — round-trips", () => {
  // Mirrors VoicePtt_RoundTrips.
  it("round-trips a VoicePtt frame", () => {
    const f: VoicePttFrame = {
      callId: CALL_ID,
      sequence: 99,
      timestampMs: 123456789,
      isSilence: true,
      encodedPayload: new Uint8Array([1, 2, 3, 4, 5]),
    };
    const back = MediaFrameCodec.deserializeVoicePtt(MediaFrameCodec.serializeVoicePtt(f));
    assert.equal(back.callId, CALL_ID);
    assert.equal(back.sequence, 99);
    assert.equal(back.timestampMs, 123456789);
    assert.equal(back.isSilence, true);
    assert.deepEqual(back.encodedPayload, new Uint8Array([1, 2, 3, 4, 5]));
  });

  // Mirrors ScreenShare_RoundTrips_KeyframeAndCallIdBigEndian.
  it("round-trips a ScreenShare frame (keyframe + call id big-endian)", () => {
    const f: ScreenShareFrame = {
      callId: CALL_ID,
      sequence: 5,
      timestampMs: 999,
      isKeyframe: true,
      encodedPayload: new Uint8Array([0xff]),
    };
    const back = MediaFrameCodec.deserializeScreenShare(MediaFrameCodec.serializeScreenShare(f));
    assert.equal(back.callId, CALL_ID);
    assert.equal(back.isKeyframe, true);
    assert.deepEqual(back.encodedPayload, new Uint8Array([0xff]));
  });
});

// ── Behaviour ─────────────────────────────────────────────────────────────────

describe("VoicePttService — send + handle", () => {
  // Mirrors VoicePtt_Send_EmitsDirectedFrame_AndHandleRaisesEvent.
  it("emits a directed frame and handle raises the event", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    const svc = new VoicePttService(sender);
    const frame: VoicePttFrame = {
      callId: CALL_ID,
      sequence: 42,
      timestampMs: 1_700_000_000_000,
      isSilence: false,
      encodedPayload: new Uint8Array([0xaa, 0xbb, 0xcc]),
    };

    assert.equal(await svc.sendFrame("aether:bob:02", frame), true);
    assert.equal(sender.unicasts.length, 1);
    const sent = sender.unicasts[0]!;
    assert.equal(sent.packet.type, PacketType.VoicePtt);
    assert.equal(sent.nextHopUhid, "aether:bob:02");
    assert.equal(sent.packet.sourceUhid, "aether:alice:01");
    assert.equal(sent.packet.destinationUhid, "aether:bob:02");

    let got: VoicePttFrameReceived | undefined;
    svc.onFrameReceived = (e) => { got = e; };
    sent.packet.sourceUhid = "aether:alice:01";
    assert.equal(await svc.handle(sent.packet), true);
    assert.ok(got);
    assert.equal(got!.frame.sequence, 42);
    assert.equal(got!.fromUhid, "aether:alice:01");
    assert.deepEqual(got!.frame.encodedPayload, new Uint8Array([0xaa, 0xbb, 0xcc]));
  });
});

describe("ScreenShareService — send + handle", () => {
  // Mirrors ScreenShare_Send_EmitsDirectedFrame_AndHandleRaisesEvent.
  it("emits a directed frame and handle raises the event", async () => {
    const sender = new FakeMeshSender("aether:alice:01");
    const svc = new ScreenShareService(sender);
    const frame: ScreenShareFrame = {
      callId: CALL_ID,
      sequence: 7,
      timestampMs: 1_700_000_000_000,
      isKeyframe: true,
      encodedPayload: new Uint8Array([0x11, 0x22, 0x33, 0x44]),
    };

    assert.equal(await svc.sendFrame("aether:bob:02", frame), true);
    assert.equal(sender.unicasts.length, 1);
    const sent = sender.unicasts[0]!;
    assert.equal(sent.packet.type, PacketType.ScreenShare);

    let got: ScreenShareFrameReceived | undefined;
    svc.onFrameReceived = (e) => { got = e; };
    assert.equal(await svc.handle(sent.packet), true);
    assert.ok(got);
    assert.equal(got!.frame.isKeyframe, true);
    assert.equal(got!.frame.sequence, 7);
  });
});

describe("MediaFrameServices — handle rejects", () => {
  // Mirrors Handle_WrongType_ReturnsFalse.
  it("rejects the wrong packet type", async () => {
    const vp = new VoicePttService(new FakeMeshSender("aether:local:01"));
    const ss = new ScreenShareService(new FakeMeshSender("aether:local:01"));

    const wrongForVp = new MeshPacket();
    wrongForVp.type = PacketType.Data;
    wrongForVp.payload = new Uint8Array(40);
    assert.equal(await vp.handle(wrongForVp), false);

    const wrongForSs = new MeshPacket();
    wrongForSs.type = PacketType.Data;
    wrongForSs.payload = new Uint8Array(40);
    assert.equal(await ss.handle(wrongForSs), false);
  });

  // Mirrors Handle_ShortFrame_ReturnsFalse.
  it("rejects a short frame (< 29 bytes)", async () => {
    const vp = new VoicePttService(new FakeMeshSender("aether:local:01"));
    const pkt = new MeshPacket();
    pkt.type = PacketType.VoicePtt;
    pkt.payload = new Uint8Array(10);
    assert.equal(await vp.handle(pkt), false);
  });
});

/** hex string → bytes (empty string → empty). */
function fromHex(h: string): Uint8Array {
  if (!h) return new Uint8Array(0);
  const out = new Uint8Array(h.length / 2);
  for (let i = 0; i < out.length; i++) out[i] = parseInt(h.substr(i * 2, 2), 16);
  return out;
}
