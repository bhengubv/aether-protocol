/**
 * Host-driven group voice call service.
 *
 * Only the host (creator) can invite, kick, or end the call.
 * All members exchange frames via unicast to every other member.
 * When membership changes the host increments keyGeneration and
 * broadcasts a key_rotation signaling message.
 *
 * Signaling uses PacketType.VoiceSignaling (JSON GroupVoiceSignalingMessage).
 * Frames use PacketType.VoiceCall (binary GroupVoiceFrame).
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL, MAX_GROUP_VOICE_MEMBERS } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";
import { uuidToBytes, bytesToUuid } from "./VoiceCallService.js";

// ──────────────────────────────────────────────────────────────────
// Constants
// ──────────────────────────────────────────────────────────────────

const GROUP_VOICE_FRAME_PRIORITY = 64;
const GROUP_VOICE_SIGNALING_PRIORITY = 32;

// ──────────────────────────────────────────────────────────────────
// Wire types (snake_case)
// ──────────────────────────────────────────────────────────────────

type GroupVoiceSignalingKind =
  | "invite"
  | "join"
  | "leave"
  | "kick"
  | "end"
  | "key_rotation";

interface GroupVoiceSignalingMessage {
  kind: GroupVoiceSignalingKind;
  call_id: string;
  from_uhid: string;
  to_uhid: string;
  invited_uhids?: string[];
  kicked_uhid?: string;
  key_generation?: number;
}

// ──────────────────────────────────────────────────────────────────
// Public types
// ──────────────────────────────────────────────────────────────────

export type GroupCallState = "idle" | "active" | "ended";

export interface GroupCallInfo {
  callId: string;
  hostUhid: string;
  members: Set<string>;
  state: GroupCallState;
  keyGeneration: number;
  startedAt: Date;
}

export interface GroupFrameEvent {
  callId: string;
  fromUhid: string;
  sequence: number;
  timestampMs: number;
  isSilence: boolean;
  keyGeneration: number;
  encodedPayload: Uint8Array;
}

// ──────────────────────────────────────────────────────────────────
// Service
// ──────────────────────────────────────────────────────────────────

export class GroupVoiceCallService {
  private readonly calls = new Map<string, GroupCallInfo>();

  // Event callbacks
  onGroupCallInvited?: (info: GroupCallInfo) => void;
  onGroupCallActive?: (info: GroupCallInfo) => void;
  onGroupCallEnded?: (info: GroupCallInfo) => void;
  onMembershipChanged?: (info: GroupCallInfo) => void;
  onGroupFrameReceived?: (event: GroupFrameEvent) => void;

  constructor(private readonly sender: IMeshSender) {}

  // ──────────────── host actions ───────────────────────────────────

  async invite(callId: string, memberUhids: string[]): Promise<void> {
    if (!callId) throw new Error("callId must not be empty");
    if (memberUhids.length === 0) throw new Error("memberUhids must not be empty");

    const local = this.sender.localUhid;
    const members = new Set<string>([local, ...memberUhids]);
    if (members.size > MAX_GROUP_VOICE_MEMBERS) {
      throw new Error(`Group call exceeds max members (${MAX_GROUP_VOICE_MEMBERS})`);
    }

    const info: GroupCallInfo = {
      callId,
      hostUhid: local,
      members,
      state: "active",
      keyGeneration: 0,
      startedAt: new Date(),
    };
    this.calls.set(callId, info);

    for (const uhid of memberUhids) {
      await this.sendSignaling(
        {
          kind: "invite",
          call_id: callId,
          from_uhid: local,
          to_uhid: uhid,
          invited_uhids: memberUhids,
          key_generation: info.keyGeneration,
        },
        uhid,
      );
    }

    this.onGroupCallActive?.(info);
  }

  async kick(callId: string, targetUhid: string): Promise<void> {
    const info = this.requireHost(callId);
    if (!info.members.has(targetUhid)) return;

    info.members.delete(targetUhid);
    await this.sendSignaling(
      {
        kind: "kick",
        call_id: callId,
        from_uhid: this.sender.localUhid,
        to_uhid: targetUhid,
        kicked_uhid: targetUhid,
      },
      targetUhid,
    );

    info.keyGeneration += 1;
    await this.broadcastKeyRotation(info);
    this.onMembershipChanged?.(info);
  }

  async endCall(callId: string): Promise<void> {
    const info = this.requireHost(callId);
    info.state = "ended";
    for (const uhid of info.members) {
      if (uhid === this.sender.localUhid) continue;
      await this.sendSignaling(
        { kind: "end", call_id: callId, from_uhid: this.sender.localUhid, to_uhid: uhid },
        uhid,
      );
    }
    this.onGroupCallEnded?.(info);
  }

  // ──────────────── member actions ─────────────────────────────────

  async join(callId: string): Promise<void> {
    const info = this.calls.get(callId);
    if (!info) return;
    const local = this.sender.localUhid;
    if (!info.members.has(local)) {
      info.members.add(local);
      this.onMembershipChanged?.(info);
    }
    await this.sendSignaling(
      { kind: "join", call_id: callId, from_uhid: local, to_uhid: info.hostUhid },
      info.hostUhid,
    );
  }

  async leave(callId: string): Promise<void> {
    const info = this.calls.get(callId);
    if (!info) return;
    const local = this.sender.localUhid;
    info.members.delete(local);
    info.state = "ended";
    for (const uhid of info.members) {
      await this.sendSignaling(
        { kind: "leave", call_id: callId, from_uhid: local, to_uhid: uhid },
        uhid,
      );
    }
    this.onGroupCallEnded?.(info);
  }

  // ──────────────── audio frame sending ───────────────────────────

  async sendFrame(
    callId: string,
    encodedAudio: Uint8Array,
    isSilence: boolean,
    keyGeneration: number,
  ): Promise<void> {
    const info = this.calls.get(callId);
    if (!info || info.state !== "active") return;

    const local = this.sender.localUhid;
    const payload = encodeGroupVoiceFrame(callId, encodedAudio, isSilence, keyGeneration);

    for (const uhid of info.members) {
      if (uhid === local) continue;
      const packet = new MeshPacket();
      packet.type = PacketType.VoiceCall;
      packet.sourceUhid = local;
      packet.destinationUhid = uhid;
      packet.ttl = DEFAULT_TTL;
      packet.priority = GROUP_VOICE_FRAME_PRIORITY;
      packet.payload = payload;
      await this.sender.send(packet, uhid);
    }
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

  getCallInfo(callId: string): GroupCallInfo | undefined {
    return this.calls.get(callId);
  }

  getActiveCalls(): GroupCallInfo[] {
    return Array.from(this.calls.values()).filter((c) => c.state === "active");
  }

  // ──────────────── private helpers ─────────────────────────────────

  private async handleSignaling(packet: MeshPacket): Promise<void> {
    let msg: GroupVoiceSignalingMessage;
    try {
      msg = JSON.parse(
        new TextDecoder().decode(packet.payload),
      ) as GroupVoiceSignalingMessage;
    } catch {
      return;
    }
    if (!msg.call_id || !msg.kind) return;

    const { kind, call_id: callId, from_uhid: fromUhid } = msg;

    switch (kind) {
      case "invite": {
        if (this.calls.has(callId)) return;
        const allMembers = new Set<string>([fromUhid, ...(msg.invited_uhids ?? [])]);
        const info: GroupCallInfo = {
          callId,
          hostUhid: fromUhid,
          members: allMembers,
          state: "active",
          keyGeneration: msg.key_generation ?? 0,
          startedAt: new Date(),
        };
        this.calls.set(callId, info);
        this.onGroupCallInvited?.(info);
        break;
      }
      case "join": {
        const info = this.calls.get(callId);
        if (!info) break;
        if (!info.members.has(fromUhid)) {
          info.members.add(fromUhid);
          // Host rotates key on new member
          if (info.hostUhid === this.sender.localUhid) {
            info.keyGeneration += 1;
            await this.broadcastKeyRotation(info);
          }
          this.onMembershipChanged?.(info);
        }
        break;
      }
      case "leave":
      case "kick": {
        const info = this.calls.get(callId);
        if (!info) break;
        const removed = kind === "kick" ? (msg.kicked_uhid ?? fromUhid) : fromUhid;
        info.members.delete(removed);
        if (removed === this.sender.localUhid) {
          info.state = "ended";
          this.onGroupCallEnded?.(info);
        } else {
          this.onMembershipChanged?.(info);
        }
        break;
      }
      case "key_rotation": {
        const info = this.calls.get(callId);
        if (!info) break;
        if (msg.key_generation !== undefined) {
          info.keyGeneration = msg.key_generation;
        }
        break;
      }
      case "end": {
        const info = this.calls.get(callId);
        if (!info) break;
        info.state = "ended";
        this.onGroupCallEnded?.(info);
        break;
      }
      default:
        break;
    }
  }

  private handleFrame(packet: MeshPacket): void {
    const frame = decodeGroupVoiceFrame(packet.payload);
    if (!frame) return;
    const info = this.calls.get(frame.callId);
    if (!info || info.state !== "active") return;

    this.onGroupFrameReceived?.({
      callId: frame.callId,
      fromUhid: packet.sourceUhid,
      sequence: frame.sequence,
      timestampMs: frame.timestampMs,
      isSilence: frame.isSilence,
      keyGeneration: frame.keyGeneration,
      encodedPayload: frame.encodedPayload,
    });
  }

  private async sendSignaling(
    msg: GroupVoiceSignalingMessage,
    toUhid: string,
  ): Promise<void> {
    const body = new TextEncoder().encode(JSON.stringify(msg));
    const packet = new MeshPacket();
    packet.type = PacketType.VoiceSignaling;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = toUhid;
    packet.ttl = DEFAULT_TTL;
    packet.priority = GROUP_VOICE_SIGNALING_PRIORITY;
    packet.payload = body;
    await this.sender.send(packet, toUhid);
  }

  private async broadcastKeyRotation(info: GroupCallInfo): Promise<void> {
    const local = this.sender.localUhid;
    for (const uhid of info.members) {
      if (uhid === local) continue;
      await this.sendSignaling(
        {
          kind: "key_rotation",
          call_id: info.callId,
          from_uhid: local,
          to_uhid: uhid,
          key_generation: info.keyGeneration,
        },
        uhid,
      );
    }
  }

  private requireHost(callId: string): GroupCallInfo {
    const info = this.calls.get(callId);
    if (!info) throw new Error(`Unknown call: ${callId}`);
    if (info.hostUhid !== this.sender.localUhid) {
      throw new Error("Only the host can perform this action");
    }
    return info;
  }
}

// ──────────────────────────────────────────────────────────────────
// Binary codec
// ──────────────────────────────────────────────────────────────────

/**
 * GroupVoiceFrame binary payload:
 * [16] CallId (UUID RFC4122 big-endian)
 * [4]  Sequence (uint32 little-endian)
 * [8]  TimestampMs (int64 little-endian)
 * [1]  IsSilence (0 or 1)
 * [4]  KeyGeneration (uint32 little-endian)
 * [N]  EncodedPayload
 */
