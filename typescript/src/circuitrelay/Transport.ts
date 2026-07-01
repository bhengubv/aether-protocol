/**
 * Native circuit-relay-v2 ENGINE — decentralised, no-libp2p any-node relaying on
 * the Aether mesh. Any node can act as target (Reserve capacity on a relay),
 * client (Send to a peer over a known relay route, performing the CONNECT
 * handshake then tunnelling DATA), and relay (grant reservations, bridge
 * CONNECT->STOP, and forward DATA between the two legs under a data/duration
 * budget) — all in this one class, a node can be any/all at once.
 *
 * Built on the fixture-locked {@link RelayFrame} wire format. One hop of a frame
 * is carried by the injected {@link RelayLink}. Faithful port of the C#
 * (AetherNet.Transport.CircuitRelay.CircuitRelayTransportService) and Go
 * (go/circuitrelay) reference engines — same roles, same validation, same
 * behavioural guarantees.
 *
 * SPDX-License-Identifier: MIT
 */

import {
  RelayFrame,
  RelayMessageType,
  RelayStatus,
  serializeRelayFrame,
  deserializeRelayFrame,
} from "./RelayFrame.js";

/**
 * The underlying one-hop link a {@link Transport} uses to exchange raw relay
 * frames with directly-reachable nodes — the seam between circuit-relay-v2
 * (transport-agnostic) and whatever real transport carries a frame one hop
 * (BLE, Wi-Fi Direct, WebRTC, the HTTP relay, or an in-process link in tests).
 * Mirrors the C# IRelayLink / Go RelayLink.
 */
export interface RelayLink {
  /**
   * Sends a raw relay frame to a node reachable in one hop. Returns true if the
   * frame was handed to that node's link.
   */
  sendFrame(node: string, frame: Uint8Array): boolean;
  /** Whether this node currently has a direct one-hop link to `node`. */
  canReach(node: string): boolean;
  /**
   * Registers the handler invoked when a raw frame arrives from a
   * directly-reachable node (sender node UHID, frame bytes).
   */
  onFrame(handler: (from: string, frame: Uint8Array) => void): void;
}

/** Tuning + policy for a {@link Transport} (mirrors C# CircuitRelayOptions / Go Options). */
export interface RelayOptions {
  /** How long a granted reservation remains valid, in milliseconds. */
  reservationTtlMs: number;
  /** Maximum concurrent reservations this node will hold as a relay. */
  maxReservations: number;
  /** Maximum concurrent bridges this node will service as a relay. */
  maxBridges: number;
  /** Per-bridge data budget in bytes granted by this relay. 0 = unlimited. */
  bridgeDataLimitBytes: number;
  /** Per-bridge duration budget in seconds granted by this relay. 0 = unlimited. */
  bridgeDurationLimitSeconds: number;
  /** How long a client waits for a CONNECT to be confirmed before giving up, in ms. */
  connectTimeoutMs: number;
  /** How long a client waits for a RESERVE to be confirmed before giving up, in ms. */
  reserveTimeoutMs: number;
  /** Whether this node grants reservations and bridges traffic for others. */
  actAsRelay: boolean;
}

/** The same defaults as the C# / Go references. */
export function defaultRelayOptions(): RelayOptions {
  return {
    reservationTtlMs: 30 * 60 * 1000, // 30 minutes
    maxReservations: 128,
    maxBridges: 128,
    bridgeDataLimitBytes: 0,
    bridgeDurationLimitSeconds: 0,
    connectTimeoutMs: 10 * 1000,
    reserveTimeoutMs: 10 * 1000,
    actAsRelay: true,
  };
}

/** A bridge this node is relaying (relay role). */
interface RelayBridge {
  a: string;
  b: string;
  dataBudget: number;
  /** Epoch ms deadline; Number.POSITIVE_INFINITY => no duration limit. */
  deadline: number;
  dataUsed: number;
  open: boolean;
}

/** An established bridge from this node's endpoint view: which connection, via which relay. */
interface ActiveBridge {
  connId: string;
  relay: string;
}

/** A CONNECT/RESERVE waiter: a resolver plus the timer to cancel on resolution. */
interface Pending {
  resolve: (status: RelayStatus) => void;
  timer: ReturnType<typeof setTimeout>;
}

/**
 * The native circuit-relay-v2 engine. Wire it onto a {@link RelayLink}; register
 * an onData callback to receive tunnelled data delivered to this node as an
 * endpoint.
 */
