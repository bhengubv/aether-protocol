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
    /// Raised on the ORIGINATING node when a peer acknowledges receiving one of our active SOS
    /// alerts — proof the emergency reached at least one device. Carries the responder and the
    /// running distinct count.
    public var onSosAcknowledged: (@Sendable (SosAcknowledgement) -> Void)?

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

    public func setOnSosAcknowledged(_ callback: (@Sendable (SosAcknowledgement) -> Void)?) {
        onSosAcknowledged = callback
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

        // Acknowledge back to the originator so the sender learns their SOS reached a device.
        await sendSosAck(broadcastId: alert.id, to: packet.sourceUhid)

        if packet.ttl > 1 {
            var fwd = packet
            fwd.ttl = packet.ttl - 1
            _ = await sender.broadcast(fwd)
            await incentives.recordRelay(localUhid: sender.localUhid, packet: fwd)
        }
    }

    /// Pump an incoming ``PacketType/sosAck`` packet into the service. On the originating node it
    /// records the responder against the matching active alert (deduping by responder UHID) and
    /// fires ``onSosAcknowledged``. No-op if the ack references an SOS this node did not originate
    /// (only the originator holds it in `activeAlerts`), or one it has already resolved.
    public func handleAck(_ packet: MeshPacket) async throws {
        guard packet.type == .sosAck else {
            throw SosError.unexpectedPacketType(expected: .sosAck, actual: packet.type)
        }

        guard let body = parseSosAckWire(packet.payload) else { return }

        // Only the ORIGINATOR holds this alert in activeAlerts; every other node ignores the ack.
        guard var alert = activeAlerts[body.broadcastId] else { return }

        let responder = packet.sourceUhid
        if responder.isEmpty { return }
        if responder == sender.localUhid { return } // our own ack echoed back — ignore

        // Dedup by responder UHID (the packet source, NOT the payload).
        guard alert.acknowledgedBy.insert(responder).inserted else { return }
        activeAlerts[body.broadcastId] = alert
        let total = alert.acknowledgedBy.count

        onSosAcknowledged?(SosAcknowledgement(
            broadcastId: body.broadcastId,
            responderUhid: responder,
            totalAcknowledgements: total
        ))
    }

    /// Send a directed ``PacketType/sosAck`` back to the alert originator so the sender learns their
    /// emergency reached this device. Best-effort: delivers when the originator is reachable as a
    /// next hop. Skips empty or self originators.
    private func sendSosAck(broadcastId: UUID, to originatorUhid: String) async {
        if originatorUhid.isEmpty { return }
        if originatorUhid == sender.localUhid { return }

        let body = encodeSosAckWire(
            broadcastId: broadcastId,
            receivedAtMs: Int64(Date().timeIntervalSince1970 * 1000)
        )

        let ack = MeshPacket(
            type: .sosAck,
            sourceUhid: sender.localUhid,
            destinationUhid: originatorUhid,
            ttl: ProtocolConstants.sosTtl,
            priority: ProtocolConstants.sosPriority,
            payload: body
        )

        _ = await sender.send(ack, nextHopUhid: originatorUhid)
    }

    private func pruneOldOrigins() {
        let cutoff = Date().addingTimeInterval(-3600)
        recentOrigins.removeAll { $0 < cutoff }
    }
}

// ─── snake_case JSON wire ───

private struct SosWire: Codable {
    @LowercaseUUIDCoding var broadcast_id: UUID
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

// ─── SosAck wire (PacketType 6) ───
//
// Serialises to snake_case keys, field order broadcast_id then received_at_ms, no whitespace,
// GUID lowercase-dashed. This is the byte-identity gate (fixtures/sos/vectors.json).

private struct SosAckWire: Codable {
    @LowercaseUUIDCoding var broadcast_id: UUID
    let received_at_ms: Int64
}

// JSONEncoder key order is non-deterministic (hash-seed-dependent per process); build by hand in
// field order so byte-identity holds. (parseSosAckWire uses JSONDecoder, which is order-independent.)
private func encodeSosAckWire(broadcastId: UUID, receivedAtMs: Int64) -> Data {
    Data("{\"broadcast_id\":\"\(broadcastId.uuidString.lowercased())\",\"received_at_ms\":\(receivedAtMs)}".utf8)
}

/// Test-only shim exposing the real ``SosAckWire`` serialization path (the struct itself stays
/// `private`) so byte-identity vectors in `fixtures/sos/vectors.json` can be verified.
internal func _sosAckWireBytesForTests(broadcastId: UUID, receivedAtMs: Int64) -> Data {
    encodeSosAckWire(broadcastId: broadcastId, receivedAtMs: receivedAtMs)
}

private func parseSosAckWire(_ data: Data) -> (broadcastId: UUID, receivedAtMs: Int64)? {
    guard let w = try? JSONDecoder().decode(SosAckWire.self, from: data) else { return nil }
    return (w.broadcast_id, w.received_at_ms)
}

/// Errors thrown by ``SosBroadcastService``.
public enum SosError: Error, Equatable {
    /// A packet handed to a typed handler had the wrong ``PacketType``.
    case unexpectedPacketType(expected: PacketType, actual: PacketType)
}
