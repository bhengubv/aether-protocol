/*
 * Default MeshTipService. Sends and receives generic PacketType.TipPacket (24)
 * packets. ArkTS (HarmonyOS) port of AetherNet.Security.Services.MeshTipService /
 * typescript MeshTipService.
 *
 * Send path: build a TipPacketPayload → sign the payload's canonical bytes with
 * the local identity key (real Ed25519) → serialise as snake_case JSON → wrap in a
 * MeshPacket → sign the enclosing packet → route toward the recipient (unicast over
 * a discovered route, else broadcast).
 *
 * Receive path: deserialise the payload → best-effort signature check (Ed25519
 * signature must be present and well-formed = 64 bytes) → hand to the host's
 * MeshTipSettlementProvider → relay the packet onward toward its addressed
 * recipient. A malformed or unverifiable payload is logged and dropped, never
 * thrown.
 *
 * This service is purely a protocol mechanism. It attaches NO value semantics to
 * the amount and performs NO settlement — settlement is entirely the host's
 * business, expressed through the injected provider. A bare node (default no-op
 * provider) accepts and relays tips but settles nothing.
 *
 * SPDX-License-Identifier: MIT
 */

import { MeshPacket } from '../protocol/MeshPacket';
import { PacketType } from '../protocol/PacketType';
import { utf8Encode } from '../protocol/Bytes';
import { TipPacketPayload, TipPacketPayloadInit } from './TipPacketPayload';

/** ProtocolConstants.DefaultTtl. */
const DEFAULT_TTL: number = 7;

/** The minimal mesh transport surface needed by MeshTipService. */
export interface TipMeshSender {
  /** UHID of the local node. */
  readonly localUhid: string;
  /** Delivers `packet` toward `nextHopUhid`. Returns true on success. */
  send(packet: MeshPacket, nextHopUhid: string): Promise<boolean>;
  /** Sends `packet` to every directly-connected peer; returns the fan-out count. */
  broadcast(packet: MeshPacket): Promise<number>;
}

/** Signs the enclosing MeshPacket envelope (nonce/timestamp + signature). */
export interface TipPacketSigner {
  /** Returns `packet` with the signature/nonce/timestamp fields populated. */
  signPacket(packet: MeshPacket): MeshPacket;
}

/** Signs the tip payload's canonical bytes with the local node's identity key. */
export interface TipIdentitySigner {
  /** Produces a 64-byte Ed25519 signature over `data` using the local identity key. */
  signData(data: Uint8Array): Uint8Array;
}

/**
 * Resolves a next-hop toward a destination UHID. Returns the next hop, or null to
 * fall back to broadcast.
 */
export interface TipRouteResolver {
  findNextHop(destinationUhid: string): string | null;
}

/**
 * The host's settlement hook — the ArkTS analog of the C#
 * IAetherNetIncentiveProvider.SettleMeshTipAsync. It receives the full signed
 * TipPacketPayload off the mesh and decides how (if at all) to interpret its value.
 * The default no-op settles nothing.
 */
export interface MeshTipSettlementProvider {
  /**
   * Invoked for every inbound, well-formed tip payload. Implementations (e.g.
   * SDPKT / BhenguPay) wire their wallet settlement here. A thrown error is logged
   * by the caller but never propagated to the wire — a settlement failure must not
   * break relaying.
   */
  settleMeshTip(payload: TipPacketPayload): Promise<void>;
}

/**
 * The default no-op settlement provider — accepts the tip and settles nothing. A
 * bare node carries the tip signal but never moves value.
 */
export class NoopMeshTipSettlementProvider implements MeshTipSettlementProvider {
  // The payload parameter is part of the interface contract; a bare node
  // intentionally settles nothing, so it is accepted and ignored.
  async settleMeshTip(payload: TipPacketPayload): Promise<void> {
    // intentionally no-op — a bare node carries the tip signal but never moves value.
  }
}

/** Optional diagnostic sink. */
export interface TipLogger {
  log(message: string): void;
}

/** Builds, signs, sends, and handles mesh tip packets. */
export class MeshTipService {
  private readonly sender: TipMeshSender;
  private readonly signer: TipPacketSigner;
  private readonly identity: TipIdentitySigner;
  private readonly routing: TipRouteResolver | null;
  private readonly settle: MeshTipSettlementProvider;
  private readonly logger: TipLogger | null;
  private readonly defaultTtl: number = DEFAULT_TTL;

  /**
   * Pass null for `routing` to always broadcast; pass null for `settle` to use the
   * default no-op settlement provider; pass null for `logger` to disable
   * diagnostics.
   */
  constructor(
    sender: TipMeshSender,
    signer: TipPacketSigner,
    identity: TipIdentitySigner,
    routing: TipRouteResolver | null,
    settle: MeshTipSettlementProvider | null,
    logger: TipLogger | null
  ) {
    this.sender = sender;
    this.signer = signer;
    this.identity = identity;
    this.routing = routing;
    this.settle = settle !== null ? settle : new NoopMeshTipSettlementProvider();
    this.logger = logger;
  }

