/**
 * Default heartbeat service. Broadcasts PacketType.Heartbeat beacons (TTL 1, one hop)
 * and tracks the liveness of peers from the heartbeats they broadcast. Unauthenticated by
 * design — like SOS, a heartbeat is a low-stakes liveness hint, not a security assertion.
 *
 * A node periodically emits a heartbeat to its direct neighbours (TTL 1). Receivers maintain a
 * per-peer PeerLiveness table (keyed by the enclosing packet's sourceUhid) and can query which
 * peers are currently live.
 *
 * SPDX-License-Identifier: MIT
 */

import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";
import { PeerLiveness } from "./models.js";

export class HeartbeatService {
  private sequence = 0;
  private readonly peers = new Map<string, PeerLiveness>();

  /** Raised when a heartbeat is received from a peer (new or refreshed liveness). */
  onPeerSeen?: (liveness: PeerLiveness) => void;

  constructor(private readonly sender: IMeshSender) {}

  /**
   * Broadcast a single heartbeat to all directly connected peers (TTL 1). The sequence number
   * increments on every call. Returns the number of peers the beacon was delivered to.
   */
  async sendHeartbeat(): Promise<number> {
    const seq = ++this.sequence;

    const body = new TextEncoder().encode(
      JSON.stringify({
        sequence: seq,
        sent_at_ms: Date.now(),
      }),
    );

    const packet = new MeshPacket();
    packet.type = PacketType.Heartbeat;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = "*";
    packet.ttl = 1; // heartbeats are single-hop: liveness of DIRECT neighbours only
    packet.payload = body;

    return this.sender.broadcast(packet);
  }

  /**
   * Process an incoming PacketType.Heartbeat packet: refresh the sender's liveness record and
   * fire onPeerSeen. No-op (returns false) for self-originated heartbeats, the wrong packet
   * type, or a malformed payload.
   */
  async handle(packet: MeshPacket): Promise<boolean> {
    if (packet.type !== PacketType.Heartbeat) return false;

    // Ignore our own heartbeat echoed back.
    if (packet.sourceUhid === this.sender.localUhid) return false;

    let data: { sequence?: number; sent_at_ms?: number };
    try {
      data = JSON.parse(new TextDecoder().decode(packet.payload));
    } catch {
      return false;
    }
    if (data === null || typeof data !== "object") return false;

    const liveness: PeerLiveness = {
      uhid: packet.sourceUhid,
      lastSequence: data.sequence ?? 0,
      lastSentAtMs: data.sent_at_ms ?? 0,
      receivedAtMs: Date.now(),
    };
    this.peers.set(packet.sourceUhid, liveness);
    this.onPeerSeen?.(liveness);
    return true;
  }

  /** Snapshot of every peer this node has ever seen a heartbeat from. */
  getKnownPeers(): PeerLiveness[] {
    return Array.from(this.peers.values());
  }

  /** Peers whose most recent heartbeat was received within the last `withinSeconds` seconds. */
  getLivePeers(withinSeconds: number): PeerLiveness[] {
    const cutoff = Date.now() - withinSeconds * 1000;
    return Array.from(this.peers.values()).filter((p) => p.receivedAtMs >= cutoff);
  }
}