export class Transport {
  private readonly localUhid: string;
  private readonly link: RelayLink;
  private readonly opts: RelayOptions;
  private readonly now: () => number;

  // Relay role
  private readonly reservations = new Map<string, number>(); // client UHID -> expiry (epoch ms)
  private readonly bridges = new Map<string, RelayBridge>(); // connId -> bridge

  // Client / target role
  private readonly routes = new Map<string, string>(); // dest -> relay
  private readonly peerBridges = new Map<string, ActiveBridge>(); // peer -> bridge
  private readonly pendingConnects = new Map<string, Pending>(); // connId -> waiter
  private readonly pendingReservations = new Map<string, Pending>(); // relay -> waiter

  private onDataCb: ((sender: string, data: Uint8Array) => void) | null = null;

  /**
   * @param localUhid This node's UHID.
   * @param link One-hop link to directly-reachable nodes.
   * @param opts Policy/tuning (defaults to {@link defaultRelayOptions}).
   * @param now Clock returning epoch ms (injectable for deterministic
   *   reservation-expiry tests; defaults to Date.now).
   */
  constructor(
    localUhid: string,
    link: RelayLink,
    opts: RelayOptions = defaultRelayOptions(),
    now: () => number = () => Date.now(),
  ) {
    this.localUhid = localUhid;
    this.link = link;
    this.opts = opts;
    this.now = now;
    this.link.onFrame((from, frame) => this.onFrame(from, frame));
  }

  /**
   * Registers the callback invoked when tunnelled data is delivered to this node
   * as an endpoint (sender UHID, payload).
   */
  setOnData(cb: (sender: string, data: Uint8Array) => void): void {
    this.onDataCb = cb;
  }

  /**
   * Records that `dest` is reachable via `relay` (in production, from the
   * directory / reservation gossip; tests set it directly).
   */
  setRoute(dest: string, relay: string): void {
    this.routes.set(dest, relay);
  }

  /** Number of bridges this node is currently servicing as a relay (diagnostics/tests). */
  activeBridgeCount(): number {
    return this.bridges.size;
  }

  /** Number of reservations this node is currently holding as a relay (diagnostics/tests). */
  activeReservationCount(): number {
    return this.reservations.size;
  }

  /** True once a relay bridge to `peer` has been established. */
  isConnected(peer: string): boolean {
    return this.peerBridges.has(peer);
  }

  /**
   * Reserves capacity on `relay` so peers can reach this node through it.
   * Resolves true once the relay confirms the reservation.
   */
  async reserve(relay: string): Promise<boolean> {
    if (!this.link.canReach(relay)) return false;

    const wait = this.registerPending(this.pendingReservations, relay, this.opts.reserveTimeoutMs);
    const frame: RelayFrame = this.newFrame(RelayMessageType.Reserve, {
      sourceUhid: this.localUhid,
      relayUhid: relay,
    });
    this.link.sendFrame(relay, serializeRelayFrame(frame));

    try {
      return (await wait) === RelayStatus.Ok;
    } finally {
      this.pendingReservations.delete(relay);
    }
  }

  /**
   * Delivers `data` to `peer`, establishing a relay bridge first if needed.
   * Resolves true if the frame was handed to the relay.
   */
  async send(peer: string, data: Uint8Array): Promise<boolean> {
    const existing = this.peerBridges.get(peer);
    if (existing) return this.sendData(existing, peer, data);

    // No bridge yet — establish one through the known relay for this peer.
    const relay = this.routes.get(peer);
    if (relay === undefined || !this.link.canReach(relay)) return false;

    if ((await this.connect(peer, relay)) !== RelayStatus.Ok) return false;

    const b = this.peerBridges.get(peer);
    return b !== undefined && this.sendData(b, peer, data);
  }

  // ── Client handshake ────────────────────────────────────────────────────────

  private async connect(dest: string, relay: string): Promise<RelayStatus> {
    const connId = crypto.randomUUID();
    const wait = this.registerPending(this.pendingConnects, connId, this.opts.connectTimeoutMs);
    const frame: RelayFrame = this.newFrame(RelayMessageType.Connect, {
      sourceUhid: this.localUhid,
      destinationUhid: dest,
      relayUhid: relay,
      connectionId: connId,
    });

    try {
      if (!this.link.sendFrame(relay, serializeRelayFrame(frame))) return RelayStatus.ConnectionFailed;
      return await wait;
    } finally {
      this.pendingConnects.delete(connId);
    }
  }

