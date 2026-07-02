/**
 * Default SOS service. Originates and re-floods SOS broadcasts.
 *
 * Dedups by packet ID; rate-limited to MAX_SOS_BROADCASTS_PER_HOUR originations
 * per rolling hour.
 *
 * SPDX-License-Identifier: MIT
 */

import {
  MAX_SOS_BROADCASTS_PER_HOUR,
  SOS_PRIORITY,
  SOS_TTL,
} from "../constants.js";
import {
  BackendClient,
  IncentiveProvider,
  NoopBackendClient,
  NoopIncentiveProvider,
} from "../extensibility.js";
import { SosAcknowledgement, SosAlert } from "../models/index.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";

export class SosBroadcastService {
  private readonly recentOrigins: Date[] = [];
  private readonly seen = new Set<string>();
  private readonly active = new Map<string, SosAlert>();

  onSosReceived?: (alert: SosAlert) => void;
  onSosResolved?: (broadcastId: string) => void;
  /**
   * Raised on the ORIGINATING node when a peer acknowledges receiving one of our active SOS
   * alerts — proof the emergency reached at least one device. Carries the responder and the
   * running distinct count. Mirrors the C# SosAcknowledged event.
   */
  onSosAcknowledged?: (ack: SosAcknowledgement) => void;

  constructor(
    private readonly sender: IMeshSender,
    private readonly backend: BackendClient = new NoopBackendClient(),
    private readonly incentives: IncentiveProvider = new NoopIncentiveProvider(),
  ) {}

  async broadcast(
    broadcastType: string,
    message: string | undefined,
    latitude: number,
    longitude: number,
    geohash?: string,
  ): Promise<boolean> {
    if (!broadcastType) throw new Error("broadcastType must not be empty");

    this.pruneOldOrigins();
    if (this.recentOrigins.length >= MAX_SOS_BROADCASTS_PER_HOUR) {
      return false;
    }
    this.recentOrigins.push(new Date());

    const alert: SosAlert = {
      id: crypto.randomUUID(),
      senderUhid: this.sender.localUhid,
      broadcastType,
      message,
      latitude,
      longitude,
      geohash,
      receivedAt: new Date(),
      acknowledgedBy: new Set<string>(),
    };
    this.active.set(alert.id, alert);

    const body = new TextEncoder().encode(
      JSON.stringify({
        broadcast_id: alert.id,
        broadcast_type: broadcastType,
        message: message ?? null,
        latitude,
        longitude,
        geohash: geohash ?? null,
      }),
    );

    const packet = new MeshPacket();
    packet.type = PacketType.SosBroadcast;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = "";
    packet.ttl = SOS_TTL;
    packet.priority = SOS_PRIORITY;
    packet.payload = body;
    this.seen.add(packet.id);

    await this.sender.broadcast(packet);
    await this.backend.syncSos(alert);
    return true;
  }

  resolve(broadcastId: string): void {
    if (this.active.delete(broadcastId)) {
      this.onSosResolved?.(broadcastId);
    }
  }

  getActiveAlerts(): SosAlert[] {
    return Array.from(this.active.values());
  }

  async handle(packet: MeshPacket): Promise<void> {
    if (packet.type !== PacketType.SosBroadcast) {
      throw new Error("expected PacketType.SosBroadcast");
    }
    if (this.seen.has(packet.id)) return;
    this.seen.add(packet.id);

    if (packet.sourceUhid === this.sender.localUhid) return;

    let data: {
      broadcast_id?: string;
      broadcast_type?: string;
      message?: string | null;
      latitude?: number;
      longitude?: number;
      geohash?: string | null;
    };
    try {
      data = JSON.parse(new TextDecoder().decode(packet.payload));
    } catch {
      return;
    }

    const alert: SosAlert = {
      id: data.broadcast_id ?? crypto.randomUUID(),
      senderUhid: packet.sourceUhid,
      broadcastType: data.broadcast_type ?? "sos",
      message: data.message ?? undefined,
      latitude: data.latitude ?? 0,
      longitude: data.longitude ?? 0,
      geohash: data.geohash ?? undefined,
      receivedAt: new Date(),
      acknowledgedBy: new Set<string>(),
    };
    this.active.set(alert.id, alert);
    this.onSosReceived?.(alert);

    // Acknowledge back to the originator so the sender learns their SOS reached a device.
    await this.sendSosAck(alert.id, packet.sourceUhid);

    if (packet.ttl > 1) {
      packet.ttl -= 1;
      await this.sender.broadcast(packet);
      await this.incentives.recordRelay(this.sender.localUhid, packet);
    }
  }

  /**
   * Pump an incoming SosAck packet into the service. On the ORIGINATING node it records the
   * responder against the matching active alert (deduping by responder UHID) and fires
   * onSosAcknowledged. No-op if the ack references an SOS this node did not originate.
   * Mirrors the C# HandleAckAsync.
   */
  async handleAck(packet: MeshPacket): Promise<void> {
    if (packet.type !== PacketType.SosAck) {
      throw new Error("expected PacketType.SosAck");
    }

    let data: { broadcast_id?: string; received_at_ms?: number };
    try {
      data = JSON.parse(new TextDecoder().decode(packet.payload));
    } catch {
      return;
    }
    if (!data.broadcast_id) return;

    // Only the ORIGINATOR holds this alert in `active`; every other node ignores the ack.
    const alert = this.active.get(data.broadcast_id);
    if (!alert) return;

    const responder = packet.sourceUhid;
    if (!responder) return;
    if (responder === this.sender.localUhid) return; // our own ack echoed back — ignore

    if (alert.acknowledgedBy.has(responder)) return; // already counted this responder — dedup
    alert.acknowledgedBy.add(responder);
    const total = alert.acknowledgedBy.size;

    this.onSosAcknowledged?.({
      broadcastId: data.broadcast_id,
      responderUhid: responder,
      totalAcknowledgements: total,
    });
  }

  // Send a directed SosAck back to the alert originator so the sender learns their emergency
  // reached this device. Best-effort: delivers when the originator is reachable as a next hop.
  private async sendSosAck(broadcastId: string, originatorUhid: string): Promise<void> {
    if (!originatorUhid) return;
    if (originatorUhid === this.sender.localUhid) return;

    const body = new TextEncoder().encode(
      JSON.stringify({
        broadcast_id: broadcastId,
        received_at_ms: Date.now(),
      }),
    );

    const ack = new MeshPacket();
    ack.type = PacketType.SosAck;
    ack.sourceUhid = this.sender.localUhid;
    ack.destinationUhid = originatorUhid;
    ack.ttl = SOS_TTL;
    ack.priority = SOS_PRIORITY;
    ack.payload = body;

    await this.sender.send(ack, originatorUhid);
  }

  private pruneOldOrigins(): void {
    const cutoff = Date.now() - 3600_000;
    while (this.recentOrigins.length > 0 && this.recentOrigins[0]!.getTime() < cutoff) {
      this.recentOrigins.shift();
    }
  }
}
