/**
 * Point-to-point video call service.
 *
 * Mirrors VoiceCallService's state machine but uses:
 *   PacketType.VideoSignaling  (JSON VideoSignalingMessage)
 *   PacketType.VideoFrame      (binary VideoFrame)
 *
 * Extra signaling operations:
 *   requestKeyframe(callId)
 *   notifyQualityChange(callId, width, height, fps, bitrateKbps)
 *
 * Binary VideoFrame:
 *   [16] CallId (UUID RFC4122 big-endian)
 *   [4]  Sequence (uint32 little-endian)
 *   [8]  TimestampMs (int64 little-endian)
 *   [1]  IsKeyframe (0 or 1)
 *   [N]  EncodedPayload
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";
import { uuidToBytes, bytesToUuid } from "../voice/VoiceCallService.js";

// ──────────────────────────────────────────────────────────────────
// Constants
// ──────────────────────────────────────────────────────────────────

const VIDEO_CALL_TIMEOUT_SECONDS = 30;
const VIDEO_FRAME_PRIORITY = 64;
const VIDEO_SIGNALING_PRIORITY = 32;

// ──────────────────────────────────────────────────────────────────
// Wire types (snake_case)
// ──────────────────────────────────────────────────────────────────

type VideoSignalingKind =
  | "offer"
  | "answer"
  | "hangup"
  | "keyframe_request"
  | "quality_change";

interface VideoSignalingMessage {
  kind: VideoSignalingKind;
  call_id: string;
  from_uhid: string;
  to_uhid: string;
  proposed_codecs?: string[];
  selected_codec?: string;
  width?: number;
  height?: number;
  fps?: number;
  bitrate_kbps?: number;
  reason?: string;
}

// ──────────────────────────────────────────────────────────────────
// Public types
// ──────────────────────────────────────────────────────────────────

export type VideoCallState = "idle" | "ringing" | "connected" | "ended" | "failed";

export interface VideoCallInfo {
  callId: string;
  remoteUhid: string;
  state: VideoCallState;
  isOutgoing: boolean;
  startedAt: Date;
}

export interface VideoFrameEvent {
  callId: string;
  fromUhid: string;
  sequence: number;
  timestampMs: number;
  isKeyframe: boolean;
  encodedPayload: Uint8Array;
}

export interface VideoQualityParams {
  width: number;
  height: number;
  fps: number;
  bitrateKbps: number;
}

// ──────────────────────────────────────────────────────────────────
// Service
// ──────────────────────────────────────────────────────────────────

export class VideoCallService {
  private readonly calls = new Map<string, VideoCallInfo>();
  private readonly timeouts = new Map<string, ReturnType<typeof setTimeout>>();

  private videoFrameSeq = 0;

  // Event callbacks
  onIncomingCall?: (info: VideoCallInfo) => void;
  onCallConnected?: (info: VideoCallInfo) => void;
  onCallEnded?: (info: VideoCallInfo) => void;
  onFrameReceived?: (event: VideoFrameEvent) => void;
  onKeyframeRequested?: (callId: string, fromUhid: string) => void;
  onQualityChanged?: (callId: string, fromUhid: string, params: VideoQualityParams) => void;

  constructor(private readonly sender: IMeshSender) {}

  // ──────────────── outbound signaling ────────────────────────────

  async sendOffer(toUhid: string, codecs: string[]): Promise<string> {
    if (!toUhid) throw new Error("toUhid must not be empty");

    const callId = crypto.randomUUID();
    const info: VideoCallInfo = {
      callId,
      remoteUhid: toUhid,
      state: "ringing",
      isOutgoing: true,
      startedAt: new Date(),
    };
    this.calls.set(callId, info);
    this.armTimeout(callId);

    await this.sendSignaling({
      kind: "offer",
      call_id: callId,
      from_uhid: this.sender.localUhid,
      to_uhid: toUhid,
      proposed_codecs: codecs,
    }, toUhid);

    return callId;
  }

  async acceptCall(callId: string, selectedCodec?: string): Promise<void> {
    const info = this.calls.get(callId);
    if (!info) return;
    this.clearTimeout(callId);
    info.state = "connected";
    this.onCallConnected?.(info);

    await this.sendSignaling({
      kind: "answer",
      call_id: callId,
      from_uhid: this.sender.localUhid,
      to_uhid: info.remoteUhid,
      selected_codec: selectedCodec,
    }, info.remoteUhid);
  }

  async hangUp(callId: string): Promise<void> {
    const info = this.calls.get(callId);
    if (!info) return;
    this.clearTimeout(callId);
    info.state = "ended";
    this.onCallEnded?.(info);

    await this.sendSignaling({
      kind: "hangup",
      call_id: callId,
      from_uhid: this.sender.localUhid,
      to_uhid: info.remoteUhid,
    }, info.remoteUhid);
  }

  async requestKeyframe(callId: string): Promise<void> {
    const info = this.calls.get(callId);
    if (!info || info.state !== "connected") return;

    await this.sendSignaling({
      kind: "keyframe_request",
      call_id: callId,
      from_uhid: this.sender.localUhid,
      to_uhid: info.remoteUhid,
    }, info.remoteUhid);
  }

  async notifyQualityChange(
    callId: string,
    width: number,
    height: number,
    fps: number,
    bitrateKbps: number,
  ): Promise<void> {
    const info = this.calls.get(callId);
    if (!info || info.state !== "connected") return;

    await this.sendSignaling({
      kind: "quality_change",
      call_id: callId,
      from_uhid: this.sender.localUhid,
      to_uhid: info.remoteUhid,
      width,
      height,
      fps,
      bitrate_kbps: bitrateKbps,
    }, info.remoteUhid);
  }

  // ──────────────── video frame sending ───────────────────────────

  async sendFrame(
    callId: string,
    encodedVideo: Uint8Array,
    isKeyframe: boolean,
  ): Promise<boolean> {
    const info = this.calls.get(callId);
    if (!info || info.state !== "connected") return false;

    const payload = encodeVideoFrame(callId, encodedVideo, isKeyframe, this.videoFrameSeq++);

    const packet = new MeshPacket();
    packet.type = PacketType.VideoFrame;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = info.remoteUhid;
    packet.ttl = DEFAULT_TTL;
    packet.priority = VIDEO_FRAME_PRIORITY;
    packet.payload = payload;

    return this.sender.send(packet, info.remoteUhid);
  }

  // ──────────────── inbound packet handling ───────────────────────

  async onPacket(packet: MeshPacket): Promise<void> {
    switch (packet.type) {
      case PacketType.VideoSignaling:
        await this.handleSignaling(packet);
        break;
      case PacketType.VideoFrame:
        this.handleFrame(packet);
        break;
      default:
        break;
    }
  }

  // ──────────────── inspection ─────────────────────────────────────

  getCallInfo(callId: string): VideoCallInfo | undefined {
    return this.calls.get(callId);
  }

  getActiveCalls(): VideoCallInfo[] {
    return Array.from(this.calls.values()).filter(
      (c) => c.state === "ringing" || c.state === "connected",
    );
  }

  // ──────────────── private helpers ─────────────────────────────────

  private async handleSignaling(packet: MeshPacket): Promise<void> {
    let msg: VideoSignalingMessage;
    try {
      msg = JSON.parse(
        new TextDecoder().decode(packet.payload),
      ) as VideoSignalingMessage;
    } catch {
      return;
    }
    if (!msg.call_id || !msg.kind) return;

    const { kind, call_id: callId, from_uhid: fromUhid } = msg;

    switch (kind) {
      case "offer": {
        if (this.calls.has(callId)) return;
        const info: VideoCallInfo = {
          callId,
          remoteUhid: fromUhid,
          state: "ringing",
          isOutgoing: false,
          startedAt: new Date(),
        };
        this.calls.set(callId, info);
        this.armTimeout(callId);
        this.onIncomingCall?.(info);
        break;
      }
      case "answer": {
        const info = this.calls.get(callId);
        if (!info || info.state !== "ringing") break;
        this.clearTimeout(callId);
        info.state = "connected";
        this.onCallConnected?.(info);
        break;
      }
      case "hangup": {
        const info = this.calls.get(callId);
        if (!info) break;
        this.clearTimeout(callId);
        info.state = "ended";
        this.onCallEnded?.(info);
        break;
      }
      case "keyframe_request": {
        const info = this.calls.get(callId);
        if (!info || info.state !== "connected") break;
        this.onKeyframeRequested?.(callId, fromUhid);
        break;
      }
      case "quality_change": {
        const info = this.calls.get(callId);
        if (!info || info.state !== "connected") break;
        this.onQualityChanged?.(callId, fromUhid, {
          width: msg.width ?? 0,
          height: msg.height ?? 0,
          fps: msg.fps ?? 0,
          bitrateKbps: msg.bitrate_kbps ?? 0,
        });
        break;
      }
      default:
        break;
    }
  }

  private handleFrame(packet: MeshPacket): void {
    const frame = decodeVideoFrame(packet.payload);
    if (!frame) return;
    const info = this.calls.get(frame.callId);
    if (!info || info.state !== "connected") return;

    this.onFrameReceived?.({
      callId: frame.callId,
      fromUhid: packet.sourceUhid,
      sequence: frame.sequence,
      timestampMs: frame.timestampMs,
      isKeyframe: frame.isKeyframe,
      encodedPayload: frame.encodedPayload,
    });
  }

  private async sendSignaling(msg: VideoSignalingMessage, toUhid: string): Promise<void> {
    const body = new TextEncoder().encode(JSON.stringify(msg));
    const packet = new MeshPacket();
    packet.type = PacketType.VideoSignaling;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = toUhid;
    packet.ttl = DEFAULT_TTL;
    packet.priority = VIDEO_SIGNALING_PRIORITY;
    packet.payload = body;
    await this.sender.send(packet, toUhid);
  }

  private armTimeout(callId: string): void {
    this.clearTimeout(callId);
    const handle = setTimeout(async () => {
      const info = this.calls.get(callId);
      if (!info || info.state !== "ringing") return;
      info.state = "failed";
      this.onCallEnded?.(info);
      await this.sendSignaling({
        kind: "hangup",
        call_id: callId,
        from_uhid: this.sender.localUhid,
        to_uhid: info.remoteUhid,
        reason: "timeout",
      }, info.remoteUhid);
    }, VIDEO_CALL_TIMEOUT_SECONDS * 1000);
    this.timeouts.set(callId, handle);
  }

  private clearTimeout(callId: string): void {
    const handle = this.timeouts.get(callId);
    if (handle !== undefined) {
      clearTimeout(handle);
      this.timeouts.delete(callId);
    }
  }
}

// ──────────────────────────────────────────────────────────────────
// Binary codec
// ──────────────────────────────────────────────────────────────────

/**
 * VideoFrame binary payload:
 * [16] CallId (UUID RFC4122 big-endian)
 * [4]  Sequence (uint32 little-endian)
 * [8]  TimestampMs (int64 little-endian)
 * [1]  IsKeyframe (0 or 1)
 * [N]  EncodedPayload
 */
export function encodeVideoFrame(
  callId: string,
  encodedPayload: Uint8Array,
  isKeyframe: boolean,
  sequence: number,
): Uint8Array {
  const buf = new Uint8Array(16 + 4 + 8 + 1 + encodedPayload.length);
  const dv = new DataView(buf.buffer);

  uuidToBytes(callId, buf, 0);
  dv.setUint32(16, sequence >>> 0, true);
  dv.setBigInt64(20, BigInt(Date.now()), true);
  buf[28] = isKeyframe ? 1 : 0;
  buf.set(encodedPayload, 29);

  return buf;
}

export function decodeVideoFrame(data: Uint8Array): {
  callId: string;
  sequence: number;
  timestampMs: number;
  isKeyframe: boolean;
  encodedPayload: Uint8Array;
} | null {
  if (data.length < 29) return null;
  const dv = new DataView(data.buffer, data.byteOffset, data.byteLength);

  const callId = bytesToUuid(data, 0);
  const sequence = dv.getUint32(16, true);
  const timestampMs = Number(dv.getBigInt64(20, true));
  const isKeyframe = data[28] !== 0;
  const encodedPayload = data.slice(29);

  return { callId, sequence, timestampMs, isKeyframe, encodedPayload };
}
