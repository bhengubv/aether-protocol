/**
 * ERID-announce mesh binding (PacketType.EridAnnounce = 56).
 *
 * A node shares its rotating-address routing key with an established peer by sending the
 * (already Signal-encrypted) ERID announcement directly. Transport only — the plaintext framing
 * (EridAnnouncementCodec) and the encryption (SignalProtocol) are done by the host/EridExchange
 * layer; this service just carries the opaque encrypted blob as a directed packet and surfaces
 * inbound ones via onAnnounceReceived.
 *
 * Mirrors the C# EridAnnounceService.
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_TTL } from "../constants.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";

/**
 * Binds PacketType.EridAnnounce (56) to the mesh. Send an already-encrypted ERID announcement
 * directly to a peer, and surface inbound announcements (payload still encrypted) via
 * onAnnounceReceived.
 */
export class EridAnnounceService {
  /**
   * Raised when an ERID announcement arrives from a peer (payload still Signal-encrypted). The
   * host decrypts it and feeds the plaintext to EridAnnouncementCodec.tryDecode.
   *
   * @param encryptedAnnouncement the opaque encrypted announcement bytes (the packet body).
   * @param fromUhid UHID of the peer that sent the announcement.
   */
  onAnnounceReceived?: (encryptedAnnouncement: Uint8Array, fromUhid: string) => void;

  constructor(private readonly sender: IMeshSender) {}

  /**
   * Send an already-encrypted ERID announcement directly to `peerUhid`: build a directed
   * EridAnnounce packet carrying the opaque blob. Returns delivery success. Throws for an empty
   * peer uhid or an empty announcement.
   */
  async sendAnnounce(peerUhid: string, encryptedAnnouncement: Uint8Array): Promise<boolean> {
    if (!peerUhid) throw new Error("peerUhid must not be empty");
    if (encryptedAnnouncement.length === 0) {
      throw new Error("encryptedAnnouncement cannot be empty");
    }

    const packet = new MeshPacket();
    packet.type = PacketType.EridAnnounce;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = peerUhid;
    packet.ttl = DEFAULT_TTL;
    packet.payload = encryptedAnnouncement;

    return this.sender.send(packet, peerUhid);
  }

  /**
   * Process an incoming PacketType.EridAnnounce packet: surface it via onAnnounceReceived. Returns
   * false for the wrong packet type or an empty body.
   */
  async handle(packet: MeshPacket): Promise<boolean> {
    if (packet.type !== PacketType.EridAnnounce) return false;
    if (packet.payload.length === 0) return false;

    this.onAnnounceReceived?.(packet.payload, packet.sourceUhid);
    return true;
  }
}
