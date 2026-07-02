/**
 * VoicePtt(15) + ScreenShare(32) directed media frames for the Aether mesh.
 *
 * Both frames share the exact 29-byte BINARY header used by the existing
 * VoiceCall(16)/VideoFrame(31) frames, so a node can treat them uniformly:
 *
 *   [0..15]  call_id       — 16 bytes, RFC-4122 BIG-ENDIAN (network order, NOT the
 *                            .NET mixed-endian Guid.ToByteArray() layout)
 *   [16..19] sequence      — u32 LITTLE-ENDIAN
 *   [20..27] timestamp_ms  — i64 LITTLE-ENDIAN
 *   [28]     flag          — u8 (VoicePtt: isSilence; ScreenShare: isKeyframe)
 *   [29..]   payload       — opaque encoded audio/video bytes
 *
 * Byte-identity gate: fixtures/media/vectors.json (expected_hex). Mirrors the C#
 * AetherNet.Media.MediaFrameCodec / VoicePttService / ScreenShareService.
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";

const HEADER_LENGTH = 29;

// ── Frame models ──────────────────────────────────────────────────────────────

/** A push-to-talk audio frame (PacketType.VoicePtt = 15 body). */
export interface VoicePttFrame {
  /** Lowercase-dashed RFC-4122 UUID identifying the call. */
  callId: string;
  sequence: number;
  timestampMs: number;
  isSilence: boolean;
  encodedPayload: Uint8Array;
}

/** A screen-share video frame (PacketType.ScreenShare = 32 body). */
export interface ScreenShareFrame {
  /** Lowercase-dashed RFC-4122 UUID identifying the call. */
  callId: string;
  sequence: number;
  timestampMs: number;
  isKeyframe: boolean;
  encodedPayload: Uint8Array;
}

/** Event: an inbound VoicePtt frame plus the peer that sent it. */
export interface VoicePttFrameReceived {
  frame: VoicePttFrame;
  fromUhid: string;
}

/** Event: an inbound ScreenShare frame plus the peer that sent it. */
export interface ScreenShareFrameReceived {
  frame: ScreenShareFrame;
  fromUhid: string;
}

// ── Binary codec ──────────────────────────────────────────────────────────────

/**
 * Binary codec for the VoicePtt(15) + ScreenShare(32) media frames. The call_id is written
 * big-endian (network order), reusing the same UUID-to-bytes handling as the DTN bundle-id
 * codec. Serializes to exactly the bytes in fixtures/media/vectors.json (expected_hex).
 */
export const MediaFrameCodec = {
  serializeVoicePtt(f: VoicePttFrame): Uint8Array {
    return serialize(f.callId, f.sequence, f.timestampMs, f.isSilence, f.encodedPayload);
  },

  serializeScreenShare(f: ScreenShareFrame): Uint8Array {
    return serialize(f.callId, f.sequence, f.timestampMs, f.isKeyframe, f.encodedPayload);
  },

  deserializeVoicePtt(b: Uint8Array): VoicePttFrame {
    if (b.length < HEADER_LENGTH) throw new Error("VoicePtt frame too short");
    const dv = new DataView(b.buffer, b.byteOffset, b.length);
    return {
      callId: bytesToUuid(b.subarray(0, 16)),
      sequence: dv.getUint32(16, true),
      timestampMs: Number(dv.getBigInt64(20, true)),
      isSilence: b[28] !== 0,
      encodedPayload: b.slice(HEADER_LENGTH),
    };
  },

  deserializeScreenShare(b: Uint8Array): ScreenShareFrame {
    if (b.length < HEADER_LENGTH) throw new Error("ScreenShare frame too short");
    const dv = new DataView(b.buffer, b.byteOffset, b.length);
    return {
      callId: bytesToUuid(b.subarray(0, 16)),
      sequence: dv.getUint32(16, true),
      timestampMs: Number(dv.getBigInt64(20, true)),
      isKeyframe: b[28] !== 0,
      encodedPayload: b.slice(HEADER_LENGTH),
    };
  },
} as const;

