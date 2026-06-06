/**
 * Default {@link IHandshakeService}-style implementation. Tracks the peers
 * we've Hello'd, the peers we've finished negotiating with, and emits events
 * on completion / incompatibility.
 *
 * Wire flow:
 * ```
 * A -> B   Hello       { min:1, max:2, caps:[X,Y,Z], impl:"…" }
 * A <- B   HelloAck    { min:1, max:2, caps:[X,Y],   impl:"…" }
 * ```
 *
 * Negotiation rules:
 *   - Negotiated version = `min(ourMax, theirMax)`.
 *   - If `min(ourMax,theirMax) < max(ourMin,theirMin)` the ranges do not
 *     overlap → fire `incompatiblePeer`, refuse to lock in.
 *   - Locked-in capability set = `ourCaps ∩ theirCaps`.
 *
 * Mirrors the C# reference at
 * src/AetherNet.Core/Handshake/HandshakeService.cs (commit 9380631). Cross-
 * language Hello packets must round-trip — see
 * src/AetherNet.Core/Handshake/HelloPayload.cs for the JSON shape.
 *
 * SPDX-License-Identifier: MIT
 */

import { MeshPacket } from "../protocol/MeshPacket.js";
import { PacketType } from "../protocol/PacketType.js";
import { IMeshSender } from "../routing/IMeshSender.js";
import { PROTOCOL_VERSION_SIGNED } from "../constants.js";
import {
  HelloPayload,
  IncompatiblePeerEvent,
  PeerCapabilities,
} from "./models.js";

/** Default capability tags advertised by this implementation. */
export const DEFAULT_CAPABILITIES: ReadonlySet<string> = new Set<string>([
  "signal-x3dh",
  "double-ratchet",
  "dtn-custody",
  "sos",
  "voice",
  "stream",
]);

/** Default implementation banner emitted in our Hello/HelloAck. */
export const DEFAULT_IMPLEMENTATION = "aether-typescript/1.0.0";

/** Listener for negotiation-complete events. */
export type PeerNegotiatedListener = (caps: PeerCapabilities) => void;

/** Listener for incompatible-peer events. */
export type IncompatiblePeerListener = (event: IncompatiblePeerEvent) => void;

/**
 * Construction options. All fields optional — defaults match the C#
 * reference (we speak versions 1..PROTOCOL_VERSION_SIGNED and advertise
 * {@link DEFAULT_CAPABILITIES}).
 */
export interface HandshakeServiceOptions {
  ourMinVersion?: number;
  ourMaxVersion?: number;
  ourCapabilities?: ReadonlySet<string> | Iterable<string>;
  ourImplementation?: string;
}

/**
 * Protocol-version + capability negotiation service.
 *
 * Peers exchange a {@link PacketType.Hello} / {@link PacketType.HelloAck}
 * pair on first contact: each side announces the protocol-version range it
 * can speak and the capability tags it supports; the receiver replies with
 * the highest mutually-supported version + the intersection of capability
 * tags. Once locked in, subsequent traffic is gated against this record.
 *
 * The handshake itself is unencrypted and unauthenticated — it runs before
 * any Signal session exists. Peer identity is verified later via Ed25519
 * packet signatures on data packets. The capability set must therefore be
 * treated as a hint, not as an authenticated claim.
 *
 * Backward-compat: a peer that never replies with a HelloAck is assumed to
 * be running protocol version 1 with no advertised capabilities. Traffic
 * still flows; services that depend on optional capabilities should query
 * {@link getPeerCapabilities} and degrade gracefully if a capability tag
 * is absent.
 */
export class HandshakeService {
  private readonly sender: IMeshSender;
  private readonly ourMinVersion: number;
  private readonly ourMaxVersion: number;
  private readonly ourCapabilities: ReadonlySet<string>;
  private readonly ourImplementation: string;

  /** Peers we've already sent a Hello to, to suppress duplicate sends. */
  private readonly helloSent = new Set<string>();

  /** Peers we've finished negotiating with. */
  private readonly negotiated = new Map<string, PeerCapabilities>();

  private readonly peerNegotiatedListeners: PeerNegotiatedListener[] = [];
  private readonly incompatiblePeerListeners: IncompatiblePeerListener[] = [];

  constructor(sender: IMeshSender, options: HandshakeServiceOptions = {}) {
    if (!sender) throw new Error("sender is required");
    this.sender = sender;
    this.ourMinVersion = options.ourMinVersion ?? 1;
    this.ourMaxVersion = options.ourMaxVersion ?? PROTOCOL_VERSION_SIGNED;
    if (this.ourMinVersion > this.ourMaxVersion) {
      throw new Error(
        `ourMinVersion (${this.ourMinVersion}) cannot exceed ourMaxVersion (${this.ourMaxVersion}).`
      );
    }
    if (this.ourMinVersion < 0 || this.ourMinVersion > 255) {
      throw new Error(`ourMinVersion must fit a byte (got ${this.ourMinVersion}).`);
    }
    if (this.ourMaxVersion < 0 || this.ourMaxVersion > 255) {
      throw new Error(`ourMaxVersion must fit a byte (got ${this.ourMaxVersion}).`);
    }
    this.ourCapabilities = options.ourCapabilities
      ? new Set(options.ourCapabilities)
      : DEFAULT_CAPABILITIES;
    this.ourImplementation = options.ourImplementation ?? DEFAULT_IMPLEMENTATION;
  }

