/**
 * Default video call-control service (PacketType.VideoCall = 27). Sends directed
 * ring/accept/decline/hangup signals to a peer and surfaces inbound ones via
 * onCallStateChanged. The caller rings a peer (minting a call id); either side then accepts,
 * declines, or hangs up, echoing that call id back.
 *
 * This is the caller-intent (call-control) plane — distinct from the media plane
 * (VideoSignaling SDP/ICE + VideoFrame media) handled by the streaming video service. It
 * mirrors how the voice call-control layer carries VoiceCall.
 *
 * Mirrors the C# VideoCallControlService.
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";
import { VideoCallControlPayload, VideoCallStateChanged } from "./models.js";

export class VideoCallControlService {
  /** Raised when a call-control signal is received from a peer. */
  onCallStateChanged?: (change: VideoCallStateChanged) => void;

  constructor(private readonly sender: IMeshSender) {}

  /**
   * Ring `peerUhid`: mint a call id and directed-send a "ring". Returns the new call id
   * (lowercase-dashed UUID) so the caller can correlate later accept/decline/hangup signals.
   */
  async ring(peerUhid: string): Promise<string> {
    if (!peerUhid) throw new Error("peerUhid must not be empty");
    const callId = crypto.randomUUID();
    await this.sendControl(callId, peerUhid, "ring");
    return callId;
  }

  /** Directed-send an "accept" for `callId` to `peerUhid`. Returns delivery success. */
  accept(callId: string, peerUhid: string): Promise<boolean> {
    return this.sendControl(callId, peerUhid, "accept");
  }

  /** Directed-send a "decline" for `callId` to `peerUhid`. Returns delivery success. */
  decline(callId: string, peerUhid: string): Promise<boolean> {
    return this.sendControl(callId, peerUhid, "decline");
  }

  /** Directed-send a "hangup" for `callId` to `peerUhid`. Returns delivery success. */
  hangup(callId: string, peerUhid: string): Promise<boolean> {
    return this.sendControl(callId, peerUhid, "hangup");
  }

  private async sendControl(callId: string, peerUhid: string, action: string): Promise<boolean> {
    if (!peerUhid) throw new Error("peerUhid must not be empty");

    const payload: VideoCallControlPayload = {
      callId,
      action,
      sentAtMs: Date.now(),
    };

    const packet = new MeshPacket();
    packet.type = PacketType.VideoCall;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = peerUhid;
    packet.ttl = DEFAULT_TTL;
    packet.payload = new TextEncoder().encode(serializeVideoCallControlPayload(payload));

    return this.sender.send(packet, peerUhid);
  }

  /**
   * Process an incoming PacketType.VideoCall packet: parse it and raise onCallStateChanged with
   * the peer's UHID taken from the packet source. Returns false for the wrong packet type or a
   * malformed payload.
   */
  async handle(packet: MeshPacket): Promise<boolean> {
    if (packet.type !== PacketType.VideoCall) return false;

    let body: VideoCallControlPayload | undefined;
    try {
      body = deserializeVideoCallControlPayload(packet.payload);
    } catch {
      return false;
    }
    if (!body || !body.action) return false;

    this.onCallStateChanged?.({
      callId: body.callId,
      action: body.action,
      fromUhid: packet.sourceUhid,
    });
    return true;
  }
}

/**
 * Canonical VideoCall call-control payload serialization — MUST be byte-identical across all
 * language ports (fixtures/videocall/vectors.json): snake_case keys, field order call_id,
 * action, sent_at_ms, no whitespace, lowercase-dashed UUID, sent_at_ms a bare integer, action
 * an ASCII verb.
 */
export function serializeVideoCallControlPayload(p: VideoCallControlPayload): string {
  return JSON.stringify({
    call_id: p.callId,
    action: p.action,
    sent_at_ms: p.sentAtMs,
  });
}

/** Parse a canonical VideoCall call-control payload back into camelCase fields. */
export function deserializeVideoCallControlPayload(bytes: Uint8Array): VideoCallControlPayload {
  const data = JSON.parse(new TextDecoder().decode(bytes)) as {
    call_id?: string;
    action?: string;
    sent_at_ms?: number;
  };
  return {
    callId: data.call_id ?? "",
    action: data.action ?? "",
    sentAtMs: data.sent_at_ms ?? 0,
  };
}
