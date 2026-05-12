/**
 * Default DTN service. Three-tier delivery:
 * direct mesh send → DTN epidemic replication → backend relay.
 *
 * SPDX-License-Identifier: MIT
 */

import {
  BackendClient,
  IncentiveProvider,
  NoopBackendClient,
  NoopIncentiveProvider,
} from "../extensibility.js";
import {
  BundlePriority,
  BundleStatus,
  CustodyRecord,
  DtnBundle,
  DtnDeliveryReceipt,
  isBundleExpired,
  newDtnBundle,
} from "../models/index.js";
import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import {
  DEFAULT_TTL,
  DTN_MAX_BUNDLES_PER_NODE,
} from "../constants.js";
import { IMeshSender } from "../routing/IMeshSender.js";
import { NodeReputationService } from "../reputation.js";
import { IDtnBundleStore, InMemoryDtnBundleStore } from "./IDtnBundleStore.js";
import {
  GeohashEpidemicStrategy,
  IBundleReplicationStrategy,
} from "./IBundleReplicationStrategy.js";

const DTN_BUNDLE_TTL_FOR_PACKET = 30; // ProtocolConstants.DtnTtl

export class DtnService {
  onBundleDelivered?: (receipt: DtnDeliveryReceipt) => void;

  private reputation: NodeReputationService | null = null;

  constructor(
    private readonly sender: IMeshSender,
    private readonly store: IDtnBundleStore = new InMemoryDtnBundleStore(),
    private readonly strategy: IBundleReplicationStrategy = new GeohashEpidemicStrategy(),
    private readonly incentives: IncentiveProvider = new NoopIncentiveProvider(),
    private readonly backend: BackendClient = new NoopBackendClient(),
  ) {}

  setReputation(rep: NodeReputationService | null): void {
    this.reputation = rep;
  }

  async createBundle(
    recipientUhid: string,
    encryptedPayload: Uint8Array,
    priority: BundlePriority = BundlePriority.Normal,
    recipientLastGeohash?: string,
  ): Promise<DtnBundle> {
    if (!recipientUhid) throw new Error("recipientUhid must not be empty");
    const bundle = newDtnBundle(this.sender.localUhid, recipientUhid, encryptedPayload, priority);
    bundle.recipientLastGeohash = recipientLastGeohash;
    bundle.senderGeohash = this.sender.localGeohash;
    await this.store.save(bundle);

    if (await this.tryDirectDelivery(bundle)) {
      bundle.status = BundleStatus.Delivered;
      await this.store.save(bundle);
    }
    return bundle;
  }

  async handle(packet: MeshPacket): Promise<void> {
    switch (packet.type) {
      case PacketType.DtnBundle:
        await this.handleBundle(packet);
        break;
      case PacketType.DtnCustodyAck:
        await this.handleCustodyAck(packet);
        break;
      case PacketType.DtnDeliveryReceipt:
        await this.handleDeliveryReceipt(packet);
        break;
      default:
        break;
    }
  }

  async runDeliveryScan(): Promise<void> {
    const active = await this.store.getActive();
    if (active.length === 0) return;
    const peers = this.sender.getConnectedPeers();
    const localGeohash = this.sender.localGeohash;

    for (const bundle of active) {
      if (bundle.status === BundleStatus.Delivered || isBundleExpired(bundle)) continue;
      if (await this.tryDirectDelivery(bundle)) {
        bundle.status = BundleStatus.Delivered;
        await this.store.save(bundle);
        continue;
      }
      if (peers.length === 0 || bundle.copyCount >= bundle.maxCopies) continue;
      const targets = this.strategy.selectTargets(bundle, peers, localGeohash);
      for (const target of targets) {
        if (bundle.copyCount >= bundle.maxCopies) break;
        const pkt = this.bundlePacket(bundle, target);
        if (await this.sender.send(pkt, target)) {
          bundle.copyCount += 1;
          await this.store.save(bundle);
          await this.incentives.recordRelay(this.sender.localUhid, pkt);
        }
      }
    }
  }

  expireStale(): Promise<number> {
    return this.store.expireStale();
  }

  getActiveBundles(): Promise<DtnBundle[]> {
    return this.store.getActive();
  }

  private async tryDirectDelivery(bundle: DtnBundle): Promise<boolean> {
    const pkt = this.bundlePacket(bundle, bundle.recipientUhid);
    for (const peer of this.sender.getConnectedPeers()) {
      if (peer.uhid === bundle.recipientUhid) {
        if (await this.sender.send(pkt, bundle.recipientUhid)) return true;
        break;
      }
    }
    return this.backend.syncDtnBundle(bundle);
  }

  private bundlePacket(bundle: DtnBundle, _nextHopUhid: string): MeshPacket {
    const packet = new MeshPacket();
    packet.id = bundle.id;
    packet.type = PacketType.DtnBundle;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = bundle.recipientUhid;
    packet.ttl = DTN_BUNDLE_TTL_FOR_PACKET;
    packet.priority = Math.min(255, Math.max(0, bundle.priority));
    packet.payload = encodeBundle(bundle);
    return packet;
  }

