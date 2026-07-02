/**
 * WIRE binding for the aether-space geo-pinned noticeboard (PacketType.SpaceBreadcrumb = 40).
 * A thin transport service: broadcast a locally-dropped breadcrumb, and surface inbound
 * breadcrumbs from peers via onBreadcrumbReceived (the host pins them into its ISpaceService).
 *
 * Mirrors the C# AetherNet.Space.SpaceBreadcrumbService.
 *
 * Wire payload (byte-identity gate — fixtures/space/vectors.json): UTF-8 JSON, snake_case keys,
 * field order content_hash, geo_hash, anchor_uhid, created_at_ms, ttl_hours, type, signature.
 * created_at_ms is the creation time as a bare Unix-ms integer (not ISO-8601), ttl_hours + type
 * (the BreadcrumbType enum value) are bare integers, and signature is STANDARD base64 (empty
 * string when unsigned). No whitespace.
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";
import { BreadcrumbType, type SpaceBreadcrumb } from "./SpaceService.js";

/**
 * Projection of a SpaceBreadcrumb onto its canonical wire shape. Carries created_at_ms as a
 * Unix-ms integer, type as the BreadcrumbType enum value, and the Ed25519 signature as STANDARD
 * base64.
 */
export interface SpaceBreadcrumbPayload {
  contentHash: string;
  geoHash: string;
  anchorUhid: string;
  createdAtMs: number;
  ttlHours: number;
  type: BreadcrumbType;
  /** STANDARD base64 of the Ed25519 signature; "" when unsigned. */
  signatureBase64: string;
}

/** Build the wire payload from a domain breadcrumb. */
export function breadcrumbToPayload(b: SpaceBreadcrumb): SpaceBreadcrumbPayload {
  return {
    contentHash: b.contentHash,
    geoHash: b.geoHash,
    anchorUhid: b.anchorUhid,
    createdAtMs: b.createdAtUtc.getTime(),
    ttlHours: b.ttlHours,
    type: b.type,
    signatureBase64: Buffer.from(b.signature).toString("base64"),
  };
}

/** Reconstruct a domain breadcrumb from a wire payload. */
export function payloadToBreadcrumb(p: SpaceBreadcrumbPayload): SpaceBreadcrumb {
  return {
    contentHash: p.contentHash,
    geoHash: p.geoHash,
    anchorUhid: p.anchorUhid,
    createdAtUtc: new Date(p.createdAtMs),
    ttlHours: p.ttlHours,
    type: p.type,
    signature: new Uint8Array(Buffer.from(p.signatureBase64, "base64")),
  };
}

/**
 * Canonical SpaceBreadcrumb payload serialization — MUST be byte-identical across all language
 * ports (fixtures/space/vectors.json): snake_case keys, field order content_hash, geo_hash,
 * anchor_uhid, created_at_ms, ttl_hours, type, signature, no whitespace, created_at_ms + ttl_hours
 * + type bare integers, signature STANDARD base64 ("" when unsigned).
 */
export function serializeSpaceBreadcrumbPayload(p: SpaceBreadcrumbPayload): string {
  return JSON.stringify({
    content_hash: p.contentHash,
    geo_hash: p.geoHash,
    anchor_uhid: p.anchorUhid,
    created_at_ms: p.createdAtMs,
    ttl_hours: p.ttlHours,
    type: p.type,
    signature: p.signatureBase64,
  });
}

/** Parse a canonical SpaceBreadcrumb payload back into camelCase fields. */
export function deserializeSpaceBreadcrumbPayload(bytes: Uint8Array): SpaceBreadcrumbPayload {
  const data = JSON.parse(new TextDecoder().decode(bytes)) as {
    content_hash?: string;
    geo_hash?: string;
    anchor_uhid?: string;
    created_at_ms?: number;
    ttl_hours?: number;
    type?: number;
    signature?: string;
  };
  return {
    contentHash: data.content_hash ?? "",
    geoHash: data.geo_hash ?? "",
    anchorUhid: data.anchor_uhid ?? "",
    createdAtMs: data.created_at_ms ?? 0,
    ttlHours: data.ttl_hours ?? 0,
    type: (data.type ?? BreadcrumbType.Notice) as BreadcrumbType,
    signatureBase64: data.signature ?? "",
  };
}

/**
 * Binds PacketType.SpaceBreadcrumb (40) to the mesh. Transport for the aether-space geo-pinned
 * noticeboard extension: broadcast a locally-dropped breadcrumb, and surface inbound breadcrumbs
 * from peers via onBreadcrumbReceived.
 */
export class SpaceBreadcrumbService {
  /** Raised when a breadcrumb arrives from a peer. */
  onBreadcrumbReceived?: (breadcrumb: SpaceBreadcrumb) => void;

  constructor(private readonly sender: IMeshSender) {}

  /**
   * Flood a breadcrumb to mesh peers (dest "*", default TTL). Returns the number of peers reached
   * directly.
   */
  async broadcast(breadcrumb: SpaceBreadcrumb): Promise<number> {
    const body = new TextEncoder().encode(
      serializeSpaceBreadcrumbPayload(breadcrumbToPayload(breadcrumb)),
    );

    const packet = new MeshPacket();
    packet.type = PacketType.SpaceBreadcrumb;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = "*";
    packet.ttl = DEFAULT_TTL;
    packet.payload = body;

    return this.sender.broadcast(packet);
  }

  /**
   * Process an incoming PacketType.SpaceBreadcrumb packet: surface it via onBreadcrumbReceived.
   * Returns false for the wrong packet type, a malformed payload, or an empty content hash.
   */
  async handle(packet: MeshPacket): Promise<boolean> {
    if (packet.type !== PacketType.SpaceBreadcrumb) return false;

    let body: SpaceBreadcrumbPayload | undefined;
    try {
      body = deserializeSpaceBreadcrumbPayload(packet.payload);
    } catch {
      return false;
    }
    if (!body || !body.contentHash) return false;

    this.onBreadcrumbReceived?.(payloadToBreadcrumb(body));
    return true;
  }
}
