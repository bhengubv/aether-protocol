/**
 * Production RelayLink that carries circuit-relay-v2 frames one hop over the real
 * mesh — mirrors the C# MeshRelayLink and the Go / Python MeshRelayLink.
 *
 * Each frame is wrapped in a {@link MeshPacket} of type
 * {@link PacketType.CircuitRelayControl} and handed to the host's
 * send-to-connected-peer callable; inbound CircuitRelayControl packets are fed back
 * into the engine via {@link MeshRelayLink.handleIncomingPacket}. The two callables
 * are the seam to whatever real transport the host runs (BLE / Wi-Fi Direct / WebRTC /
 * the HTTP relay). It never calls a radio directly and never recurses through itself
 * (the host's one-hop send must exclude the circuit-relay transport).
 *
 * SPDX-License-Identifier: MIT
 */

import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { RelayLink } from "./Transport.js";

/** Host callable that sends a MeshPacket one hop to a directly-connected peer. */
export type SendOneHop = (packet: MeshPacket) => boolean;
/** Reports whether this node has a direct one-hop link to a peer. */
export type CanReachFn = (node: string) => boolean;

export class MeshRelayLink implements RelayLink {
  private handler: ((from: string, frame: Uint8Array) => void) | null = null;

  /**
   * @param localUhid This node's UHID (stamped as the packet source).
   * @param sendOneHop Sends a MeshPacket to a directly-connected peer; true if handed off.
   * @param canReachFn Reports a direct one-hop link to a peer.
   */
  constructor(
    private readonly localUhid: string,
    private readonly sendOneHop: SendOneHop,
    private readonly canReachFn: CanReachFn,
  ) {}

  sendFrame(node: string, frame: Uint8Array): boolean {
    const pkt = MeshPacket.create(PacketType.CircuitRelayControl, this.localUhid);
    pkt.destinationUhid = node;
    pkt.payload = frame;
    pkt.ttl = 1; // relay frames travel exactly one hop; end-to-end routing is the engine's job
    return this.sendOneHop(pkt);
  }

  canReach(node: string): boolean {
    return this.canReachFn(node);
  }

  onFrame(handler: (from: string, frame: Uint8Array) => void): void {
    this.handler = handler;
  }

  /**
   * Feed an inbound CircuitRelayControl packet from the host's receive path into the
   * relay engine (non-relay packet types are ignored). The host must call this for
   * every received {@link PacketType.CircuitRelayControl} packet.
   */
  handleIncomingPacket(packet: MeshPacket): void {
    if (packet.type !== PacketType.CircuitRelayControl) return;
    this.handler?.(packet.sourceUhid, packet.payload);
  }
}