  private sendData(bridge: ActiveBridge, peer: string, data: Uint8Array): boolean {
    const frame: RelayFrame = this.newFrame(RelayMessageType.Data, {
      sourceUhid: this.localUhid,
      destinationUhid: peer,
      relayUhid: bridge.relay,
      connectionId: bridge.connId,
      payload: data,
    });
    return this.link.sendFrame(bridge.relay, serializeRelayFrame(frame));
  }

  // ── Inbound frame dispatch ────────────────────────────────────────────────────

  private onFrame(from: string, frame: Uint8Array): void {
    let f: RelayFrame;
    try {
      f = deserializeRelayFrame(frame);
    } catch {
      return; // drop malformed
    }
    switch (f.type) {
      case RelayMessageType.Reserve:
        this.handleReserve(from, f);
        break;
      case RelayMessageType.ReserveResponse:
        this.handleReserveResponse(from, f);
        break;
      case RelayMessageType.Connect:
        this.handleConnect(from, f);
        break;
      case RelayMessageType.Stop:
        this.handleStop(from, f);
        break;
      case RelayMessageType.StopResponse:
        this.handleStopResponse(from, f);
        break;
      case RelayMessageType.ConnectResponse:
        this.handleConnectResponse(from, f);
        break;
      case RelayMessageType.Data:
        this.handleData(from, f);
        break;
    }
  }

  // Relay: grant/refuse a reservation.
  private handleReserve(from: string, f: RelayFrame): void {
    if (!this.opts.actAsRelay || this.reservations.size >= this.opts.maxReservations) {
      this.send2(from, this.newFrame(RelayMessageType.ReserveResponse, {
        sourceUhid: f.sourceUhid,
        relayUhid: this.localUhid,
        status: RelayStatus.ReservationRefused,
      }));
      return;
    }
    const expiry = this.now() + this.opts.reservationTtlMs;
    this.reservations.set(f.sourceUhid, expiry);
    this.send2(from, this.newFrame(RelayMessageType.ReserveResponse, {
      sourceUhid: f.sourceUhid,
      relayUhid: this.localUhid,
      status: RelayStatus.Ok,
      reservationExpiresAtMs: expiry,
    }));
  }

  // Client: reservation confirmed/denied.
  private handleReserveResponse(from: string, f: RelayFrame): void {
    this.resolvePending(this.pendingReservations, from, f.status);
  }

  // Relay: A wants to reach B. Validate B's reservation + reachability, then open a STOP to B.
  private handleConnect(from: string, f: RelayFrame): void {
    const a = f.sourceUhid;
    const b = f.destinationUhid;

    if (!this.opts.actAsRelay) {
      this.replyConnect(a, f, RelayStatus.ConnectionFailed);
      return;
    }
    const exp = this.reservations.get(b);
    if (exp === undefined || this.now() >= exp) {
      this.reservations.delete(b);
      this.replyConnect(a, f, RelayStatus.NoReservation);
      return;
    }
    if (!this.link.canReach(b)) {
      this.replyConnect(a, f, RelayStatus.ConnectionFailed);
      return;
    }
    if (this.bridges.size >= this.opts.maxBridges) {
      this.replyConnect(a, f, RelayStatus.ResourceLimitExceeded);
      return;
    }

    const deadline =
      this.opts.bridgeDurationLimitSeconds > 0
        ? this.now() + this.opts.bridgeDurationLimitSeconds * 1000
        : Number.POSITIVE_INFINITY;
    this.bridges.set(f.connectionId, {
      a,
      b,
      dataBudget: this.opts.bridgeDataLimitBytes,
      deadline,
      dataUsed: 0,
      open: false,
    });

    this.send2(b, this.newFrame(RelayMessageType.Stop, {
      sourceUhid: a,
      destinationUhid: b,
      relayUhid: this.localUhid,
      connectionId: f.connectionId,
      limitDataBytes: this.opts.bridgeDataLimitBytes,
      limitDurationSeconds: this.opts.bridgeDurationLimitSeconds,
    }));
  }

