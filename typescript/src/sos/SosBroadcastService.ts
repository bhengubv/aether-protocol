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
import { SosAlert } from "../models/index.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";

export class SosBroadcastService {
  private readonly recentOrigins: Date[] = [];
  private readonly seen = new Set<string>();
  private readonly active = new Map<string, SosAlert>();

  onSosReceived?: (alert: SosAlert) => void;
  onSosResolved?: (broadcastId: string) => void;

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
    };
    this.active.set(alert.id, alert);
    this.onSosReceived?.(alert);

    if (packet.ttl > 1) {
      packet.ttl -= 1;
      await this.sender.broadcast(packet);
      await this.incentives.recordRelay(this.sender.localUhid, packet);
    }
  }

  private pruneOldOrigins(): void {
    const cutoff = Date.now() - 3600_000;
    while (this.recentOrigins.length > 0 && this.recentOrigins[0]!.getTime() < cutoff) {
      this.recentOrigins.shift();
    }
  }
}