  private logMsg(message: string): void {
    if (this.logger !== null) {
      this.logger.log(message);
    }
  }

  /**
   * Builds, signs, and routes a TipPacket(24) addressed to `recipientUhid`.
   * `amount` is the caller's input verbatim (the invariant decimal string) — the
   * protocol imposes NO policy on it. It is signed into the payload and carried
   * as-is. Returns the signed MeshPacket that was routed onto the mesh.
   */
  async sendTip(
    recipientUhid: string,
    amount: string,
    trafficType: string,
    referenceId: string | null,
    timestampUnixMs: bigint
  ): Promise<MeshPacket> {
    const init = new TipPacketPayloadInit();
    init.tipperUhid = this.sender.localUhid;
    init.recipientUhid = recipientUhid;
    init.amount = amount;
    init.trafficType = trafficType;
    init.referenceId = referenceId;
    init.timestampUnixMs = timestampUnixMs;
    const payload = new TipPacketPayload(init);

    // Sign the payload's canonical bytes with the local identity key (real Ed25519).
    payload.signature = this.identity.signData(payload.buildCanonicalData());

    const body: Uint8Array = utf8Encode(payload.toJsonString());

    const packet = new MeshPacket();
    packet.type = PacketType.TipPacket;
    packet.sourceUhid = this.sender.localUhid;
    packet.destinationUhid = recipientUhid;
    packet.ttl = this.defaultTtl;
    packet.priority = 0;
    packet.payload = body;

    // Sign the enclosing MeshPacket (fills nonce/timestamp + envelope signature).
    const signed: MeshPacket = this.signer.signPacket(packet);

    // Route toward the recipient: unicast over a discovered route, else broadcast.
    if (this.routing !== null) {
      const nextHop: string | null = this.routing.findNextHop(recipientUhid);
      if (nextHop !== null) {
        await this.sender.send(signed, nextHop);
        this.logMsg(`MeshTip: sent (unicast) to recipient=${recipientUhid} via ${nextHop}`);
        return signed;
      }
    }
    await this.sender.broadcast(signed);
    this.logMsg(`MeshTip: sent (broadcast) to recipient=${recipientUhid}`);
    return signed;
  }

  /**
   * Processes an inbound TipPacket(24) received off the mesh.
   *
   * Returns true when the payload was accepted and handed to the settlement
   * provider. Returns false when the packet should be silently discarded (wrong
   * type, malformed payload, missing/malformed signature).
   */
  async handleTipPacket(packet: MeshPacket | null): Promise<boolean> {
    if (packet === null) {
      return false;
    }
    if (packet.type !== PacketType.TipPacket) {
      this.logMsg(`MeshTip: unexpected packet type ${packet.type} — ignored`);
      return false;
    }

    // 1. Deserialise the payload. A malformed payload is logged and dropped.
    let payload: TipPacketPayload;
    try {
      payload = TipPacketPayload.parseBytes(packet.payload);
    } catch (err) {
      this.logMsg(`MeshTip from ${packet.sourceUhid}: JSON deserialization failed — dropped: ${err}`);
      return false;
    }
    if (payload.tipperUhid.length === 0 || payload.recipientUhid.length === 0) {
      this.logMsg(`MeshTip from ${packet.sourceUhid}: payload missing required fields — dropped`);
      return false;
    }

    // 2. Best-effort signature check: an Ed25519 signature is exactly 64 bytes. A
    //    payload carrying no signature, or a malformed one, is unverifiable —
    //    logged and dropped. The host's settlement provider is responsible for any
    //    stronger, key-bound verification it needs.
    if (!payload.hasWellFormedSignature()) {
      this.logMsg(`MeshTip from ${payload.tipperUhid}: missing or malformed signature — dropped`);
      return false;
    }

    // 3. Hand to the host's settlement provider. Default no-op settles nothing. A
    //    settlement error is logged but never breaks relaying.
    try {
      await this.settle.settleMeshTip(payload);
    } catch (err) {
      this.logMsg(`MeshTip from ${payload.tipperUhid}: settlement provider error: ${err}`);
    }

    // 4. Relay onward toward the addressed recipient if this node is not the
    //    destination and the packet may still be forwarded. The tip is ordinary
    //    addressed traffic.
    if (packet.destinationUhid !== this.sender.localUhid && packet.canForward) {
      if (this.routing !== null) {
        const nextHop: string | null = this.routing.findNextHop(packet.destinationUhid);
        if (nextHop !== null) {
          await this.sender.send(packet, nextHop);
          return true;
        }
      }
      await this.sender.broadcast(packet);
    }

    this.logMsg(
      `MeshTip handled: tipper=${payload.tipperUhid} recipient=${payload.recipientUhid} traffic=${payload.trafficType}`
    );
    return true;
  }
}
