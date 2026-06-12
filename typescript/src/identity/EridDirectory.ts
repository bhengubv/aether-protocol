/**
 * EridDirectory — resolves rotating {@link EphemeralRoutingId} wire addresses to and from the stable
 * peer identities behind them. A node shares its secret routingKey with a peer INSIDE the Signal
 * session; each side stores the other's key here, so either can compute the other's current ERID and
 * reverse-resolve an inbound ERID back to the peer. An outsider holds no key and can do neither.
 *
 * Port of the C# reference (src/AetherNet.Core/Identity/EridDirectory.cs).
 *
 * SPDX-License-Identifier: MIT
 */

import { DEFAULT_EPOCH_SECONDS, DEFAULT_LENGTH, deriveForEpoch, epochFor } from "./EphemeralRoutingId.js";

export class EridDirectory {
  readonly #myRoutingKey: Uint8Array;
  readonly #epochSeconds: number;
  readonly #eridLength: number;
  readonly #peerKeys = new Map<string, Uint8Array>();

  /**
   * @param myRoutingKey this node's secret routingKey (from {@link deriveRoutingKey}). Copied.
   */
  constructor(
    myRoutingKey: Uint8Array,
    epochSeconds: number = DEFAULT_EPOCH_SECONDS,
    eridLength: number = DEFAULT_LENGTH,
  ) {
    if (myRoutingKey.length === 0) throw new Error("ERID: myRoutingKey cannot be empty");
    if (epochSeconds <= 0) throw new Error("ERID: epochSeconds must be positive");
    this.#myRoutingKey = myRoutingKey.slice();
    this.#epochSeconds = epochSeconds;
    this.#eridLength = eridLength;
  }

  /** Our own current ERID for the epoch containing `unixSeconds`. */
  myErid(unixSeconds: number): string {
    return deriveForEpoch(this.#myRoutingKey, epochFor(unixSeconds, this.#epochSeconds), this.#eridLength);
  }

  /** Store a peer's routingKey, learned inside an established session. Idempotent. */
  rememberPeer(peerUhid: string, peerRoutingKey: Uint8Array): void {
    if (!peerUhid) throw new Error("ERID: peerUhid cannot be empty");
    if (peerRoutingKey.length === 0) throw new Error("ERID: peerRoutingKey cannot be empty");
    this.#peerKeys.set(peerUhid, peerRoutingKey.slice());
  }

  /** Forget a peer (session torn down / excommunicated). Returns false if unknown. */
  forgetPeer(peerUhid: string): boolean {
    return this.#peerKeys.delete(peerUhid);
  }

  /** The current ERID a known peer presents this epoch, or null if we hold no key for them. */
  eridForPeer(peerUhid: string, unixSeconds: number): string | null {
    const key = this.#peerKeys.get(peerUhid);
    return key ? deriveForEpoch(key, epochFor(unixSeconds, this.#epochSeconds), this.#eridLength) : null;
  }

  /**
   * Reverse-resolve an inbound wire ERID to the stable peer UHID behind it for the given epoch, or
   * null if no known peer currently presents it. O(n) over known peers (a node's relationship count).
   */
  resolvePeer(erid: string, unixSeconds: number): string | null {
    if (!erid) return null;
    const epoch = epochFor(unixSeconds, this.#epochSeconds);
    for (const [uhid, key] of this.#peerKeys) {
      if (deriveForEpoch(key, epoch, this.#eridLength) === erid) return uhid;
    }
    return null;
  }

  /** Number of peers whose routingKey we currently hold. */
  get knownPeerCount(): number {
    return this.#peerKeys.size;
  }
}