let groupFrameSeq = 0;

export function encodeGroupVoiceFrame(
  callId: string,
  encodedPayload: Uint8Array,
  isSilence: boolean,
  keyGeneration: number,
): Uint8Array {
  const buf = new Uint8Array(16 + 4 + 8 + 1 + 4 + encodedPayload.length);
  const dv = new DataView(buf.buffer);

  uuidToBytes(callId, buf, 0);
  dv.setUint32(16, groupFrameSeq++ & 0xffffffff, true);
  dv.setBigInt64(20, BigInt(Date.now()), true);
  buf[28] = isSilence ? 1 : 0;
  dv.setUint32(29, keyGeneration >>> 0, true);
  buf.set(encodedPayload, 33);

  return buf;
}

export function decodeGroupVoiceFrame(data: Uint8Array): {
  callId: string;
  sequence: number;
  timestampMs: number;
  isSilence: boolean;
  keyGeneration: number;
  encodedPayload: Uint8Array;
} | null {
  if (data.length < 33) return null;
  const dv = new DataView(data.buffer, data.byteOffset, data.byteLength);

  const callId = bytesToUuid(data, 0);
  const sequence = dv.getUint32(16, true);
  const timestampMs = Number(dv.getBigInt64(20, true));
  const isSilence = data[28] !== 0;
  const keyGeneration = dv.getUint32(29, true);
  const encodedPayload = data.slice(33);

  return { callId, sequence, timestampMs, isSilence, keyGeneration, encodedPayload };
}
