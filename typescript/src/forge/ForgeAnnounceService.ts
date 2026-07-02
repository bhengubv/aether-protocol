/**
 * WIRE binding for the aether-forge package cache (PacketType.ForgeAnnounce = 41). A thin
 * transport service: a node broadcasts this when it caches a new package artifact so mesh peers
 * with the aethernet.forge/v1 capability learn where the artifact lives; inbound announcements
 * surface via onAnnounceReceived (the host records them in its IForgeService).
 *
 * Mirrors the C# AetherNet.Forge.ForgeAnnounceService.
 *
 * Wire payload (byte-identity gate — fixtures/forge/vectors.json): UTF-8 JSON, snake_case keys,
 * field order package_id, content_hash, size_bytes, announced_at_ms — no whitespace, size_bytes +
 * announced_at_ms bare integers.
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";

/**
 * JSON payload for a ForgeAnnounce packet. Announces that `packageId` (content-addressed by
 * `contentHash`, `sizeBytes` large) was cached at `announcedAtMs`. Also the event arg surfaced on
 * receipt.
 */
export interface ForgeAnnouncePayload {
  /** Package identifier in "ecosystem:name@version" format (e.g. "npm:react@18.2.0"). */
  packageId: string;
  /** Aether content hash of the cached artifact. */
  contentHash: string;
  /** Size of the cached artifact in bytes. */
  sizeBytes: number;
  /** Unix timestamp in milliseconds when the artifact was announced. */
  announcedAtMs: number;
}

/**
 * Canonical ForgeAnnounce payload serialization — MUST be byte-identical across all language ports
 * (fixtures/forge/vectors.json): snake_case keys, field order package_id, content_hash, size_bytes,
 * announced_at_ms, no whitespace, size_bytes + announced_at_ms bare integers.
 */
export function serializeForgeAnnouncePayload(p: ForgeAnnouncePayload): string {
  return JSON.stringify({
    package_id: p.packageId,
    content_hash: p.contentHash,
    size_bytes: p.sizeBytes,
    announced_at_ms: p.announcedAtMs,
  });
}

/** Parse a canonical ForgeAnnounce payload back into camelCase fields. */
export function deserializeForgeAnnouncePayload(bytes: Uint8Array): ForgeAnnouncePayload {
  const data = JSON.parse(new TextDecoder().decode(bytes)) as {
    package_id?: string;
    content_hash?: string;
    size_bytes?: number;
    announced_at_ms?: number;
  };
  return {
    packageId: data.package_id ?? "",
    contentHash: data.content_hash ?? "",
    sizeBytes: data.size_bytes ?? 0,
    announcedAtMs: data.announced_at_ms ?? 0,
  };
}

/**
 * Binds PacketType.ForgeAnnounce (41) to the mesh. Transport for the aether-forge package-cache
 * extension: broadcast a freshly-cached artifact announcement, and surface inbound announcements
 * via onAnnounceReceived.
 */
export class ForgeAnnounceService {
  /** Raised when a forge announcement arrives from a peer. */
  onAnnounceReceived?: (announcement: ForgeAnnouncePayload) => void;

  constructor(private readonly sender: IMeshSender) {}

  /**
   * Announce a cached artifact to mesh peers (dest "*", default TTL). Returns the number of peers
   * reached directly.
   */
  async broadcast(
    packageId: string,
    contentHash: string,
    sizeBytes: number,
    announcedAtMs: number,
  ): Promise<number> {
    if (!packageId) throw new Error("packageId must not be empty");

    const body = new TextEncoder().encode(
      serializeForgeAnnouncePayload({
        packageId,
        contentHash: contentHash ?? "",
        sizeBytes,
        announcedAtMs,
      }),
    );

    const packet = new MeshPacket();
    packet.type = PacketType.ForgeAnnounce;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = "*";
    packet.ttl = DEFAULT_TTL;
    packet.payload = body;

    return this.sender.broadcast(packet);
  }

  /**
   * Process an incoming PacketType.ForgeAnnounce packet: surface it via onAnnounceReceived.
   * Returns false for the wrong packet type, a malformed payload, or an empty package id.
   */
  async handle(packet: MeshPacket): Promise<boolean> {
    if (packet.type !== PacketType.ForgeAnnounce) return false;

    let body: ForgeAnnouncePayload | undefined;
    try {
      body = deserializeForgeAnnouncePayload(packet.payload);
    } catch {
      return false;
    }
    if (!body || !body.packageId) return false;

    this.onAnnounceReceived?.(body);
    return true;
  }
}
