// SPDX-License-Identifier: MIT

import Foundation

/// SOS broadcast service. Originates and re-floods SOS broadcasts.
/// Dedups by packet ID; rate-limited to MAX_SOS_BROADCASTS_PER_HOUR per rolling hour.
public actor SosBroadcastService {
    private let sender: any MeshSender
    private let backend: any BackendClient
    private let incentives: any IncentiveProvider

    private var recentOrigins: [Date] = []
    private var seen: Set<UUID> = []
    private var activeAlerts: [UUID: SosAlert] = [:]

    public var onSosReceived: (@Sendable (SosAlert) -> Void)?
    public var onSosResolved: (@Sendable (UUID) -> Void)?

    public init(
        sender: any MeshSender,
        backend: any BackendClient = NoopBackendClient(),
        incentives: any IncentiveProvider = NoopIncentiveProvider()
    ) {
        self.sender = sender
        self.backend = backend
        self.incentives = incentives
    }

    public func setOnSosReceived(_ callback: (@Sendable (SosAlert) -> Void)?) {
        onSosReceived = callback
    }

    public func setOnSosResolved(_ callback: (@Sendable (UUID) -> Void)?) {
        onSosResolved = callback
    }

    public func broadcast(
        broadcastType: String,
        message: String?,
        latitude: Double,
        longitude: Double,
        geohash: String? = nil
    ) async -> Bool {
        guard !broadcastType.isEmpty else { return false }
        pruneOldOrigins()
        if recentOrigins.count >= ProtocolConstants.maxSosBroadcastsPerHour { return false }
        recentOrigins.append(Date())

        let alert = SosAlert(
            senderUhid: sender.localUhid,
            broadcastType: broadcastType,
            message: message,
            latitude: latitude,
            longitude: longitude,
            geohash: geohash
        )
        activeAlerts[alert.id] = alert

        let body = encodeSosWire(
            broadcastId: alert.id,
            broadcastType: broadcastType,
            message: message,
            latitude: latitude,
            longitude: longitude,
            geohash: geohash
        )

        var packet = MeshPacket(
            type: .sosBroadcast,
            sourceUhid: sender.localUhid,
            destinationUhid: "",
            ttl: ProtocolConstants.sosTtl,
            priority: ProtocolConstants.sosPriority,
            payload: body
        )
        packet.timestampMs = Int64(Date().timeIntervalSince1970 * 1000)
        seen.insert(packet.id)

        _ = await sender.broadcast(packet)
        _ = await backend.syncSos(alert)
        return true
    }

    public func resolve(_ broadcastId: UUID) {
        if activeAlerts.removeValue(forKey: broadcastId) != nil {
            onSosResolved?(broadcastId)
        }
    }

    public func getActiveAlerts() -> [SosAlert] {
        Array(activeAlerts.values)
    }

    public func handle(_ packet: MeshPacket) async {
        guard packet.type == .sosBroadcast else { return }
        if !seen.insert(packet.id).inserted { return }
        if packet.sourceUhid == sender.localUhid { return }

        let parsed = parseSosWire(packet.payload)
        let alert = SosAlert(
            id: parsed?.broadcastId ?? UUID(),
            senderUhid: packet.sourceUhid,
            broadcastType: parsed?.broadcastType ?? "sos",
            message: parsed?.message,
            latitude: parsed?.latitude ?? 0,
            longitude: parsed?.longitude ?? 0,
            geohash: parsed?.geohash,
            receivedAt: Date()
        )
        activeAlerts[alert.id] = alert
        onSosReceived?(alert)

        if packet.ttl > 1 {
            var fwd = packet
            fwd.ttl = packet.ttl - 1
            _ = await sender.broadcast(fwd)
            await incentives.recordRelay(localUhid: sender.localUhid, packet: fwd)
        }
    }

    private func pruneOldOrigins() {
        let cutoff = Date().addingTimeInterval(-3600)
        recentOrigins.removeAll { $0 < cutoff }
    }
}

// ─── snake_case JSON wire ───

private struct SosWire: Codable {
    let broadcast_id: UUID
    let broadcast_type: String
    let message: String?
    let latitude: Double
    let longitude: Double
    let geohash: String?
}

private func encodeSosWire(
    broadcastId: UUID,
    broadcastType: String,
    message: String?,
    latitude: Double,
    longitude: Double,
    geohash: String?
) -> Data {
    let w = SosWire(
        broadcast_id: broadcastId,
        broadcast_type: broadcastType,
        message: message,
        latitude: latitude,
        longitude: longitude,
        geohash: geohash
    )
    return (try? JSONEncoder().encode(w)) ?? Data()
}

private func parseSosWire(
    _ data: Data
) -> (broadcastId: UUID, broadcastType: String, message: String?, latitude: Double, longitude: Double, geohash: String?)? {
    guard let w = try? JSONDecoder().decode(SosWire.self, from: data) else { return nil }
    return (w.broadcast_id, w.broadcast_type, w.message, w.latitude, w.longitude, w.geohash)
}
