// SPDX-License-Identifier: MIT

/**
 * Binary WIRE bindings for the three ABMF PacketTypes — the TypeScript port of
 *   src/AetherNet.Core/Bandwidth/BandwidthWireService.cs
 *
 * All multi-byte integers are LITTLE-ENDIAN (DataView with littleEndian = true),
 * matching the packet-serializer convention. NO version byte — the layouts are the
 * ones documented on the PacketType members. Byte-identity gate:
 * fixtures/bandwidth/vectors.json (lowercase hex).
 *
 *   Probe(53)  : sequence u32 | sender_send_us i64                                              (12 B)
 *   Ack(54)    : sequence u32 | sender_send_us i64 | receiver_receive_us i64 | receiver_send_us i64 | probe_bytes i32 (32 B)
 *   Gossip(55) : btlbw_bps i64 | rtprop_us i32 | confidence u8                                   (13 B)
 *
 * senderReceiveUs is NOT on the wire — the prober fills it locally on receipt (0 on
 * deserialize). peerUhid/transportName/measuredAt of a gossip come from the enclosing
 * packet + local clock, not the wire body.
 */

import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";
import { DEFAULT_TTL } from "../constants.js";
import {
  BandwidthConfidence,
  makeBandwidthProbeAck,
  type BandwidthProbeAck,
  type BandwidthGossipPayload,
} from "./models.js";

// ── Probe wire model ────────────────────────────────────────────────────────────

/** A latency/throughput probe request (PacketType.BandwidthProbe = 53 body). */
export interface BandwidthProbe {
  readonly sequence: number;
  readonly senderSendUs: bigint;
}

/** An inbound probe plus the peer that sent it (so the host can reply with an ack). */
export interface BandwidthProbeReceived {
  readonly probe: BandwidthProbe;
  readonly fromUhid: string;
}

const INT32_MAX = 2_147_483_647n;

/** Clamp a bigint into the signed-int32 range [0, int.MaxValue] (mirrors C# Math.Clamp). */
function clampToInt32(v: bigint): number {
  if (v < 0n) return 0;
  if (v > INT32_MAX) return Number(INT32_MAX);
  return Number(v);
}

// ── Codec ───────────────────────────────────────────────────────────────────────

/**
 * Binary wire codec for the three ABMF packets. Static-only namespace — the byte
 * layout is the cross-language contract; every SDK MUST produce these exact bytes.
 */
export const BandwidthWireCodec = {
  // Probe(53): sequence u32 | sender_send_us i64  = 12 B
  serializeProbe(p: BandwidthProbe): Uint8Array {
    const buf = new Uint8Array(12);
    const dv = new DataView(buf.buffer);
    dv.setUint32(0, p.sequence >>> 0, true);
    dv.setBigInt64(4, BigInt(p.senderSendUs), true);
    return buf;
  },

  deserializeProbe(b: Uint8Array): BandwidthProbe {
    if (b.length < 12) throw new RangeError("BandwidthProbe payload too short");
    const dv = new DataView(b.buffer, b.byteOffset, b.length);
    return {
      sequence: dv.getUint32(0, true),
      senderSendUs: dv.getBigInt64(4, true),
    };
  },

  // Ack(54): sequence u32 | sender_send_us i64 | receiver_receive_us i64 | receiver_send_us i64 | probe_bytes i32 = 32 B
  serializeAck(a: BandwidthProbeAck): Uint8Array {
    const buf = new Uint8Array(32);
    const dv = new DataView(buf.buffer);
    dv.setUint32(0, a.sequence >>> 0, true);
    dv.setBigInt64(4, BigInt(a.senderSendUs), true);
    dv.setBigInt64(12, BigInt(a.receiverReceiveUs), true);
    dv.setBigInt64(20, BigInt(a.receiverSendUs), true);
    dv.setInt32(28, a.probeBytes | 0, true);
    // senderReceiveUs is local-only — deliberately NOT written to the wire.
    return buf;
  },

  deserializeAck(b: Uint8Array): BandwidthProbeAck {
    if (b.length < 32) throw new RangeError("BandwidthProbeAck payload too short");
    const dv = new DataView(b.buffer, b.byteOffset, b.length);
    return makeBandwidthProbeAck({
      sequence: dv.getUint32(0, true),
      senderSendUs: dv.getBigInt64(4, true),
      receiverReceiveUs: dv.getBigInt64(12, true),
      receiverSendUs: dv.getBigInt64(20, true),
      senderReceiveUs: 0n, // filled by the prober on receipt, not carried on the wire
      probeBytes: dv.getInt32(28, true),
    });
  },

  // Gossip(55): btlbw_bps i64 | rtprop_us i32 | confidence u8 = 13 B
  serializeGossip(g: BandwidthGossipPayload): Uint8Array {
    const buf = new Uint8Array(13);
    const dv = new DataView(buf.buffer);
    dv.setBigInt64(0, BigInt(g.btlBwBps), true);
    dv.setInt32(8, clampToInt32(BigInt(g.rtPropUs)), true);
    buf[12] = g.confidence & 0xff;
    // peerUhid/transportName/measuredAt are not on the wire.
    return buf;
  },

  /** Decode a gossip body. peerUhid/transportName default to empty; the service fills peerUhid from the packet. */
  deserializeGossip(b: Uint8Array): BandwidthGossipPayload {
    if (b.length < 13) throw new RangeError("BandwidthGossipPayload payload too short");
    const dv = new DataView(b.buffer, b.byteOffset, b.length);
    return {
      peerUhid: "",
      transportName: "",
      btlBwBps: dv.getBigInt64(0, true),
      rtPropUs: BigInt(dv.getInt32(8, true)),
      confidence: b[12]! as BandwidthConfidence,
      measuredAt: new Date(0),
    };
  },
} as const;

