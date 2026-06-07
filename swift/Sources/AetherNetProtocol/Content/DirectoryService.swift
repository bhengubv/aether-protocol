// SPDX-License-Identifier: MIT

import Foundation

/// Wire payload for ``PacketType/namePublish``.
/// Serialized as JSON with snake_case property names for cross-language interop.
///
/// Two modes:
/// - Unsolicited broadcast: the publisher emits this on
///   ``DirectoryService/publish(name:descriptor:)``. ``inResponseToQueryId`` is nil.
/// - Query response: a peer that holds the name unicasts this back to a querier
///   carrying the original query's correlation id.
public struct NamePublishPayload: Equatable, Codable, Sendable {
    /// The application-layer name being announced.
    public var name: String
    /// The full descriptor that the name resolves to.
    public var descriptor: ContentDescriptor
    /// If non-nil, this is a unicast response to a prior ``PacketType/nameQuery``
    /// whose ``NameQueryPayload/queryId`` matched this value. If nil, the publish is unsolicited.
    public var inResponseToQueryId: UUID?

    public init(name: String, descriptor: ContentDescriptor, inResponseToQueryId: UUID? = nil) {
        self.name = name
        self.descriptor = descriptor
        self.inResponseToQueryId = inResponseToQueryId
    }

    private enum CodingKeys: String, CodingKey {
        case name
        case descriptor
        case inResponseToQueryId = "in_response_to_query_id"
    }
}

/// Wire payload for ``PacketType/nameQuery``. A broadcast request asking peers to
/// send a ``NamePublishPayload`` for the named entry back to the sender, correlated
/// by ``queryId``.
/// Serialized as JSON with snake_case property names for cross-language interop.
public struct NameQueryPayload: Equatable, Codable, Sendable {
    /// The application-layer name being queried.
    public var name: String
    /// Correlation id. Echoed by responders in ``NamePublishPayload/inResponseToQueryId``
    /// so the querier can match responses to outstanding queries.
    public var queryId: UUID

    public init(name: String, queryId: UUID = UUID()) {
        self.name = name
        self.queryId = queryId
    }

    private enum CodingKeys: String, CodingKey {
        case name
        case queryId = "query_id"
    }
}

/// Event payload for ``DirectoryService/onEntryAnnounced`` — raised when a
/// ``PacketType/namePublish`` packet arrives and the local catalogue learns a
/// new (or replaced) name → descriptor binding.
public struct DirectoryEntryAnnouncedEvent: Sendable {
    /// The newly-learned application-layer name.
    public let name: String
    /// The descriptor the name resolves to.
    public let descriptor: ContentDescriptor
    /// UHID of the peer that emitted the announcement.
    public let sourceUhid: String
    /// UTC time the announcement arrived locally.
    public let announcedAtUtc: Date

    public init(name: String, descriptor: ContentDescriptor, sourceUhid: String, announcedAtUtc: Date = Date()) {
        self.name = name
        self.descriptor = descriptor
        self.sourceUhid = sourceUhid
        self.announcedAtUtc = announcedAtUtc
    }
}

/// Errors raised by ``DirectoryService``.
public enum DirectoryServiceError: Error, Equatable {
    /// `name` was empty.
    case emptyName
}

