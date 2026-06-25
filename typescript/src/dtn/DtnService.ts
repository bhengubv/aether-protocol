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
  DtnBundleReceivedEvent,
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
import {
  deserializeBundle,
  deserializeCustodyAck,
  deserializeDeliveryReceipt,
  serializeBundle,
  serializeCustodyAck,
  serializeDeliveryReceipt,
} from "./DtnEnvelope.js";

const DTN_BUNDLE_TTL_FOR_PACKET = 30; // ProtocolConstants.DtnTtl

export class DtnService {
  onBundleDelivered?: (receipt: DtnDeliveryReceipt) => void;

  /**
   * Fires the moment a DTN bundle arrives whose final recipient is the local
   * node — see DtnBundleReceivedEvent. Added in v1.2.0 — closes the Wave-16
   * gap surfaced by Issue #59.
   */
  onBundleReceived?: (event: DtnBundleReceivedEvent) => void;

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
    packet.payload = serializeBundle(bundle);
    return packet;
  }

  private async handleBundle(packet: MeshPacket): Promise<void> {
    let bundle: DtnBundle;
    try {
      bundle = deserializeBundle(packet.payload);
    } catch {
      return;
    }

    if (bundle.recipientUhid === this.sender.localUhid) {
      bundle.status = BundleStatus.Delivered;
      await this.store.save(bundle);
      this.reputation?.recordDeliverySuccess(packet.sourceUhid, 0);
      this.onBundleReceived?.({
        bundleId: bundle.id,
        senderUhid: bundle.senderUhid,
        recipientUhid: bundle.recipientUhid,
        encryptedPayload: bundle.encryptedPayload,
        priority: bundle.priority,
        hopCount: bundle.hopCount,
        receivedAtUtc: new Date(),
      });
      await this.sendDeliveryReceipt(bundle);
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
    let ack: { bundleId: string; accepted: boolean };
    try {
      ack = deserializeCustodyAck(packet.payload);
    } catch {
      return;
    }
    if (!ack.bundleId) return;
    if (!ack.accepted) {
      this.reputation?.recordCustodyRefusal(packet.sourceUhid);
      return;
    }
    const bundle = await this.store.get(ack.bundleId);
    if (!bundle) return;
    bundle.copyCount += 1;
    await this.store.save(bundle);
  }

  private async handleDeliveryReceipt(packet: MeshPacket): Promise<void> {
    let receipt: DtnDeliveryReceipt;
    try {
      receipt = deserializeDeliveryReceipt(packet.payload);
    } catch {
      return;
    }
    if (!receipt.bundleId) return;
    const bundle = await this.store.get(receipt.bundleId);
    if (bundle) {
      bundle.status = BundleStatus.Delivered;
      await this.store.save(bundle);
    }
    this.onBundleDelivered?.(receipt);
  }

  private async sendCustodyAck(bundleId: string, toUhid: string, accepted: boolean): Promise<void> {
    if (!toUhid) return;
    const body = serializeCustodyAck(bundleId, accepted);
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
    const body = serializeDeliveryReceipt({
      bundleId: bundle.id,
      recipientUhid: bundle.recipientUhid,
      totalHops: bundle.hopCount,
      totalCustodyTransfers: custody.length,
      deliveredAt: new Date(),
    });
    const packet = new MeshPacket();
    packet.type = PacketType.DtnDeliveryReceipt;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = bundle.senderUhid;
    packet.ttl = DEFAULT_TTL;
    packet.payload = body;
    await this.sender.send(packet, bundle.senderUhid);
  }
}

