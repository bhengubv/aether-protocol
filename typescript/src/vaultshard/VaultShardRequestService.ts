/**
 * WIRE binding for the aether-vault erasure-coded-storage extension
 * (PacketType.VaultShardRequest = 42). A thin transport service: a node broadcasts a request for a
 * shard it needs to recover a file; inbound requests surface via onShardRequested (the host answers
 * from its IVaultService if it holds the shard).
 *
 * Mirrors the C# AetherNet.Vault.VaultShardRequestService.
 *
 * Wire payload (byte-identity gate — fixtures/vaultshard/vectors.json): UTF-8 JSON, snake_case keys,
 * field order shard_hash, requester_uhid — no whitespace.
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";

/**
 * A peer's request for an erasure-coded shard. Surfaced on the receiving node when a
 * VaultShardRequest arrives. `requesterUhid` is the UHID of the node that needs the shard.
 */
export interface VaultShardRequest {
  /** SHA-256 hex of the requested shard. */
  shardHash: string;
  /** UHID of the node that needs the shard. */
  requesterUhid: string;
}

/** JSON payload for a VaultShardRequest packet (same fields as the surfaced request). */
export type VaultShardRequestPayload = VaultShardRequest;

/**
 * Canonical VaultShardRequest payload serialization — MUST be byte-identical across all language
 * ports (fixtures/vaultshard/vectors.json): snake_case keys, field order shard_hash,
 * requester_uhid, no whitespace.
 */
export function serializeVaultShardRequestPayload(p: VaultShardRequestPayload): string {
  return JSON.stringify({
    shard_hash: p.shardHash,
    requester_uhid: p.requesterUhid,
  });
}

/** Parse a canonical VaultShardRequest payload back into camelCase fields. */
export function deserializeVaultShardRequestPayload(bytes: Uint8Array): VaultShardRequestPayload {
  const data = JSON.parse(new TextDecoder().decode(bytes)) as {
    shard_hash?: string;
    requester_uhid?: string;
  };
  return {
    shardHash: data.shard_hash ?? "",
    requesterUhid: data.requester_uhid ?? "",
  };
}

/**
 * Binds PacketType.VaultShardRequest (42) to the mesh. Transport for the aether-vault erasure-coded
 * storage extension: broadcast a request for a shard, and surface inbound shard requests via
 * onShardRequested.
 */
export class VaultShardRequestService {
  /** Raised when a peer requests a shard. */
  onShardRequested?: (request: VaultShardRequest) => void;

  constructor(private readonly sender: IMeshSender) {}

  /**
   * Broadcast a request for `shardHash` (dest "*", default TTL). The requester is this node's
   * localUhid. Returns the number of peers reached directly.
   */
  async requestShard(shardHash: string): Promise<number> {
    if (!shardHash) throw new Error("shardHash must not be empty");

    const body = new TextEncoder().encode(
      serializeVaultShardRequestPayload({ shardHash, requesterUhid: this.sender.localUhid }),
    );

    const packet = new MeshPacket();
    packet.type = PacketType.VaultShardRequest;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = "*";
    packet.ttl = DEFAULT_TTL;
    packet.payload = body;

    return this.sender.broadcast(packet);
  }

  /**
   * Process an incoming PacketType.VaultShardRequest packet: surface it via onShardRequested.
   * Returns false for the wrong packet type, a malformed payload, or an empty shard hash.
   */
  async handle(packet: MeshPacket): Promise<boolean> {
    if (packet.type !== PacketType.VaultShardRequest) return false;

    let body: VaultShardRequestPayload | undefined;
    try {
      body = deserializeVaultShardRequestPayload(packet.payload);
    } catch {
      return false;
    }
    if (!body || !body.shardHash) return false;

    this.onShardRequested?.(body);
    return true;
  }
}
