/*
 * Core MeshPacket model — the envelope that carries a TipPacket(24) or a
 * PoVTokenExchange(43) payload across the mesh. ArkTS (HarmonyOS) port of
 * typescript/src/protocol/MeshPacket.ts; wire-compatible with the C# reference.
 *
 * Only the fields and behaviour exercised by the tipping / PoV exchange paths are
 * modelled here (the full binary PacketSerializer lives elsewhere in the suite).
 * The `id` field is a convenience identifier and is NOT part of any canonical
 * byte contract, so it is generated locally without pulling in a UUID dependency.
 *
 * SPDX-License-Identifier: MIT
 */

import { PacketType, packetTypeToString } from './PacketType';

/** ProtocolConstants.DefaultTtl. */
const DEFAULT_TTL: number = 7;
/** Current signed wire version. */
const SIGNED_PROTOCOL_VERSION: number = 2;

/**
 * A mutable mesh packet. Construct one, populate `type` / `sourceUhid` /
 * `destinationUhid` / `payload`, then hand it to a packet signer before sending.
 */
export class MeshPacket {
  /** Convenience identifier (RFC-4122-shaped). Not part of any byte contract. */
  id: string;
  /** Discriminator selecting how the payload is interpreted. */
  type: PacketType;
  /** UHID of the node that originated this packet. */
  sourceUhid: string;
  /** UHID of the node this packet is addressed to. */
  destinationUhid: string;
  /** Remaining hop budget; the packet may be forwarded while this is > 0. */
  ttl: number;
  /** Relative scheduling priority (0 = normal). */
  priority: number;
  /** Opaque application payload (UTF-8 JSON for tip / PoV packets). */
  payload: Uint8Array;
  /** Envelope signature populated by the packet signer. */
  signature: Uint8Array;
  /** Replay-dedup nonce populated by the packet signer. */
  packetNonce: Uint8Array;
  /** Creation time in Unix milliseconds (i64 via bigint). */
  timestampMs: bigint;
  /** Wire protocol version. */
  protocolVersion: number;

  constructor() {
    this.id = MeshPacket.newId();
    this.type = PacketType.Data;
    this.sourceUhid = '';
    this.destinationUhid = '';
    this.ttl = DEFAULT_TTL;
    this.priority = 0;
    this.payload = new Uint8Array(0);
    this.signature = new Uint8Array(0);
    this.packetNonce = new Uint8Array(0);
    this.timestampMs = BigInt(Date.now());
    this.protocolVersion = SIGNED_PROTOCOL_VERSION;
  }

  /** True while the packet may still be forwarded (TTL not yet exhausted). */
  get canForward(): boolean {
    return this.ttl > 0;
  }

  /** True when the packet's age exceeds `maxAgeSeconds`. */
  isExpired(maxAgeSeconds: number): boolean {
    const nowMs: bigint = BigInt(Date.now());
    const ageMs: bigint = nowMs - this.timestampMs;
    return ageMs > BigInt(maxAgeSeconds) * 1000n;
  }

  toDisplayString(): string {
    return `[${packetTypeToString(this.type)}] ${this.id} src=${this.sourceUhid} dst=${this.destinationUhid} ttl=${this.ttl} pri=${this.priority} ver=${this.protocolVersion}`;
  }

  /** Factory: a new packet of `type` originating from `sourceUhid`. */
  static create(type: PacketType, sourceUhid: string): MeshPacket {
    const packet = new MeshPacket();
    packet.type = type;
    packet.sourceUhid = sourceUhid;
    return packet;
  }

  /**
   * Generates an RFC-4122 version-4-shaped identifier. This is a convenience id
   * only — it is never signed and never part of a canonical byte layout, so a
   * lightweight generator is sufficient and avoids an external dependency.
   */
  private static newId(): string {
    const hex: string[] = new Array<string>(36);
    for (let i = 0; i < 36; i++) {
      if (i === 8 || i === 13 || i === 18 || i === 23) {
        hex[i] = '-';
      } else if (i === 14) {
        hex[i] = '4';
      } else {
        const r: number = Math.floor(Math.random() * 16);
        const v: number = i === 19 ? (r & 0x3) | 0x8 : r;
        hex[i] = v.toString(16);
      }
    }
    return hex.join('');
  }
}
