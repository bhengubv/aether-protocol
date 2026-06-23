/**
 * WebRTC signalling abstraction and an in-process reference bus.
 *
 * A {@link Signal} is a single SDP offer/answer or trickled ICE candidate that two peers must
 * exchange before a direct data channel can open. {@link Signaling} carries those signals between
 * peers by UHID — over the AetherNet relay, the radio mesh, or (here, for same-process scenarios
 * and tests) the in-memory {@link InMemorySignalingBus}. There is never a central signalling server.
 *
 * Mirrors the C# `IWebRtcSignaling` / `InMemoryWebRtcSignalingBus` and the Go `Signaling` /
 * `InMemorySignalingBus`.
 *
 * SPDX-License-Identifier: MIT
 */

/** The kind of WebRTC signalling message exchanged while a direct link is set up. */
export enum SignalType {
  /** SDP offer from the initiating peer. */
  Offer = 0,
  /** SDP answer from the responding peer. */
  Answer = 1,
  /** A trickled ICE candidate. */
  Candidate = 2,
}

/**
 * A single WebRTC signalling message — an SDP offer/answer or an ICE candidate two peers must
 * exchange before a direct data channel can open. Carried by a {@link Signaling} channel, never a
 * central signalling server.
 */
export interface Signal {
  /** UHID of the node that produced this signal. */
  readonly fromUhid: string;
  /** UHID of the node this signal is addressed to. */
  readonly toUhid: string;
  /** What this signal carries. */
  readonly type: SignalType;
  /** SDP text — set for {@link SignalType.Offer} / {@link SignalType.Answer}. */
  readonly sdp?: string;
  /** The ICE candidate string — set for {@link SignalType.Candidate}. */
  readonly candidate?: string;
  /** SDP mid for the ICE candidate. */
  readonly sdpMid?: string;
  /** SDP m-line index for the ICE candidate (0 for the single data section). */
  readonly sdpMLineIndex?: number;
}

/** Carries WebRTC SDP/ICE signalling between two peers by UHID. */
export interface Signaling {
  /**
   * Delivers a signalling message to its addressee.
   * @returns `true` if the signal was handed to the underlying channel; `false` otherwise.
   */
  sendSignal(peerUhid: string, signal: Signal): Promise<boolean>;

  /** Registers the handler invoked for signals addressed to the local node. */
  onSignal(handler: (signal: Signal) => void): void;
}

/**
 * In-process {@link Signaling} bus that routes signals between endpoints by UHID. It needs no
 * network and no server, so it backs same-process scenarios (multi-node simulations, one device
 * holding several identities) and the test suite.
 *
 * Each endpoint delivers inbound signals on its own ordered queue, drained on a microtask, so
 * signals arrive in send order and never re-enter the sender's call stack — matching the ordered,
 * reliable delivery a real signalling channel provides.
 */
export class InMemorySignalingBus {
  private readonly endpoints = new Map<string, BusEndpoint>();
  private closed = false;

  /** Returns the signalling endpoint for `uhid`, creating it once. */
  endpoint(uhid: string): Signaling {
    let endpoint = this.endpoints.get(uhid);
    if (endpoint === undefined) {
      endpoint = new BusEndpoint(this);
      this.endpoints.set(uhid, endpoint);
    }
    return endpoint;
  }

  /** Stops all endpoint pumps. */
  close(): void {
    this.closed = true;
    for (const endpoint of this.endpoints.values()) {
      endpoint.close();
    }
    this.endpoints.clear();
  }

  /** @internal Routes a signal to its addressee's endpoint. */
  route(signal: Signal): boolean {
    if (this.closed) return false;
    const target = this.endpoints.get(signal.toUhid);
    if (target === undefined) return false;
    target.deliver(signal);
    return true;
  }
}

/**
 * One endpoint on an {@link InMemorySignalingBus}: an ordered FIFO drained asynchronously so a
 * delivered signal never runs inside the sender's `sendSignal` call.
 */
class BusEndpoint implements Signaling {
  private readonly bus: InMemorySignalingBus;
  private readonly queue: Signal[] = [];
  private handler?: (signal: Signal) => void;
  private draining = false;
  private closed = false;

  constructor(bus: InMemorySignalingBus) {
    this.bus = bus;
  }

  async sendSignal(_peerUhid: string, signal: Signal): Promise<boolean> {
    return this.bus.route(signal);
  }

  onSignal(handler: (signal: Signal) => void): void {
    this.handler = handler;
  }

  /** @internal Enqueues a signal and schedules the pump. */
  deliver(signal: Signal): void {
    if (this.closed) return;
    this.queue.push(signal);
    if (!this.draining) {
      this.draining = true;
      // Drain on a microtask so delivery is ordered yet off the sender's stack.
      queueMicrotask(() => this.pump());
    }
  }

  private pump(): void {
    while (this.queue.length > 0) {
      const signal = this.queue.shift()!;
      const handler = this.handler;
      if (handler !== undefined) {
        try {
          handler(signal);
        } catch {
          // A misbehaving handler must not stop the queue.
        }
      }
    }
    this.draining = false;
  }

  close(): void {
    this.closed = true;
    this.queue.length = 0;
    this.handler = undefined;
  }
}