/// Application-layer name → ``ContentDescriptor`` resolver. Closes the
/// Wave-16 protocol gap: the content service is content-addressed (`rootHash`-keyed)
/// — consumers that want to fetch content by an application-layer name (e.g.
/// `"podcast:abc123"`, `"reel:hash"`, `"album:artist/title"`) cannot do so via
/// content-addressing alone because they do not know the `rootHash` upfront.
/// That's precisely what they're trying to discover.
///
/// This service maintains a local name catalogue, broadcasts
/// ``PacketType/namePublish`` when the local node publishes a binding, emits
/// ``PacketType/nameQuery`` when the local node needs to resolve an unknown name,
/// and unicasts a ``PacketType/namePublish`` response when a peer's query matches
/// an entry we hold.
///
/// Added in v1.2.0. Closes Issue #60 — see `OPEN_ISSUES.md`.
public actor DirectoryService {
    /// Default timeout for ``resolve(name:timeout:)`` when no value is supplied.
    public static let defaultQueryTimeout: TimeInterval = 5.0

    private let sender: any MeshSender

    // Local catalogue: name → descriptor.
    private var catalogue: [String: ContentDescriptor] = [:]

    // Outstanding queries keyed by QueryId. Completed when a matching NamePublish arrives
    // or resumed with nil on timeout.
    private var pendingQueries: [UUID: CheckedContinuation<ContentDescriptor?, Never>] = [:]

    /// Callback fired when a ``PacketType/namePublish`` packet arrives — either an
    /// unsolicited broadcast from a peer or a unicast response to one of our
    /// outstanding queries — and updates the local catalogue.
    /// Matches ``DtnService/onBundleDelivered`` callback idiom.
    public var onEntryAnnounced: (@Sendable (DirectoryEntryAnnouncedEvent) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    public func setOnEntryAnnounced(_ callback: (@Sendable (DirectoryEntryAnnouncedEvent) -> Void)?) {
        self.onEntryAnnounced = callback
    }

    /// Store the binding locally and broadcast a ``PacketType/namePublish`` to every
    /// connected peer. Subsequent ``resolve(name:timeout:)`` calls on the local node
    /// return the descriptor immediately from the catalogue.
    public func publish(name: String, descriptor: ContentDescriptor) async throws {
        guard !name.isEmpty else { throw DirectoryServiceError.emptyName }

        catalogue[name] = descriptor

        let payload = NamePublishPayload(name: name, descriptor: descriptor, inResponseToQueryId: nil)
        let data = (try? JSONEncoder().encode(payload)) ?? Data()

        let packet = MeshPacket(
            type: .namePublish,
            sourceUhid: sender.localUhid,
            ttl: ProtocolConstants.defaultTtl,
            payload: data
        )
        _ = await sender.broadcast(packet)
    }

    /// Resolve a name to its descriptor. Returns the local-catalogue hit immediately
    /// if present. Otherwise broadcasts a ``PacketType/nameQuery`` and awaits a
    /// matching ``PacketType/namePublish`` response up to `timeout` seconds (default 5).
    /// Returns nil on timeout.
    public func resolve(name: String, timeout: TimeInterval = DirectoryService.defaultQueryTimeout) async -> ContentDescriptor? {
        if name.isEmpty { return nil }
        if let cached = catalogue[name] { return cached }

        let queryId = UUID()
        let query = NameQueryPayload(name: name, queryId: queryId)
        let payloadData = (try? JSONEncoder().encode(query)) ?? Data()
        let packet = MeshPacket(
            type: .nameQuery,
            sourceUhid: sender.localUhid,
            ttl: ProtocolConstants.defaultTtl,
            payload: payloadData
        )
        _ = await sender.broadcast(packet)

        // Race the query against the timeout. We need to ensure the continuation
        // is resumed exactly once; the actor's serialization protects the dict.
        let result = await withCheckedContinuation { (cont: CheckedContinuation<ContentDescriptor?, Never>) in
            pendingQueries[queryId] = cont
            Task { [weak self] in
                let nanos: UInt64 = timeout > 0 ? UInt64(timeout * 1_000_000_000) : 0
                try? await Task.sleep(nanoseconds: nanos)
                await self?.timeoutQuery(queryId: queryId)
            }
        }
        return result
    }

    /// Internal: invoked by the timeout task. Resumes the pending continuation with nil if still outstanding.
    private func timeoutQuery(queryId: UUID) {
        if let cont = pendingQueries.removeValue(forKey: queryId) {
            cont.resume(returning: nil)
        }
    }

    /// Enumerate every name currently in the local catalogue (snapshot).
    public func listNames() async -> [String] {
        Array(catalogue.keys)
    }

    /// Pump inbound ``PacketType/namePublish`` / ``PacketType/nameQuery`` packets into
    /// the service. Hosts wire this from their transport's receive pump.
    public func handle(_ packet: MeshPacket) async throws {
        switch packet.type {
        case .namePublish:
            handlePublish(packet)
        case .nameQuery:
            await handleQuery(packet)
        default:
            // Silently ignore unrelated packet types.
            break
        }
    }

    private func handlePublish(_ packet: MeshPacket) {
        guard let payload = try? JSONDecoder().decode(NamePublishPayload.self, from: packet.payload) else {
            return
        }
        if payload.name.isEmpty { return }

        catalogue[payload.name] = payload.descriptor

        // Query-response correlation.
        if let queryId = payload.inResponseToQueryId,
           let cont = pendingQueries.removeValue(forKey: queryId) {
            cont.resume(returning: payload.descriptor)
        }

        let event = DirectoryEntryAnnouncedEvent(
            name: payload.name,
            descriptor: payload.descriptor,
            sourceUhid: packet.sourceUhid,
            announcedAtUtc: Date()
        )
        if let cb = onEntryAnnounced { cb(event) }
    }

    private func handleQuery(_ packet: MeshPacket) async {
        guard let query = try? JSONDecoder().decode(NameQueryPayload.self, from: packet.payload) else {
            return
        }
        if query.name.isEmpty { return }
        guard let descriptor = catalogue[query.name] else {
            // We don't hold this name — silently ignore. Other peers may answer.
            return
        }

        let response = NamePublishPayload(
            name: query.name,
            descriptor: descriptor,
            inResponseToQueryId: query.queryId
        )
        let body = (try? JSONEncoder().encode(response)) ?? Data()
        let pkt = MeshPacket(
            type: .namePublish,
            sourceUhid: sender.localUhid,
            destinationUhid: packet.sourceUhid,
            ttl: ProtocolConstants.defaultTtl,
            payload: body
        )
        _ = await sender.send(pkt, nextHopUhid: packet.sourceUhid)
    }
}
