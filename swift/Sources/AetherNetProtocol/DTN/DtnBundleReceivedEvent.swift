// SPDX-License-Identifier: MIT

import Foundation

/// Event payload fired by ``DtnService/onBundleReceived`` the moment a DTN bundle
/// arrives whose final recipient is the local node — i.e., a bundle addressed TO
/// us has just been delivered locally by a peer or by the receive pump itself.
///
/// Distinct from ``DtnDeliveryReceipt`` (fired via ``DtnService/onBundleDelivered``),
/// which fires on the original sender side once a delivery confirmation flows back.
/// Consumers that want to know "did a bundle arrive for me?" should subscribe to
/// ``DtnService/onBundleReceived``; consumers that want to know "did my outbound
/// bundle reach the recipient?" should subscribe to ``DtnService/onBundleDelivered``.
///
/// Added in v1.2.0 to close the gap surfaced by Wave 16 — previously, receive-side
/// consumers had to inspect ``DtnService/handle(_:)`` indirectly via the host shell
/// to know when a bundle had arrived.
public struct DtnBundleReceivedEvent: Sendable, Equatable {
    /// The globally-unique bundle identifier.
    public let bundleId: UUID

    /// UHID of the original sender of the bundle.
    public let senderUhid: String

    /// UHID of the recipient — always the local node when this event fires.
    public let recipientUhid: String

    /// The encrypted payload bytes as delivered. The DTN layer does not decrypt —
    /// consumers route this through their security layer.
    public let encryptedPayload: Data

    /// Replication-aggressiveness class of the bundle.
    public let priority: BundlePriority

    /// Number of custody transfers the bundle underwent before arriving here.
    public let hopCount: Int

    /// UTC timestamp at which the bundle was received locally.
    public let receivedAtUtc: Date

    public init(
        bundleId: UUID,
        senderUhid: String,
        recipientUhid: String,
        encryptedPayload: Data,
        priority: BundlePriority,
        hopCount: Int,
        receivedAtUtc: Date = Date()
    ) {
        self.bundleId = bundleId
        self.senderUhid = senderUhid
        self.recipientUhid = recipientUhid
        self.encryptedPayload = encryptedPayload
        self.priority = priority
        self.hopCount = hopCount
        self.receivedAtUtc = receivedAtUtc
    }
}