// ── Service ───────────────────────────────────────────────────────────────────

/**
 * Binds the three ABMF PacketTypes to the mesh: send probes (directed) + their acks
 * (directed reply), and broadcast/receive warm-start gossip. Inbound packets surface via
 * the on* callbacks; the host feeds them into the estimator and replies to probes.
 */
export class BandwidthWireService {
  /** Raised when a BandwidthProbe(53) is received (with the peer that sent it). */
  onProbeReceived?: (received: BandwidthProbeReceived) => void;
  /** Raised when a BandwidthAck(54) is received. */
  onAckReceived?: (ack: BandwidthProbeAck) => void;
  /** Raised when a BandwidthGossip(55) is received (peerUhid filled from the packet). */
  onGossipReceived?: (gossip: BandwidthGossipPayload) => void;

  constructor(private readonly sender: IMeshSender) {}

  /** Send a directed PacketType.BandwidthProbe to a peer. */
  async sendProbe(peerUhid: string, probe: BandwidthProbe): Promise<boolean> {
    if (!peerUhid) throw new Error("peerUhid must not be empty");
    return this.sendDirected(peerUhid, PacketType.BandwidthProbe, BandwidthWireCodec.serializeProbe(probe));
  }

  /** Send a directed PacketType.BandwidthAck reply to the prober. */
  async sendAck(peerUhid: string, ack: BandwidthProbeAck): Promise<boolean> {
    if (!peerUhid) throw new Error("peerUhid must not be empty");
    return this.sendDirected(peerUhid, PacketType.BandwidthAck, BandwidthWireCodec.serializeAck(ack));
  }

  private sendDirected(peerUhid: string, type: PacketType, payload: Uint8Array): Promise<boolean> {
    const packet = new MeshPacket();
    packet.type = type;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = peerUhid;
    packet.ttl = DEFAULT_TTL;
    packet.payload = payload;
    return this.sender.send(packet, peerUhid);
  }

  /** Broadcast a PacketType.BandwidthGossip warm-start estimate. Returns peers reached. */
  async broadcastGossip(gossip: BandwidthGossipPayload): Promise<number> {
    const packet = new MeshPacket();
    packet.type = PacketType.BandwidthGossip;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = "*";
    packet.ttl = DEFAULT_TTL;
    packet.payload = BandwidthWireCodec.serializeGossip(gossip);
    return this.sender.broadcast(packet);
  }

  /**
   * Dispatch an inbound bandwidth packet to the matching callback. Returns false on the
   * wrong packet type or a malformed (too-short) body.
   */
  async handle(packet: MeshPacket): Promise<boolean> {
    try {
      switch (packet.type) {
        case PacketType.BandwidthProbe: {
          const probe = BandwidthWireCodec.deserializeProbe(packet.payload);
          this.onProbeReceived?.({ probe, fromUhid: packet.sourceUhid });
          return true;
        }
        case PacketType.BandwidthAck: {
          const ack = BandwidthWireCodec.deserializeAck(packet.payload);
          this.onAckReceived?.(ack);
          return true;
        }
        case PacketType.BandwidthGossip: {
          const gossip = BandwidthWireCodec.deserializeGossip(packet.payload);
          this.onGossipReceived?.({ ...gossip, peerUhid: packet.sourceUhid });
          return true;
        }
        default:
          return false;
      }
    } catch {
      // Malformed body — drop.
      return false;
    }
  }
}