  /** Subscribe to negotiation-complete events. Returns an unsubscribe fn. */
  onPeerNegotiated(listener: PeerNegotiatedListener): () => void {
    this.peerNegotiatedListeners.push(listener);
    return () => {
      const i = this.peerNegotiatedListeners.indexOf(listener);
      if (i >= 0) this.peerNegotiatedListeners.splice(i, 1);
    };
  }

  /** Subscribe to incompatible-peer events. Returns an unsubscribe fn. */
  onIncompatiblePeer(listener: IncompatiblePeerListener): () => void {
    this.incompatiblePeerListeners.push(listener);
    return () => {
      const i = this.incompatiblePeerListeners.indexOf(listener);
      if (i >= 0) this.incompatiblePeerListeners.splice(i, 1);
    };
  }

  /**
   * Initiate a Hello towards a freshly discovered peer. No-op if a Hello
   * has already been sent to this peer in the current session
   * (re-broadcasts can cause duplicate Hellos otherwise).
   */
  async initiate(peerUhid: string): Promise<void> {
    if (!peerUhid) throw new Error("peerUhid is required");
    if (peerUhid === this.sender.localUhid) return;

    // Suppress duplicate Hellos.
    if (this.helloSent.has(peerUhid)) return;
    this.helloSent.add(peerUhid);

    const hello = this.buildPacket(PacketType.Hello, peerUhid);
    await this.sender.send(hello, peerUhid);
  }

  /**
   * Handle an inbound {@link PacketType.Hello}: lock in their announced
   * capabilities and reply with a HelloAck.
   */
  async handleHello(helloPacket: MeshPacket): Promise<void> {
    if (!helloPacket) throw new Error("helloPacket is required");
    if (helloPacket.type !== PacketType.Hello) {
      throw new Error(`Expected Hello, got ${helloPacket.type}`);
    }
    if (!helloPacket.sourceUhid) return;
    if (helloPacket.sourceUhid === this.sender.localUhid) return;

    const theirs = this.tryDeserialize(helloPacket);
    if (theirs === null) return;

    const negotiated = this.tryNegotiate(helloPacket.sourceUhid, theirs);
    if (negotiated === null) return; // incompatiblePeer already fired

    this.negotiated.set(helloPacket.sourceUhid, negotiated);
    this.firePeerNegotiated(negotiated);

    // Reply with HelloAck — even if we already sent them an unprompted
    // Hello, the spec is symmetric and the ack carries our own range/caps.
    const ack = this.buildPacket(PacketType.HelloAck, helloPacket.sourceUhid);
    await this.sender.send(ack, helloPacket.sourceUhid);
  }

  /**
   * Handle an inbound {@link PacketType.HelloAck}: lock in the negotiated
   * capabilities for the replying peer.
   */
  async handleHelloAck(helloAckPacket: MeshPacket): Promise<void> {
    if (!helloAckPacket) throw new Error("helloAckPacket is required");
    if (helloAckPacket.type !== PacketType.HelloAck) {
      throw new Error(`Expected HelloAck, got ${helloAckPacket.type}`);
    }
    if (!helloAckPacket.sourceUhid) return;
    if (helloAckPacket.sourceUhid === this.sender.localUhid) return;

    const theirs = this.tryDeserialize(helloAckPacket);
    if (theirs === null) return;

    const negotiated = this.tryNegotiate(helloAckPacket.sourceUhid, theirs);
    if (negotiated === null) return; // incompatiblePeer already fired

    this.negotiated.set(helloAckPacket.sourceUhid, negotiated);
    this.firePeerNegotiated(negotiated);
  }

  /**
   * Look up the locked-in capabilities for a peer. Returns null if the
   * handshake has not yet completed — callers can either subscribe to
   * {@link onPeerNegotiated} or proceed with caution.
   */
  async getPeerCapabilities(peerUhid: string): Promise<PeerCapabilities | null> {
    if (!peerUhid) throw new Error("peerUhid is required");
    return this.negotiated.get(peerUhid) ?? null;
  }

  /**
   * Drop a peer's cached capabilities and re-issue a Hello on the next
   * outbound contact. Used when version-mismatch is detected in subsequent
   * traffic.
   */
  async renegotiate(peerUhid: string): Promise<void> {
    if (!peerUhid) throw new Error("peerUhid is required");
    this.negotiated.delete(peerUhid);
    this.helloSent.delete(peerUhid);
  }

  /** Snapshot of every peer that has finished negotiating. */
  getAllNegotiated(): readonly PeerCapabilities[] {
    return Array.from(this.negotiated.values());
  }

