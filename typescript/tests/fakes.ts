/**
 * In-memory test doubles for the routing/DTN/SOS unit-test suite.
 * SPDX-License-Identifier: MIT
 */

import { IMeshSender } from "../src/routing/IMeshSender.js";
import { MeshPacket } from "../src/protocol/MeshPacket.js";
import { PeerInfo } from "../src/models/index.js";

export interface UnicastRecord {
  packet: MeshPacket;
  nextHopUhid: string;
}

export class FakeMeshSender implements IMeshSender {
  readonly localUhid: string;
  localGeohash?: string;
  private peers: PeerInfo[] = [];
  private failPeers = new Set<string>();
  unicasts: UnicastRecord[] = [];
  broadcasts: MeshPacket[] = [];

  constructor(localUhid: string, localGeohash?: string) {
    this.localUhid = localUhid;
    this.localGeohash = localGeohash;
  }

  getConnectedPeers(): PeerInfo[] {
    return [...this.peers];
  }

  addPeer(peer: PeerInfo): void {
    this.peers.push(peer);
  }

  failSendsTo(uhid: string): void {
    this.failPeers.add(uhid);
  }

  async send(packet: MeshPacket, nextHopUhid: string): Promise<boolean> {
    if (this.failPeers.has(nextHopUhid)) return false;
    this.unicasts.push({ packet: clonePacket(packet), nextHopUhid });
    return true;
  }

  async broadcast(packet: MeshPacket): Promise<number> {
    this.broadcasts.push(clonePacket(packet));
    return this.peers.length;
  }

  clear(): void {
    this.unicasts = [];
    this.broadcasts = [];
  }
}

function clonePacket(p: MeshPacket): MeshPacket {
  const c = new MeshPacket();
  c.id = p.id;
  c.type = p.type;
  c.sourceUhid = p.sourceUhid;
  c.destinationUhid = p.destinationUhid;
  c.ttl = p.ttl;
  c.priority = p.priority;
  c.payload = new Uint8Array(p.payload);
  c.signature = new Uint8Array(p.signature);
  c.packetNonce = new Uint8Array(p.packetNonce);
  c.timestampMs = p.timestampMs;
  c.protocolVersion = p.protocolVersion;
  c.createdAt = new Date(p.createdAt);
  return c;
}
