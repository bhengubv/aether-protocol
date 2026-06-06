// SPDX-License-Identifier: MIT

import Foundation

/// Verifies that a received RREP was actually signed by the node it claims to come from.
/// Default `AcceptAllRouteReplyVerifier` is permissive (tests/demos only); production
/// hosts wire up an implementation backed by their security service.
public protocol RouteReplyVerifier: Sendable {
    func verify(_ routeReply: MeshPacket) async -> Bool
}

public struct AcceptAllRouteReplyVerifier: RouteReplyVerifier {
    public init() {}
    public func verify(_ routeReply: MeshPacket) async -> Bool { true }
}