function serialize(
  callId: string,
  sequence: number,
  timestampMs: number,
  flag: boolean,
  payload: Uint8Array,
): Uint8Array {
  const body = payload ?? new Uint8Array(0);
  const buf = new Uint8Array(HEADER_LENGTH + body.length);
  const dv = new DataView(buf.buffer);
  buf.set(uuidToBytes(callId), 0); // call_id — 16 bytes, RFC-4122 big-endian
  dv.setUint32(16, sequence >>> 0, true); // sequence — u32 LE
  dv.setBigInt64(20, BigInt(timestampMs), true); // timestamp_ms — i64 LE
  buf[28] = flag ? 1 : 0;
  buf.set(body, HEADER_LENGTH);
  return buf;
}

/** UUID hex → 16 bytes in written (RFC-4122 big-endian) order. Same layout as the DTN bundle id. */
function uuidToBytes(uuidStr: string): Uint8Array {
  const hex = uuidStr.replace(/-/g, "");
  const bytes = new Uint8Array(16);
  for (let i = 0; i < 16; i++) bytes[i] = parseInt(hex.substr(i * 2, 2), 16);
  return bytes;
}

/** 16 bytes → lowercase-dashed RFC-4122 UUID. */
function bytesToUuid(bytes: Uint8Array): string {
  const hex = Array.from(bytes).map((b) => b.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

// ── Services ──────────────────────────────────────────────────────────────────

/**
 * Binds PacketType.VoicePtt (15) to the mesh: directed push-to-talk audio frames + inbound
 * event. Mirrors the C# VoicePttService.
 */
export class VoicePttService {
  /** Raised when a VoicePtt frame is received from a peer. */
  onFrameReceived?: (received: VoicePttFrameReceived) => void;

  constructor(private readonly sender: IMeshSender) {}

  /** Directed-send `frame` to `peerUhid` as a VoicePtt(15) packet. Returns delivery success. */
  sendFrame(peerUhid: string, frame: VoicePttFrame): Promise<boolean> {
    if (!peerUhid) throw new Error("peerUhid must not be empty");

    const packet = new MeshPacket();
    packet.type = PacketType.VoicePtt;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = peerUhid;
    packet.ttl = DEFAULT_TTL;
    packet.payload = MediaFrameCodec.serializeVoicePtt(frame);

    return this.sender.send(packet, peerUhid);
  }

  /**
   * Process an incoming PacketType.VoicePtt packet: decode it and raise onFrameReceived with the
   * peer's UHID taken from the packet source. Returns false for the wrong packet type or a
   * malformed (too-short) frame.
   */
  async handle(packet: MeshPacket): Promise<boolean> {
    if (packet.type !== PacketType.VoicePtt) return false;

    let frame: VoicePttFrame;
    try {
      frame = MediaFrameCodec.deserializeVoicePtt(packet.payload);
    } catch {
      return false;
    }

    this.onFrameReceived?.({ frame, fromUhid: packet.sourceUhid });
    return true;
  }
}

/**
 * Binds PacketType.ScreenShare (32) to the mesh: directed screen-share video frames + inbound
 * event. Mirrors the C# ScreenShareService.
 */
export class ScreenShareService {
  /** Raised when a ScreenShare frame is received from a peer. */
  onFrameReceived?: (received: ScreenShareFrameReceived) => void;

  constructor(private readonly sender: IMeshSender) {}

  /** Directed-send `frame` to `peerUhid` as a ScreenShare(32) packet. Returns delivery success. */
  sendFrame(peerUhid: string, frame: ScreenShareFrame): Promise<boolean> {
    if (!peerUhid) throw new Error("peerUhid must not be empty");

    const packet = new MeshPacket();
    packet.type = PacketType.ScreenShare;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = peerUhid;
    packet.ttl = DEFAULT_TTL;
    packet.payload = MediaFrameCodec.serializeScreenShare(frame);

    return this.sender.send(packet, peerUhid);
  }

  /**
   * Process an incoming PacketType.ScreenShare packet: decode it and raise onFrameReceived with
   * the peer's UHID taken from the packet source. Returns false for the wrong packet type or a
   * malformed (too-short) frame.
   */
  async handle(packet: MeshPacket): Promise<boolean> {
    if (packet.type !== PacketType.ScreenShare) return false;

    let frame: ScreenShareFrame;
    try {
      frame = MediaFrameCodec.deserializeScreenShare(packet.payload);
    } catch {
      return false;
    }

    this.onFrameReceived?.({ frame, fromUhid: packet.sourceUhid });
    return true;
  }
}
