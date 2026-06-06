// SPDX-License-Identifier: MIT

import Foundation

/// Records relays for reward calculation; decides priority. Default: no-op.
public protocol IncentiveProvider: Sendable {
    func recordRelay(localUhid: String, packet: MeshPacket) async
    func shouldPrioritize(packet: MeshPacket) async -> Bool
}

public extension IncentiveProvider {
    func recordRelay(localUhid: String, packet: MeshPacket) async {}
    func shouldPrioritize(packet: MeshPacket) async -> Bool { false }
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
