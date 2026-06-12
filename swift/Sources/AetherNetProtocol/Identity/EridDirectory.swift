// SPDX-License-Identifier: MIT

import Foundation

/// Resolves rotating ``EphemeralRoutingId`` (ERID) wire addresses to and from the stable
/// peer identities behind them — the piece that lets an ESTABLISHED relationship follow a
/// peer's rotating address while an outsider cannot.
///
/// A node derives its OWN secret routingKey once (via
/// ``EphemeralRoutingId/deriveRoutingKey(_:)``) and shares it with a peer INSIDE the
/// established Signal session — never on the wire. Each side stores the other's routingKey
/// here, so either can compute the other's current ERID for addressing and reverse-resolve
/// an inbound ERID back to the peer it belongs to. An outsider holds no routingKey and can
/// do neither. Port of the C# reference (`src/AetherNet.Core/Identity/EridDirectory.cs`).
public final class EridDirectory {

    private let myRoutingKey: [UInt8]
    private let epochSeconds: Int64
    private let eridLength: Int
    private var peerKeys: [String: [UInt8]] = [:]

    /// - Parameter myRoutingKey: this node's secret routingKey (from
    ///   ``EphemeralRoutingId/deriveRoutingKey(_:)``).
    /// - Throws: ``EphemeralRoutingId/EphemeralRoutingIdError/emptyRoutingKey`` if
    ///   `myRoutingKey` is empty, or ``.invalidEpochSeconds`` if `epochSeconds <= 0`.
    public init(
        _ myRoutingKey: [UInt8],
        epochSeconds: Int64 = EphemeralRoutingId.defaultEpochSeconds,
        eridLength: Int = EphemeralRoutingId.defaultLength
    ) throws {
        guard !myRoutingKey.isEmpty else {
            throw EphemeralRoutingId.EphemeralRoutingIdError.emptyRoutingKey
        }
        guard epochSeconds > 0 else {
            throw EphemeralRoutingId.EphemeralRoutingIdError.invalidEpochSeconds
        }
        self.myRoutingKey = myRoutingKey
        self.epochSeconds = epochSeconds
        self.eridLength = eridLength
    }

    /// Our own current ERID for the epoch containing `unixSeconds` — the address we present
    /// on the wire this window.
    public func myErid(_ unixSeconds: Int64) throws -> String {
        try EphemeralRoutingId.derive(
            myRoutingKey, unixSeconds: unixSeconds,
            epochSeconds: epochSeconds, length: eridLength
        )
    }

    /// Store a peer's routingKey, learned inside an established session. Idempotent; a
    /// later call replaces an earlier key for the same peer.
    /// - Precondition: `peerUhid` and `peerRoutingKey` are non-empty.
    public func rememberPeer(_ peerUhid: String, routingKey peerRoutingKey: [UInt8]) {
        precondition(!peerUhid.isEmpty, "peerUhid cannot be empty")
        precondition(!peerRoutingKey.isEmpty, "peerRoutingKey cannot be empty")
        peerKeys[peerUhid] = peerRoutingKey
    }

    /// Forget a peer (session torn down / excommunicated). Returns false if unknown.
    @discardableResult
    public func forgetPeer(_ peerUhid: String) -> Bool {
        peerKeys.removeValue(forKey: peerUhid) != nil
    }

    /// The current ERID a known peer presents this epoch, or `nil` if we hold no key for
    /// them.
    public func eridForPeer(_ peerUhid: String, unixSeconds: Int64) throws -> String? {
        guard let key = peerKeys[peerUhid] else { return nil }
        return try EphemeralRoutingId.derive(
            key, unixSeconds: unixSeconds,
            epochSeconds: epochSeconds, length: eridLength
        )
    }

    /// Reverse-resolve an inbound wire ERID to the stable peer UHID behind it for the given
    /// epoch, or `nil` if no known peer currently presents it. O(n) over known peers — a
    /// node's actual relationship count.
    public func resolvePeer(_ erid: String, unixSeconds: Int64) throws -> String? {
        guard !erid.isEmpty else { return nil }
        for (uhid, key) in peerKeys {
            let candidate = try EphemeralRoutingId.derive(
                key, unixSeconds: unixSeconds,
                epochSeconds: epochSeconds, length: eridLength
            )
            if candidate == erid { return uhid }
        }
        return nil
    }

    /// Number of peers whose routingKey we currently hold.
    public var knownPeerCount: Int { peerKeys.count }
}
