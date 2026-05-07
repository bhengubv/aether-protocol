/**
 * Point-to-point voice call service.
 *
 * State machine:
 *   Outgoing: Idle → Ringing → Connected → Ended | Failed
 *   Incoming: Ringing → Connected → Ended | Failed
 *
 * Signaling uses PacketType.VoiceSignaling (JSON).
 * Audio frames use PacketType.VoiceCall (binary VoiceFrame).
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";

// ──────────────────────────────────────────────────────────────────
// Constants
// ──────────────────────────────────────────────────────────────────

const VOICE_CALL_TIMEOUT_SECONDS = 30;
const VOICE_FRAME_PRIORITY = 64;
const VOICE_SIGNALING_PRIORITY = 32;

// ──────────────────────────────────────────────────────────────────
// Wire types (snake_case — matches canonical JSON format)
// ──────────────────────────────────────────────────────────────────

type VoiceSignalingKind = "offer" | "answer" | "hangup" | "cancel" | "timeout";

interface VoiceSignalingMessage {
  kind: VoiceSignalingKind;
  call_id: string;
  from_uhid: string;
  to_uhid: string;
  proposed_codecs?: string[];
  selected_codec?: string;
  sample_rate_hz?: number;
  reason?: string;
}

// ──────────────────────────────────────────────────────────────────
// Public types
// ──────────────────────────────────────────────────────────────────

export type VoiceCallState = "idle" | "ringing" | "connected" | "ended" | "failed";

export interface VoiceCallInfo {
  callId: string;
  remoteUhid: string;
  state: VoiceCallState;
  isOutgoing: boolean;
  startedAt: Date;
}

export interface VoiceFrameEvent {
  callId: string;
  fromUhid: string;
  sequence: number;
  timestampMs: number;
  isSilence: boolean;
  encodedPayload: Uint8Array;
}

// ──────────────────────────────────────────────────────────────────
// Service
// ──────────────────────────────────────────────────────────────────

export class VoiceCallService {
  private readonly calls = new Map<string, VoiceCallInfo>();
  private readonly timeouts = new Map<string, ReturnType<typeof setTimeout>>();

  // Event callbacks
  onIncomingCall?: (info: VoiceCallInfo) => void;
  onCallConnected?: (info: VoiceCallInfo) => void;
  onCallEnded?: (info: VoiceCallInfo) => void;
  onFrameReceived?: (event: VoiceFrameEvent) => void;

  constructor(private readonly sender: IMeshSender) {}

  // ──────────────── outbound signaling ────────────────────────────

  async sendOffer(
    toUhid: string,
    codecs: string[],
    sampleRateHz: number,
  ): Promise<string> {
    if (!toUhid) throw new Error("toUhid must not be empty");

    const callId = crypto.randomUUID();
    const info: VoiceCallInfo = {
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
      sample_rate_hz: sampleRateHz,
    }, toUhid);

    return callId;
  }

  async acceptCall(callId: string): Promise<void> {
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
    }, info.remoteUhid);
  }

  async hangUp(callId: string): Promise<void> {
    const info = this.calls.get(callId);
    if (!info) return;
    this.clearTimeout(callId);
    const previousState = info.state;
    info.state = "ended";
    this.onCallEnded?.(info);

    const kind: VoiceSignalingKind = previousState === "ringing" ? "cancel" : "hangup";
    await this.sendSignaling({
      kind,
      call_id: callId,
      from_uhid: this.sender.localUhid,
      to_uhid: info.remoteUhid,
    }, info.remoteUhid);
  }

  // ──────────────── audio frame sending ───────────────────────────

  async sendFrame(
    callId: string,
    encodedAudio: Uint8Array,
    isSilence: boolean,
  ): Promise<boolean> {
    const info = this.calls.get(callId);
    if (!info || info.state !== "connected") return false;

    const payload = encodeVoiceFrame(callId, encodedAudio, isSilence);

    const packet = new MeshPacket();
    packet.type = PacketType.VoiceCall;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = info.remoteUhid;
    packet.ttl = DEFAULT_TTL;
    packet.priority = VOICE_FRAME_PRIORITY;
    packet.payload = payload;

    return this.sender.send(packet, info.remoteUhid);
  }

  // ──────────────── inbound packet handling ───────────────────────

  async onPacket(packet: MeshPacket): Promise<void> {
    switch (packet.type) {
      case PacketType.VoiceSignaling:
        await this.handleSignaling(packet);
        break;
      case PacketType.VoiceCall:
        this.handleFrame(packet);
        break;
      default:
        break;
    }
  }

  // ──────────────── inspection ─────────────────────────────────────

  getCallInfo(callId: string): VoiceCallInfo | undefined {
    return this.calls.get(callId);
  }

  getActiveCalls(): VoiceCallInfo[] {
    return Array.from(this.calls.values()).filter(
      (c) => c.state === "ringing" || c.state === "connected",
    );
  }

  // ──────────────── private helpers ────────────────────────────────

  private async handleSignaling(packet: MeshPacket): Promise<void> {
    let msg: VoiceSignalingMessage;
    try {
      msg = JSON.parse(new TextDecoder().decode(packet.payload)) as VoiceSignalingMessage;
    } catch {
      return;
    }
    if (!msg.call_id || !msg.kind) return;

    const { kind, call_id: callId, from_uhid: fromUhid } = msg;

    switch (kind) {
      case "offer": {
        if (this.calls.has(callId)) return; // duplicate
        const info: VoiceCallInfo = {
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
      case "hangup":
      case "cancel":
      case "timeout": {
        const info = this.calls.get(callId);
        if (!info) break;
        this.clearTimeout(callId);
        info.state = "ended";
        this.onCallEnded?.(info);
        break;
      }
      default:
        break;
    }
  }

  private handleFrame(packet: MeshPacket): void {
    const frame = decodeVoiceFrame(packet.payload);
    if (!frame) return;
    const info = this.calls.get(frame.callId);
    if (!info || info.state !== "connected") return;

    this.onFrameReceived?.({
      callId: frame.callId,
      fromUhid: packet.sourceUhid,
      sequence: frame.sequence,
      timestampMs: frame.timestampMs,
      isSilence: frame.isSilence,
      encodedPayload: frame.encodedPayload,
    });
  }

  private async sendSignaling(msg: VoiceSignalingMessage, toUhid: string): Promise<void> {
    const body = new TextEncoder().encode(JSON.stringify(msg));
    const packet = new MeshPacket();
    packet.type = PacketType.VoiceSignaling;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = toUhid;
    packet.ttl = DEFAULT_TTL;
    packet.priority = VOICE_SIGNALING_PRIORITY;
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
        kind: "timeout",
        call_id: callId,
        from_uhid: this.sender.localUhid,
        to_uhid: info.remoteUhid,
        reason: "no_answer",
      }, info.remoteUhid);
    }, VOICE_CALL_TIMEOUT_SECONDS * 1000);
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
// Binary codec helpers
// ──────────────────────────────────────────────────────────────────

/**
 * Encode VoiceFrame binary payload:
 * [16] CallId (UUID RFC4122 big-endian)
 * [4]  Sequence (uint32 little-endian)  — incrementing counter from timestamp
 * [8]  TimestampMs (int64 little-endian)
 * [1]  IsSilence (0 or 1)
 * [N]  EncodedPayload
 */
