/**
 * Default presence service (PacketType.PresenceBeacon = 21 / PresenceQuery = 22).
 *
 * Broadcast a beacon (host builds it with the rotating erid + coarse geohash), broadcast a query,
 * and surface inbound beacons/queries via events. Transport only — the ERID rotation + geohash
 * coarsening are the host's concern (this service never touches the stable UHID or precise location).
 *
 * Mirrors the C# PresenceService.
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";
import {
  PresenceBeaconPayload,
  PresenceBeaconReceived,
  PresenceQueryPayload,
  PresenceQueryReceived,
  deserializePresenceBeaconPayload,
  deserializePresenceQueryPayload,
  serializePresenceBeaconPayload,
  serializePresenceQueryPayload,
} from "./models.js";

/**
 * Binds PacketType.PresenceBeacon (21) and PresenceQuery (22) to the mesh. Broadcast a beacon or a
 * query, and surface inbound beacons/queries from peers via onBeaconReceived / onQueryReceived.
 */
export class PresenceService {
  /** Raised when a presence beacon arrives from a peer. */
  onBeaconReceived?: (received: PresenceBeaconReceived) => void;
  /** Raised when a presence query arrives from a peer. */
  onQueryReceived?: (received: PresenceQueryReceived) => void;

  constructor(private readonly sender: IMeshSender) {}

  /**
   * Broadcast a presence beacon (dest "*", default TTL). Returns the number of peers reached
   * directly.
   */
  async broadcastBeacon(beacon: PresenceBeaconPayload): Promise<number> {
    const packet = new MeshPacket();
    packet.type = PacketType.PresenceBeacon;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = "*";
    packet.ttl = DEFAULT_TTL;
    packet.payload = new TextEncoder().encode(serializePresenceBeaconPayload(beacon));

    return this.sender.broadcast(packet);
  }

  /**
   * Broadcast a presence query for the given (coarse, possibly empty) geohash: mint a query id and
   * flood a PresenceQuery. Returns the new query id (lowercase-dashed UUID).
   */
  async query(geohash: string): Promise<string> {
    const queryId = crypto.randomUUID();
    const payload: PresenceQueryPayload = { queryId, geohash: geohash ?? "" };

    const packet = new MeshPacket();
    packet.type = PacketType.PresenceQuery;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = "*";
    packet.ttl = DEFAULT_TTL;
    packet.payload = new TextEncoder().encode(serializePresenceQueryPayload(payload));

    await this.sender.broadcast(packet);
    return queryId;
  }

  /**
   * Process an incoming presence packet (beacon or query): surface it via onBeaconReceived /
   * onQueryReceived. Returns false for the wrong packet type, a malformed payload, or a beacon
   * with an empty erid.
   */
  async handle(packet: MeshPacket): Promise<boolean> {
    switch (packet.type) {
      case PacketType.PresenceBeacon:
        return this.handleBeacon(packet);
      case PacketType.PresenceQuery:
        return this.handleQuery(packet);
      default:
        return false;
    }
  }

  private async handleBeacon(packet: MeshPacket): Promise<boolean> {
    let beacon: PresenceBeaconPayload | undefined;
    try {
      beacon = deserializePresenceBeaconPayload(packet.payload);
    } catch {
      return false;
    }
    if (!beacon || !beacon.erid) return false;

    this.onBeaconReceived?.({ beacon, fromUhid: packet.sourceUhid });
    return true;
  }

  private async handleQuery(packet: MeshPacket): Promise<boolean> {
    let query: PresenceQueryPayload | undefined;
    try {
      query = deserializePresenceQueryPayload(packet.payload);
    } catch {
      return false;
    }
    if (!query) return false;

    this.onQueryReceived?.({ query, fromUhid: packet.sourceUhid });
    return true;
  }
}
