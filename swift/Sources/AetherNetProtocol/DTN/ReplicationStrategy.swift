// SPDX-License-Identifier: MIT

import Foundation

/// Decides which connected peers should receive a copy of a bundle on the next replication pass.
public protocol ReplicationStrategy: Sendable {
    func selectTargets(bundle: DtnBundle, peers: [PeerInfo], localGeohash: String?) -> [String]
}

/// Default geohash-aware epidemic strategy. SOS bundles fan out to every eligible carrier
/// up to the copy cap; normal bundles prefer peers whose geohash shares a longer prefix
/// with the recipient than the local node. Ties broken by reliability score.
public struct GeohashEpidemicStrategy: ReplicationStrategy {
    public init() {}

    public func selectTargets(bundle: DtnBundle, peers: [PeerInfo], localGeohash: String?) -> [String] {
        let slots = Int(bundle.maxCopies - bundle.copyCount)
        if slots <= 0 { return [] }

        let dtnFlag = NodeCapabilityBits.dtnCarrier
        let eligible = peers.filter { p in
            !p.uhid.isEmpty
                && p.uhid != bundle.senderUhid
                && !p.isBlocked
                && (p.capabilities & dtnFlag) == dtnFlag
        }
        if eligible.isEmpty { return [] }

        if bundle.priority == BundlePriority.sos.rawValue {
            return eligible.prefix(slots).map { $0.uhid }
        }

        if let recipient = bundle.recipientLastGeohash, !recipient.isEmpty {
            let localProx = sharedPrefix(localGeohash, recipient)
            let ranked = eligible
                .map { (peer: $0, prox: sharedPrefix($0.geohash, recipient)) }
                .filter { $0.prox >= localProx }
                .sorted {
                    if $0.prox != $1.prox { return $0.prox > $1.prox }
                    return $0.peer.reliabilityScore > $1.peer.reliabilityScore
                }
            return ranked.prefix(slots).map { $0.peer.uhid }
        }

        return eligible
            .sorted { $0.reliabilityScore > $1.reliabilityScore }
            .prefix(slots)
            .map { $0.uhid }
    }

    private func sharedPrefix(_ a: String?, _ b: String) -> Int {
        guard let a = a, !a.isEmpty, !b.isEmpty else { return 0 }
        let n = min(a.count, b.count)
        var i = 0
        let aChars = Array(a)
        let bChars = Array(b)
        while i < n && aChars[i] == bChars[i] { i += 1 }
        return i
    }
}