let voiceFrameSeq = 0;

export function encodeVoiceFrame(
  callId: string,
  encodedPayload: Uint8Array,
  isSilence: boolean,
): Uint8Array {
  const buf = new Uint8Array(16 + 4 + 8 + 1 + encodedPayload.length);
  const dv = new DataView(buf.buffer);

  uuidToBytes(callId, buf, 0);
  dv.setUint32(16, voiceFrameSeq++ & 0xffffffff, true);
  dv.setBigInt64(20, BigInt(Date.now()), true);
  buf[28] = isSilence ? 1 : 0;
  buf.set(encodedPayload, 29);

  return buf;
}

export function decodeVoiceFrame(data: Uint8Array): {
  callId: string;
  sequence: number;
  timestampMs: number;
  isSilence: boolean;
  encodedPayload: Uint8Array;
} | null {
  if (data.length < 29) return null;
  const dv = new DataView(data.buffer, data.byteOffset, data.byteLength);

  const callId = bytesToUuid(data, 0);
  const sequence = dv.getUint32(16, true);
  const timestampMs = Number(dv.getBigInt64(20, true));
  const isSilence = data[28] !== 0;
  const encodedPayload = data.slice(29);

  return { callId, sequence, timestampMs, isSilence, encodedPayload };
}

// ──────────────────────────────────────────────────────────────────
// UUID helpers shared across this file
// ──────────────────────────────────────────────────────────────────

export function uuidToBytes(uuid: string, buf: Uint8Array, offset: number): void {
  const hex = uuid.replace(/-/g, "");
  for (let i = 0; i < 16; i++) {
    buf[offset + i] = parseInt(hex.substr(i * 2, 2), 16);
  }
}

export function bytesToUuid(buf: Uint8Array, offset: number): string {
  const hex = Array.from(buf.slice(offset, offset + 16))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
