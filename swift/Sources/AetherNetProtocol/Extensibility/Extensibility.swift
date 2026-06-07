// SPDX-License-Identifier: MIT

import Foundation

/// Records relays for reward calculation; decides priority. Default: no-op.
public protocol IncentiveProvider: Sendable {
    func recordRelay(localUhid: String, packet: MeshPacket) async
    func shouldPrioritize(packet: MeshPacket) async -> Bool
    /// Called when the local user tips a content author. Distinct from
    /// recordRelay (relay credit - paid to nodes that forward bytes); this
    /// records direct creator -> consumer settlement (paid to the user who
    /// AUTHORED the content). Host implementations (e.g. SDPKT, BhenguPay)
    /// wire their settlement logic here. Default no-op does nothing.
    /// Added in v1.2.0 - closes Issue #61 surfaced by Wave 16.
    func recordCreatorTip(creatorUhid: String, amount: Decimal, contentHash: String) async throws
}

public extension IncentiveProvider {
    func recordRelay(localUhid: String, packet: MeshPacket) async {}
    func shouldPrioritize(packet: MeshPacket) async -> Bool { false }
    func recordCreatorTip(creatorUhid: String, amount: Decimal, contentHash: String) async throws {}
}

/// Optional cloud-relay seam. Default returns false everywhere.
public protocol BackendClient: Sendable {
    func relayMessage(senderUhid: String, recipientUhid: String, encryptedContent: Data, priority: UInt8) async -> Bool
    func syncDtnBundle(_ bundle: DtnBundle) async -> Bool
    func syncSos(_ alert: SosAlert) async -> Bool
}

public extension BackendClient {
    func relayMessage(senderUhid: String, recipientUhid: String, encryptedContent: Data, priority: UInt8) async -> Bool { false }
    func syncDtnBundle(_ bundle: DtnBundle) async -> Bool { false }
    func syncSos(_ alert: SosAlert) async -> Bool { false }
}

/// Gates protocol features behind remote configuration. Default: every feature enabled.
public protocol FeatureFlagProvider: Sendable {
    func isEnabled(_ featureName: String) async -> Bool
}

public extension FeatureFlagProvider {
    func isEnabled(_ featureName: String) async -> Bool { true }
}

/// Default no-op implementations.
public struct NoopIncentiveProvider: IncentiveProvider {
    public init() {}
}

public struct NoopBackendClient: BackendClient {
    public init() {}
}

public struct NoopFeatureFlagProvider: FeatureFlagProvider {
    public init() {}
}