  /**
   * Backward-compat: install a "v1, no caps" record for a peer that never
   * replied to our Hello within the timeout window. Hosts call this from
   * their own timer / heartbeat loop. Idempotent — if the peer has since
   * replied with a HelloAck, the existing record wins.
   */
  assumeLegacyV1(peerUhid: string): void {
    if (!peerUhid) throw new Error("peerUhid is required");
    if (peerUhid === this.sender.localUhid) return;

    if (this.negotiated.has(peerUhid)) return;

    const fallback: PeerCapabilities = {
      peerUhid,
      negotiatedVersion: 1,
      capabilities: new Set<string>(),
      implementationVersion: "",
      negotiatedAt: new Date(),
    };
    this.negotiated.set(peerUhid, fallback);
    this.firePeerNegotiated(fallback);
  }

  // ─── internals ─────────────────────────────────────────────────────────

  private buildPacket(type: PacketType, destinationUhid: string): MeshPacket {
    const payload: HelloPayload = {
      min_version: this.ourMinVersion,
      max_version: this.ourMaxVersion,
      capabilities: Array.from(this.ourCapabilities),
      implementation: this.ourImplementation,
    };
    const json = JSON.stringify(payload);

    const packet = MeshPacket.create(type, this.sender.localUhid);
    packet.destinationUhid = destinationUhid;
    packet.ttl = 1; // direct hop only — handshake never relays
    packet.priority = 0;
    packet.protocolVersion = this.ourMaxVersion;
    packet.payload = new Uint8Array(Buffer.from(json, "utf8"));
    return packet;
  }

  private tryDeserialize(packet: MeshPacket): HelloPayload | null {
    if (!packet.payload || packet.payload.length === 0) return null;
    try {
      const text = Buffer.from(packet.payload).toString("utf8");
      const parsed = JSON.parse(text);
      if (parsed === null || typeof parsed !== "object") return null;
      const minVersion = (parsed as Record<string, unknown>).min_version;
      const maxVersion = (parsed as Record<string, unknown>).max_version;
      if (typeof minVersion !== "number" || typeof maxVersion !== "number") return null;
      const capsRaw = (parsed as Record<string, unknown>).capabilities;
      const capabilities: string[] = Array.isArray(capsRaw)
        ? capsRaw.filter((s): s is string => typeof s === "string")
        : [];
      const implRaw = (parsed as Record<string, unknown>).implementation;
      const implementation = typeof implRaw === "string" ? implRaw : "";
      return {
        min_version: Math.trunc(minVersion),
        max_version: Math.trunc(maxVersion),
        capabilities,
        implementation,
      };
    } catch {
      return null;
    }
  }

  /**
   * Run the negotiation rules against the peer's announced HelloPayload.
   * Returns the locked-in {@link PeerCapabilities} on success, null on
   * incompatibility (in which case `incompatiblePeer` has already fired).
   */
  private tryNegotiate(peerUhid: string, theirs: HelloPayload): PeerCapabilities | null {
    if (theirs.min_version > theirs.max_version) {
      this.fireIncompatible(peerUhid, theirs, "inverted version range");
      return null;
    }

    // Overlap check: highest min must be <= lowest max.
    const overlapMin = Math.max(this.ourMinVersion, theirs.min_version);
    const overlapMax = Math.min(this.ourMaxVersion, theirs.max_version);
    if (overlapMin > overlapMax) {
      this.fireIncompatible(
        peerUhid,
        theirs,
        `no version overlap (ours=${this.ourMinVersion}..${this.ourMaxVersion}, theirs=${theirs.min_version}..${theirs.max_version})`
      );
      return null;
    }

    // Pick the highest mutually-supported version.
    const chosenVersion = overlapMax;

    // Capability intersection (case-sensitive — capability names are wire
    // constants, not human strings).
    const intersection = new Set<string>();
    for (const cap of theirs.capabilities ?? []) {
      if (cap && this.ourCapabilities.has(cap)) intersection.add(cap);
    }

    return {
      peerUhid,
      negotiatedVersion: chosenVersion,
      capabilities: intersection,
      implementationVersion: theirs.implementation ?? "",
      negotiatedAt: new Date(),
    };
  }

  private firePeerNegotiated(caps: PeerCapabilities): void {
    for (const listener of this.peerNegotiatedListeners) {
      try {
        listener(caps);
      } catch {
        // listener exceptions must not break other subscribers
      }
    }
  }

  private fireIncompatible(peerUhid: string, theirs: HelloPayload, reason: string): void {
    const event: IncompatiblePeerEvent = {
      peerUhid,
      theirMinVersion: theirs.min_version,
      theirMaxVersion: theirs.max_version,
      ourMinVersion: this.ourMinVersion,
      ourMaxVersion: this.ourMaxVersion,
      reason,
    };
    for (const listener of this.incompatiblePeerListeners) {
      try {
        listener(event);
      } catch {
        // listener exceptions must not break other subscribers
      }
    }
  }
}
