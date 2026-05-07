/**
 * Watch-together synchronization service.
 *
 * The host drives playback (play, pause, seek, setSpeed) by broadcasting
 * WatchSync packets to all session members. Followers apply RTT compensation:
 *   adjustedPositionMs = position_ms + (Date.now() - sent_at_ms) * playback_speed
 *
 * Reactions are sent with WatchReaction (JSON) and delivered to all members.
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";

// ──────────────────────────────────────────────────────────────────
// Wire types (snake_case)
// ──────────────────────────────────────────────────────────────────

type WatchSyncKind = "play" | "pause" | "seek" | "speed" | "join" | "leave" | "end";

interface WatchSyncPayload {
  session_id: string;
  kind: WatchSyncKind;
  position_ms?: number;
  playback_speed?: number;
  sent_at_ms: number;
  content_id?: string;
}

interface WatchReactionPayload {
  session_id: string;
  reaction: string;
}

// ──────────────────────────────────────────────────────────────────
// Public types
// ──────────────────────────────────────────────────────────────────

export interface WatchSession {
  sessionId: string;
  hostUhid: string;
  contentId: string;
  members: Set<string>;
}

export interface SyncAppliedEvent {
  sessionId: string;
  kind: WatchSyncKind;
  positionMs: number;
  playbackSpeed: number;
  fromUhid: string;
}

export interface ReactionEvent {
  sessionId: string;
  fromUhid: string;
  reaction: string;
}

// ──────────────────────────────────────────────────────────────────
// Service
// ──────────────────────────────────────────────────────────────────

export class WatchTogetherService {
  private readonly sessions = new Map<string, WatchSession>();

  // Event callbacks
  onSessionInvited?: (session: WatchSession) => void;
  onSyncApplied?: (event: SyncAppliedEvent) => void;
  onReactionReceived?: (event: ReactionEvent) => void;
  onSessionEnded?: (sessionId: string) => void;

  constructor(private readonly sender: IMeshSender) {}

  // ──────────────── host session management ────────────────────────

  async inviteToSession(
    sessionId: string,
    contentId: string,
    memberUhids: string[],
  ): Promise<void> {
    if (!sessionId) throw new Error("sessionId must not be empty");
    const local = this.sender.localUhid;
    const members = new Set<string>([local, ...memberUhids]);

    const session: WatchSession = {
      sessionId,
      hostUhid: local,
      contentId,
      members,
    };
    this.sessions.set(sessionId, session);

    for (const uhid of memberUhids) {
      await this.sendSync(
        {
          session_id: sessionId,
          kind: "join",
          sent_at_ms: Date.now(),
          content_id: contentId,
        },
        uhid,
      );
    }
  }

  // ──────────────── host playback control ─────────────────────────

  async play(sessionId: string, positionMs: number): Promise<void> {
    await this.broadcastSync(sessionId, {
      session_id: sessionId,
      kind: "play",
      position_ms: positionMs,
      playback_speed: 1,
      sent_at_ms: Date.now(),
    });
  }

  async pause(sessionId: string, positionMs: number): Promise<void> {
    await this.broadcastSync(sessionId, {
      session_id: sessionId,
      kind: "pause",
      position_ms: positionMs,
      playback_speed: 0,
      sent_at_ms: Date.now(),
    });
  }

  async seek(sessionId: string, positionMs: number): Promise<void> {
    await this.broadcastSync(sessionId, {
      session_id: sessionId,
      kind: "seek",
      position_ms: positionMs,
      sent_at_ms: Date.now(),
    });
  }

  async setSpeed(sessionId: string, speed: number): Promise<void> {
    await this.broadcastSync(sessionId, {
      session_id: sessionId,
      kind: "speed",
      playback_speed: speed,
      sent_at_ms: Date.now(),
    });
  }

  // ──────────────── member actions ─────────────────────────────────

  async sendReaction(sessionId: string, reaction: string): Promise<void> {
    const session = this.sessions.get(sessionId);
    if (!session) return;

    const body = new TextEncoder().encode(
      JSON.stringify({ session_id: sessionId, reaction } satisfies WatchReactionPayload),
    );

    const local = this.sender.localUhid;
    for (const uhid of session.members) {
      if (uhid === local) continue;
      const packet = new MeshPacket();
      packet.type = PacketType.WatchReaction;
      packet.sourceUhid = local;
      packet.destinationUhid = uhid;
      packet.ttl = DEFAULT_TTL;
      packet.priority = 0;
      packet.payload = body;
      await this.sender.send(packet, uhid);
    }
  }

  // ──────────────── inbound packet routing ────────────────────────

  async onPacket(packet: MeshPacket): Promise<void> {
    switch (packet.type) {
      case PacketType.WatchSync:
        await this.handleSync(packet);
        break;
      case PacketType.WatchReaction:
        this.handleReaction(packet);
        break;
      default:
        break;
    }
  }

  // ──────────────── inspection ─────────────────────────────────────

  getSession(sessionId: string): WatchSession | undefined {
    return this.sessions.get(sessionId);
  }

  getActiveSessions(): WatchSession[] {
    return Array.from(this.sessions.values());
  }

  // ──────────────── private helpers ─────────────────────────────────

  private async handleSync(packet: MeshPacket): Promise<void> {
    let msg: WatchSyncPayload;
    try {
      msg = JSON.parse(new TextDecoder().decode(packet.payload)) as WatchSyncPayload;
    } catch {
      return;
    }
    if (!msg.session_id || !msg.kind) return;

    const { session_id: sessionId, kind } = msg;

    switch (kind) {
      case "join": {
        if (this.sessions.has(sessionId)) {
          // existing session — add sender as member
          this.sessions.get(sessionId)!.members.add(packet.sourceUhid);
          break;
        }
        const session: WatchSession = {
          sessionId,
          hostUhid: packet.sourceUhid,
          contentId: msg.content_id ?? "",
          members: new Set<string>([packet.sourceUhid, this.sender.localUhid]),
        };
        this.sessions.set(sessionId, session);
        this.onSessionInvited?.(session);
        break;
      }

      case "leave": {
        const session = this.sessions.get(sessionId);
        if (session) session.members.delete(packet.sourceUhid);
        break;
      }

      case "end": {
        this.sessions.delete(sessionId);
        this.onSessionEnded?.(sessionId);
        break;
      }

      case "play":
      case "pause":
      case "seek":
      case "speed": {
        const rawPositionMs = msg.position_ms ?? 0;
        const playbackSpeed = msg.playback_speed ?? 1;
        const sentAtMs = msg.sent_at_ms;

        // RTT compensation: advance position by transit time × speed
        const transitMs = Date.now() - sentAtMs;
        const compensatedPositionMs =
          kind === "pause"
            ? rawPositionMs
            : Math.max(0, rawPositionMs + transitMs * playbackSpeed);

        this.onSyncApplied?.({
          sessionId,
          kind,
          positionMs: compensatedPositionMs,
          playbackSpeed,
          fromUhid: packet.sourceUhid,
        });
        break;
      }

      default:
        break;
    }
  }

  private handleReaction(packet: MeshPacket): void {
    let msg: WatchReactionPayload;
    try {
      msg = JSON.parse(new TextDecoder().decode(packet.payload)) as WatchReactionPayload;
    } catch {
      return;
    }
    if (!msg.session_id || !msg.reaction) return;

    this.onReactionReceived?.({
      sessionId: msg.session_id,
      fromUhid: packet.sourceUhid,
      reaction: msg.reaction,
    });
  }

  private async broadcastSync(sessionId: string, payload: WatchSyncPayload): Promise<void> {
    const session = this.sessions.get(sessionId);
    if (!session) return;
    const local = this.sender.localUhid;
    for (const uhid of session.members) {
      if (uhid === local) continue;
      await this.sendSync(payload, uhid);
    }
  }

  private async sendSync(payload: WatchSyncPayload, toUhid: string): Promise<void> {
    const body = new TextEncoder().encode(JSON.stringify(payload));
    const packet = new MeshPacket();
    packet.type = PacketType.WatchSync;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = toUhid;
    packet.ttl = DEFAULT_TTL;
    packet.priority = 0;
    packet.payload = body;
    await this.sender.send(packet, toUhid);
  }
}