  private async handleBundle(packet: MeshPacket): Promise<void> {
    const bundle = decodeBundle(packet.payload);
    if (!bundle) return;

    if (bundle.recipientUhid === this.sender.localUhid) {
      bundle.status = BundleStatus.Delivered;
      await this.store.save(bundle);
      await this.sendDeliveryReceipt(bundle);
      this.reputation?.recordDeliverySuccess(packet.sourceUhid, 0);
      return;
    }

    if ((await this.store.getActiveCount()) >= DTN_MAX_BUNDLES_PER_NODE) {
      await this.sendCustodyAck(bundle.id, packet.sourceUhid, false);
      return;
    }

    bundle.status = BundleStatus.InCustody;
    bundle.hopCount += 1;
    await this.store.save(bundle);
    const record: CustodyRecord = {
      id: crypto.randomUUID(),
      bundleId: bundle.id,
      fromUhid: packet.sourceUhid,
      toUhid: this.sender.localUhid,
      accepted: true,
      transferredAt: new Date(),
    };
    await this.store.saveCustody(record);
    await this.sendCustodyAck(bundle.id, packet.sourceUhid, true);
    await this.incentives.recordRelay(this.sender.localUhid, packet);
  }

  private async handleCustodyAck(packet: MeshPacket): Promise<void> {
    let data: { bundle_id?: string; accepted?: boolean };
    try {
      data = JSON.parse(new TextDecoder().decode(packet.payload));
    } catch {
      return;
    }
    if (!data.bundle_id) return;
    if (data.accepted === false) {
      this.reputation?.recordCustodyRefusal(packet.sourceUhid);
      return;
    }
    if (!data.accepted) return;
    const bundle = await this.store.get(data.bundle_id);
    if (!bundle) return;
    bundle.copyCount += 1;
    await this.store.save(bundle);
  }

  private async handleDeliveryReceipt(packet: MeshPacket): Promise<void> {
    let data: {
      bundle_id?: string;
      recipient_uhid?: string;
      total_hops?: number;
      total_custody_transfers?: number;
      delivered_at_ms?: number;
    };
    try {
      data = JSON.parse(new TextDecoder().decode(packet.payload));
    } catch {
      return;
    }
    if (!data.bundle_id) return;
    const receipt: DtnDeliveryReceipt = {
      bundleId: data.bundle_id,
      recipientUhid: data.recipient_uhid ?? "",
      totalHops: data.total_hops ?? 0,
      totalCustodyTransfers: data.total_custody_transfers ?? 0,
      deliveredAt: new Date(data.delivered_at_ms ?? Date.now()),
    };
    const bundle = await this.store.get(data.bundle_id);
    if (bundle) {
      bundle.status = BundleStatus.Delivered;
      await this.store.save(bundle);
    }
    this.onBundleDelivered?.(receipt);
  }

  private async sendCustodyAck(bundleId: string, toUhid: string, accepted: boolean): Promise<void> {
    if (!toUhid) return;
    const body = new TextEncoder().encode(
      JSON.stringify({ bundle_id: bundleId, accepted }),
    );
    const packet = new MeshPacket();
    packet.type = PacketType.DtnCustodyAck;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = toUhid;
    packet.ttl = DEFAULT_TTL;
    packet.payload = body;
    await this.sender.send(packet, toUhid);
  }

  private async sendDeliveryReceipt(bundle: DtnBundle): Promise<void> {
    if (!bundle.senderUhid || bundle.senderUhid === this.sender.localUhid) return;
    const custody = await this.store.getCustodyRecords(bundle.id);
    const body = new TextEncoder().encode(
      JSON.stringify({
        bundle_id: bundle.id,
        recipient_uhid: bundle.recipientUhid,
        total_hops: bundle.hopCount,
        total_custody_transfers: custody.length,
        delivered_at_ms: Date.now(),
      }),
    );
    const packet = new MeshPacket();
    packet.type = PacketType.DtnDeliveryReceipt;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = bundle.senderUhid;
    packet.ttl = DEFAULT_TTL;
    packet.payload = body;
    await this.sender.send(packet, bundle.senderUhid);
  }
}

function encodeBundle(bundle: DtnBundle): Uint8Array {
  const obj = {
    id: bundle.id,
    sender_uhid: bundle.senderUhid,
    recipient_uhid: bundle.recipientUhid,
    encrypted_payload: Array.from(bundle.encryptedPayload),
    priority: bundle.priority,
    status: bundle.status,
    copy_count: bundle.copyCount,
    max_copies: bundle.maxCopies,
    sender_geohash: bundle.senderGeohash ?? null,
    recipient_last_geohash: bundle.recipientLastGeohash ?? null,
    hop_count: bundle.hopCount,
    created_at_ms: bundle.createdAt.getTime(),
    expires_at_ms: bundle.expiresAt.getTime(),
  };
  return new TextEncoder().encode(JSON.stringify(obj));
}

function decodeBundle(payload: Uint8Array): DtnBundle | null {
  try {
    const data = JSON.parse(new TextDecoder().decode(payload));
    return {
      id: data.id,
      senderUhid: data.sender_uhid,
      recipientUhid: data.recipient_uhid,
      encryptedPayload: new Uint8Array(data.encrypted_payload ?? []),
      priority: data.priority as BundlePriority,
      status: data.status as BundleStatus,
      copyCount: data.copy_count,
      maxCopies: data.max_copies,
      senderGeohash: data.sender_geohash ?? undefined,
      recipientLastGeohash: data.recipient_last_geohash ?? undefined,
      hopCount: data.hop_count,
      createdAt: new Date(data.created_at_ms),
      expiresAt: new Date(data.expires_at_ms),
    };
  } catch {
    return null;
  }
}
