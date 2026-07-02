/**
 * Default profile service (PacketType.ProfileSync = 23). Shares this node's profile directly with
 * a chosen peer and caches profiles received from peers. Directed (not broadcast) to avoid leaking
 * identity metadata to the whole mesh. Received profiles are cached (keyed by uhid) and surfaced
 * via onProfileUpdated.
 *
 * Mirrors the C# ProfileService.
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";
import { ProfileSyncPayload } from "./models.js";

export class ProfileService {
  private local: ProfileSyncPayload;
  private readonly peerProfiles = new Map<string, ProfileSyncPayload>();

  /** Raised when a peer's profile is received or refreshed. */
  onProfileUpdated?: (profile: ProfileSyncPayload) => void;

  constructor(private readonly sender: IMeshSender) {
    this.local = {
      uhid: sender.localUhid,
      displayName: "",
      avatarRef: "",
      statusMessage: "",
      updatedAtMs: 0,
    };
  }

  /** Set this node's own profile (stamps updatedAtMs to now). */
  setLocalProfile(displayName: string, avatarRef: string, statusMessage: string): void {
    this.local = {
      uhid: this.sender.localUhid,
      displayName: displayName ?? "",
      avatarRef: avatarRef ?? "",
      statusMessage: statusMessage ?? "",
      updatedAtMs: Date.now(),
    };
  }

  /** This node's current local profile. */
  getLocalProfile(): ProfileSyncPayload {
    return this.local;
  }

  /**
   * Send this node's local profile directly to `peerUhid` via the sender's directed send.
   * Best-effort; returns delivery success.
   */
  async publishProfileTo(peerUhid: string): Promise<boolean> {
    if (!peerUhid) throw new Error("peerUhid must not be empty");

    const body = new TextEncoder().encode(serializeProfileSyncPayload(this.local));

    const packet = new MeshPacket();
    packet.type = PacketType.ProfileSync;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = peerUhid;
    packet.ttl = DEFAULT_TTL;
    packet.payload = body;

    return this.sender.send(packet, peerUhid);
  }

  /**
   * Process an incoming PacketType.ProfileSync packet: cache the sender's profile (keyed by its
   * uhid) and raise onProfileUpdated. Returns false for the wrong packet type, a malformed
   * payload, or our own profile echoed back.
   */
  async handle(packet: MeshPacket): Promise<boolean> {
    if (packet.type !== PacketType.ProfileSync) return false;

    let body: ProfileSyncPayload | undefined;
    try {
      body = deserializeProfileSyncPayload(packet.payload);
    } catch {
      return false;
    }
    if (!body || !body.uhid) return false;

    // Ignore our own profile echoed back.
    if (body.uhid === this.sender.localUhid) return false;

    this.peerProfiles.set(body.uhid, body);
    this.onProfileUpdated?.(body);
    return true;
  }

  /** The cached profile for `uhid`, or undefined if none is known. */
  getProfile(uhid: string): ProfileSyncPayload | undefined {
    return this.peerProfiles.get(uhid);
  }

  /** Snapshot of every peer profile this node has cached. */
  getKnownProfiles(): ProfileSyncPayload[] {
    return Array.from(this.peerProfiles.values());
  }
}

/**
 * Canonical ProfileSync payload serialization — MUST be byte-identical across all language ports
 * (fixtures/profiles/vectors.json): snake_case keys, field order uhid, display_name, avatar_ref,
 * status_message, updated_at_ms, no whitespace, updated_at_ms a bare integer, all string fields
 * always present.
 */
export function serializeProfileSyncPayload(p: ProfileSyncPayload): string {
  return JSON.stringify({
    uhid: p.uhid,
    display_name: p.displayName,
    avatar_ref: p.avatarRef,
    status_message: p.statusMessage,
    updated_at_ms: p.updatedAtMs,
  });
}

/** Parse a canonical ProfileSync payload back into camelCase fields. */
export function deserializeProfileSyncPayload(bytes: Uint8Array): ProfileSyncPayload {
  const data = JSON.parse(new TextDecoder().decode(bytes)) as {
    uhid?: string;
    display_name?: string;
    avatar_ref?: string;
    status_message?: string;
    updated_at_ms?: number;
  };
  return {
    uhid: data.uhid ?? "",
    displayName: data.display_name ?? "",
    avatarRef: data.avatar_ref ?? "",
    statusMessage: data.status_message ?? "",
    updatedAtMs: data.updated_at_ms ?? 0,
  };
}
