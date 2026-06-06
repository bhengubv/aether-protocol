// SPDX-License-Identifier: MIT

import Foundation

/// Persistent backing store for the routing table. Default is in-memory.
public protocol RouteStore: Sendable {
    func get(_ destinationUhid: String) async -> RouteEntry?
    func getAll() async -> [RouteEntry]
    func save(_ route: RouteEntry) async
    func remove(_ destinationUhid: String) async
    func pruneExpired() async -> Int
}

/// Process-local route store. Loses everything on restart.
public actor InMemoryRouteStore: RouteStore {
    private var routes: [String: RouteEntry] = [:]

    public init() {}

    public func get(_ destinationUhid: String) -> RouteEntry? { routes[destinationUhid] }
    public func getAll() -> [RouteEntry] { Array(routes.values) }
    public func save(_ route: RouteEntry) { routes[route.destination] = route }
    public func remove(_ destinationUhid: String) { routes.removeValue(forKey: destinationUhid) }

    public func pruneExpired() -> Int {
        let expiredKeys = routes.filter { $0.value.isExpired }.map { $0.key }
        for k in expiredKeys { routes.removeValue(forKey: k) }
        return expiredKeys.count
    }
}
