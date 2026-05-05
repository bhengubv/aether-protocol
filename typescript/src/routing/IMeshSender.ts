/**
 * MeshSender — minimal sending abstraction routing/DTN/SOS depend on.
 * Hosts wire this with a thin adapter over their transport so the protocol
 * services don't take a hard dependency on a specific transport implementation.
 *
 * SPDX-License-Identifier: MIT
 */

import { MeshPacket } from "../protocol/MeshPacket.js";
import { PeerInfo } from "../models/index.js";

export interface IMeshSender {
  /** The local node's UHID. Used as packet.sourceUhid on outbound packets. */
  readonly localUhid: string;

  /** Local node's last-known geohash, or undefined if not shared. */
  readonly localGeohash?: string;

  /** Snapshot of currently directly-connected peers. Empty if not implemented. */
  getConnectedPeers(): PeerInfo[];

  /** Forward a packet to a single next-hop peer. Returns true if delivered. */
  send(packet: MeshPacket, nextHopUhid: string): Promise<boolean>;

  /** Broadcast a packet to every connected peer. Returns the fan-out count. */
  broadcast(packet: MeshPacket): Promise<number>;
}
