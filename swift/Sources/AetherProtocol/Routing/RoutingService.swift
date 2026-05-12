// SPDX-License-Identifier: MIT

import Foundation

/// AODV-inspired reactive routing service.
///
/// Lifecycle:
///   - `findRoute(_:)` returns cached or discovers via RREQ/RREP.
///   - `handleRouteRequest(_:)` / `handleRouteReply(_:)` pump received packets.
///   - `prune()` clears expired routes and trims the RREQ dedup state.
public actor RoutingService {
    private let sender: any MeshSender
    private let store: any RouteStore
    private let verifier: any RouteReplyVerifier
    private let incentives: any IncentiveProvider

    private var cache: [String: RouteEntry] = [:]
    private var pending: [String: CheckedContinuation<RouteEntry?, Never>] = [:]
    private var seenRreqs: Set<UUID> = []
    private var loaded = false
    /// Per-source sliding-window timestamps (milliseconds since epoch).
    private var rreqSources: [String: [Int64]] = [:]
    /// Optional reputation service; nil disables flood recording.
    private var reputation: NodeReputationService?

    public init(
        sender: any MeshSender,
        store: any RouteStore = InMemoryRouteStore(),
        verifier: any RouteReplyVerifier = AcceptAllRouteReplyVerifier(),
        incentives: any IncentiveProvider = NoopIncentiveProvider()
    ) {
        self.sender = sender
        self.store = store
        self.verifier = verifier
        self.incentives = incentives
    }

    public func findRoute(_ destinationUhid: String) async -> RouteEntry? {
        guard !destinationUhid.isEmpty else { return nil }
        await ensureLoaded()

        if let cached = cache[destinationUhid], !cached.isExpired {
            return cached
        }
        if let stored = await store.get(destinationUhid), !stored.isExpired {
            cache[destinationUhid] = stored
            return stored
        }
        return await discover(destinationUhid)
    }

    public func getCachedRoute(_ destinationUhid: String) -> RouteEntry? {
        guard let cached = cache[destinationUhid], !cached.isExpired else { return nil }
        return cached
    }

    public func getAllRoutes() -> [RouteEntry] {
        cache.values.filter { !$0.isExpired }
    }

    /// Attach an optional NodeReputationService. Pass nil to disable.
    public func setReputation(_ rep: NodeReputationService?) {
        reputation = rep
    }

    public func handleRouteRequest(_ rreq: MeshPacket) async {
        guard rreq.type == .routeRequest else { return }
        guard seenRreqs.insert(rreq.id).inserted else { return }
        // Per-source RREQ rate limiting — mirrors Go/Rust RoutingService.
        if !rreq.sourceUhid.isEmpty {
            let nowMs = Int64(Date().timeIntervalSince1970 * 1000)
            let windowStart = nowMs - ProtocolConstants.rreqRateLimitWindowMs
            let src = rreq.sourceUhid
            let existing = rreqSources[src] ?? []
            let recent = existing.filter { $0 > windowStart }
            if recent.count >= ProtocolConstants.rreqRateLimitMax {
                rreqSources[src] = recent
                seenRreqs.remove(rreq.id)   // undo dedup add — flood packets must not persist
                await reputation?.recordRreqFloodAttempt(uhid: src)
                return  // silently drop: source is flooding unique RREQs
            }
            rreqSources[src] = recent + [nowMs]
        }

        let local = sender.localUhid
        guard !rreq.sourceUhid.isEmpty, rreq.sourceUhid != local else { return }

        let hopCount = max(1, Int(ProtocolConstants.defaultTtl) - Int(rreq.ttl) + 1)
        let reverse = RouteEntry(
            destination: rreq.sourceUhid,
            nextHop: rreq.sourceUhid,
            hopCount: hopCount,
            expiresAt: Date(timeIntervalSinceNow: TimeInterval(ProtocolConstants.routeExpirySeconds)),
            qualityScore: 50
        )
        cache[reverse.destination] = reverse
        await store.save(reverse)

        if rreq.destinationUhid == local {
            await sendRouteReply(repliedSource: local, rreq: rreq)
            return
        }
        if let known = cache[rreq.destinationUhid], !known.isExpired {
            await sendRouteReply(repliedSource: rreq.destinationUhid, rreq: rreq)
            return
        }
        if rreq.ttl > 1 {
            var fwd = rreq
            fwd.ttl = rreq.ttl - 1
            _ = await sender.broadcast(fwd)
            await incentives.recordRelay(localUhid: local, packet: fwd)
        }
    }

    public func handleRouteReply(_ rrep: MeshPacket) async {
        guard rrep.type == .routeReply else { return }
        guard await verifier.verify(rrep) else { return }

        let local = sender.localUhid
        guard !rrep.sourceUhid.isEmpty, rrep.sourceUhid != local else { return }

        let hopCount = max(1, Int(ProtocolConstants.defaultTtl) - Int(rrep.ttl) + 1)
        let forward = RouteEntry(
            destination: rrep.sourceUhid,
            nextHop: rrep.sourceUhid,
            hopCount: hopCount,
            expiresAt: Date(timeIntervalSinceNow: TimeInterval(ProtocolConstants.routeExpirySeconds)),
            qualityScore: 50
        )
        cache[forward.destination] = forward
        await store.save(forward)

        if rrep.destinationUhid == local {
            if let cont = pending.removeValue(forKey: forward.destination) {
                cont.resume(returning: forward)
            }
            return
        }
        if rrep.ttl <= 1 { return }
        guard let next = cache[rrep.destinationUhid], !next.isExpired else { return }

        var fwd = rrep
        fwd.ttl = rrep.ttl - 1
        let delivered = await sender.send(fwd, nextHopUhid: next.nextHop)
        if delivered { await incentives.recordRelay(localUhid: local, packet: fwd) }
    }

    public func prune() async {
        for (k, v) in cache where v.isExpired { cache.removeValue(forKey: k) }
        if seenRreqs.count > 10_000 { seenRreqs.removeAll() }
        _ = await store.pruneExpired()
    }

    private func sendRouteReply(repliedSource: String, rreq: MeshPacket) async {
        var rrep = MeshPacket(
            type: .routeReply,
            sourceUhid: repliedSource,
            destinationUhid: rreq.sourceUhid
        )
        rrep.ttl = ProtocolConstants.defaultTtl
        rrep.payload = rreq.payload

        if let reverse = cache[rreq.sourceUhid], !reverse.isExpired {
            _ = await sender.send(rrep, nextHopUhid: reverse.nextHop)
        } else {
            _ = await sender.broadcast(rrep)
        }
    }

    private func discover(_ destinationUhid: String) async -> RouteEntry? {
        var rreq = MeshPacket(
            type: .routeRequest,
            sourceUhid: sender.localUhid,
            destinationUhid: destinationUhid
        )
        rreq.ttl = ProtocolConstants.defaultTtl

        let fanout = await sender.broadcast(rreq)
        if fanout == 0 { return nil }

        // Race the awaited continuation against a timeout
        return await withTaskGroup(of: RouteEntry?.self) { group in
            group.addTask { [weak self] in
                guard let self = self else { return nil }
                return await withCheckedContinuation { (cont: CheckedContinuation<RouteEntry?, Never>) in
                    Task { await self.registerPending(destinationUhid, cont: cont) }
                }
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: UInt64(ProtocolConstants.routeTimeoutMs) * 1_000_000)
                return nil
            }
            let first = await group.next() ?? nil
            group.cancelAll()
            await self.releasePending(destinationUhid)
            return first ?? nil
        }
    }

    private func registerPending(_ key: String, cont: CheckedContinuation<RouteEntry?, Never>) {
        pending[key] = cont
    }

    private func releasePending(_ key: String) {
        if let cont = pending.removeValue(forKey: key) {
            cont.resume(returning: nil)
        }
    }

    private func ensureLoaded() async {
        if loaded { return }
        loaded = true
        for r in await store.getAll() where !r.isExpired {
            cache[r.destination] = r
        }
    }
}