  // Target: relay says A wants to reach us. Accept and record a return route to A.
  private handleStop(from: string, f: RelayFrame): void {
    this.peerBridges.set(f.sourceUhid, { connId: f.connectionId, relay: from });
    this.send2(from, this.newFrame(RelayMessageType.StopResponse, {
      sourceUhid: f.sourceUhid,
      destinationUhid: this.localUhid,
      relayUhid: from,
      connectionId: f.connectionId,
      status: RelayStatus.Ok,
    }));
  }

  // Relay: target accepted/refused. Finalise the bridge and answer the client.
  private handleStopResponse(from: string, f: RelayFrame): void {
    const bridge = this.bridges.get(f.connectionId);
    if (bridge === undefined) return;

    if (f.status !== RelayStatus.Ok) {
      this.bridges.delete(f.connectionId);
      this.replyConnect(bridge.a, f, RelayStatus.ConnectionFailed);
      return;
    }

    bridge.open = true;
    this.send2(bridge.a, this.newFrame(RelayMessageType.ConnectResponse, {
      sourceUhid: bridge.a,
      destinationUhid: bridge.b,
      relayUhid: this.localUhid,
      connectionId: f.connectionId,
      status: RelayStatus.Ok,
      limitDataBytes: bridge.dataBudget,
    }));
  }

  // Client: bridge established/refused.
  private handleConnectResponse(from: string, f: RelayFrame): void {
    if (f.status === RelayStatus.Ok) {
      this.peerBridges.set(f.destinationUhid, { connId: f.connectionId, relay: from });
    }
    this.resolvePending(this.pendingConnects, f.connectionId, f.status);
  }

  // Data: either I'm an endpoint (deliver) or the relay (forward the other way, under budget).
  private handleData(from: string, f: RelayFrame): void {
    if (f.destinationUhid === this.localUhid) {
      this.onDataCb?.(f.sourceUhid, f.payload);
      return;
    }

    const bridge = this.bridges.get(f.connectionId);
    if (bridge === undefined || !bridge.open) return; // unknown / not-yet-open bridge — drop
    if (from !== bridge.a && from !== bridge.b) return; // frame not from a party to this bridge

    if (this.now() >= bridge.deadline) {
      this.bridges.delete(f.connectionId);
      return;
    }

    bridge.dataUsed += f.payload.length;
    if (bridge.dataBudget > 0 && bridge.dataUsed > bridge.dataBudget) {
      this.bridges.delete(f.connectionId);
      return;
    }

    // Forward the frame unchanged to the other endpoint (= its dst).
    this.link.sendFrame(f.destinationUhid, serializeRelayFrame(f));
  }

  // ── Pending-map helpers (Promise + timeout race) ──────────────────────────────

  private registerPending(
    map: Map<string, Pending>,
    key: string,
    timeoutMs: number,
  ): Promise<RelayStatus> {
    return new Promise<RelayStatus>((resolve) => {
      const timer = setTimeout(() => {
        map.delete(key);
        resolve(RelayStatus.ConnectionFailed); // timeout
      }, timeoutMs);
      map.set(key, { resolve, timer });
    });
  }

  private resolvePending(map: Map<string, Pending>, key: string, status: RelayStatus): void {
    const p = map.get(key);
    if (p === undefined) return;
    map.delete(key);
    clearTimeout(p.timer);
    p.resolve(status);
  }

  // ── Frame construction / send helpers ─────────────────────────────────────────

  private newFrame(type: RelayMessageType, fields: Partial<RelayFrame>): RelayFrame {
    return {
      type,
      status: fields.status ?? RelayStatus.Ok,
      sourceUhid: fields.sourceUhid ?? "",
      destinationUhid: fields.destinationUhid ?? "",
      relayUhid: fields.relayUhid ?? "",
      connectionId: fields.connectionId ?? "",
      reservationExpiresAtMs: fields.reservationExpiresAtMs ?? 0,
      limitDurationSeconds: fields.limitDurationSeconds ?? 0,
      limitDataBytes: fields.limitDataBytes ?? 0,
      payload: fields.payload ?? new Uint8Array(0),
    };
  }

  private send2(to: string, f: RelayFrame): void {
    this.link.sendFrame(to, serializeRelayFrame(f));
  }

  private replyConnect(client: string, connect: RelayFrame, status: RelayStatus): void {
    this.send2(client, this.newFrame(RelayMessageType.ConnectResponse, {
      sourceUhid: connect.sourceUhid,
      destinationUhid: connect.destinationUhid,
      relayUhid: this.localUhid,
      connectionId: connect.connectionId,
      status,
    }));
  }
}
