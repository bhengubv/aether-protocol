/**
 * DTN replication strategy.
 * SPDX-License-Identifier: MIT
 */

import {
  BundlePriority,
  DtnBundle,
  NodeCapabilities,
  PeerInfo,
} from "../models/index.js";

export interface IBundleReplicationStrategy {
  selectTargets(
    bundle: DtnBundle,
    connectedPeers: PeerInfo[],
    localGeohash: string | undefined,
  ): string[];
}

function sharedPrefix(a: string | undefined, b: string): number {
  if (!a || !b) return 0;
  const min = Math.min(a.length, b.length);
  for (let i = 0; i < min; i++) {
    if (a[i] !== b[i]) return i;
  }
  return min;
}

/**
 * Default strategy. SOS bundles fan out to every eligible DTN-carrier peer up
 * to the copy cap. Normal bundles prefer peers whose geohash shares a longer
 * prefix with the recipient than the local node — i.e. peers at least as close
 * to the recipient. Ties broken by reliability.
 */
export class GeohashEpidemicStrategy implements IBundleReplicationStrategy {
  selectTargets(
    bundle: DtnBundle,
    connectedPeers: PeerInfo[],
    localGeohash: string | undefined,
  ): string[] {
    const slots = bundle.maxCopies - bundle.copyCount;
    if (slots <= 0) return [];

    const dtnFlag = NodeCapabilities.DtnCarrier;
    const eligible = connectedPeers.filter(
      (p) =>
        p.uhid &&
        p.uhid !== bundle.senderUhid &&
        !p.isBlocked &&
        ((p.capabilities ?? 0) & dtnFlag) !== 0,
    );
    if (eligible.length === 0) return [];

    if (bundle.priority === BundlePriority.Sos) {
      return eligible.slice(0, slots).map((p) => p.uhid);
    }

    if (bundle.recipientLastGeohash) {
      const localProx = sharedPrefix(localGeohash, bundle.recipientLastGeohash);
      const ranked = eligible
        .map((p) => ({
          peer: p,
          prox: sharedPrefix(p.geohash, bundle.recipientLastGeohash!),
        }))
        .filter((x) => x.prox >= localProx)
        .sort((a, b) => {
          if (a.prox !== b.prox) return b.prox - a.prox;
          return b.peer.reliabilityScore - a.peer.reliabilityScore;
        });
      return ranked.slice(0, slots).map((r) => r.peer.uhid);
    }

    return eligible
      .slice()
      .sort((a, b) => b.reliabilityScore - a.reliabilityScore)
      .slice(0, slots)
      .map((p) => p.uhid);
  }
}
