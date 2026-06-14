// SPDX-License-Identifier: MIT
//
// Default MeshTipService. Sends and receives generic PacketType.tipPacket (24) packets. Swift port of
// AetherNet.Security.Services.MeshTipService, mirroring the Go and TypeScript ports.
//
// Send path: build a TipPacketPayload → sign the payload's canonical bytes with the local identity
// key (real Ed25519) → serialise as snake_case JSON → wrap in a MeshPacket → sign the enclosing packet
// → route toward the recipient (unicast over a discovered route, falling back to broadcast).
//
// Receive path: deserialise the payload → best-effort signature check (Ed25519 signature must be
// present and well-formed = 64 bytes) → hand to the host's MeshTipSettlementProvider → relay the
// packet onward toward its addressed recipient. A malformed or unverifiable payload is logged and
// dropped, never thrown.
//
// This service is purely a protocol mechanism. It attaches NO value semantics to the amount and
// performs NO settlement — settlement is entirely the host's business, expressed through the injected
// provider. A bare node (default no-op provider) accepts and relays tips but settles nothing.

import Foundation

/// Ed25519 signature length in bytes — used for the best-effort inbound check.
private let ed25519SignatureLength = 64

// MARK: - Seams

/// Signs and verifies the enclosing ``MeshPacket`` envelope for tip traffic. Mirrors the
/// gossip-layer ``GossipPacketSigner`` seam so the tip service does not take a hard dependency on the
/// concrete ``PacketSigningService`` actor.
public protocol TipPacketSigner: Sendable {
    /// Populate `packet.packetNonce`, `packet.timestampMs`, and `packet.signature` in place.
    func sign(packet: inout MeshPacket) async throws
}

/// Signs the tip payload's canonical bytes with the local node's Ed25519 identity key.
public protocol TipIdentitySigner: Sendable {
    /// Produce a 64-byte Ed25519 signature over `data` using the local identity key.
    func signData(_ data: Data) async throws -> Data
}

/// Resolves a next-hop toward a destination UHID. Returns the next-hop UHID when a route is known, or
/// `nil` to fall back to broadcast.
public protocol TipRouteResolver: Sendable {
    func findNextHop(destinationUhid: String) async -> String?
}

/// The host's settlement hook — the Swift analog of the C# `IAetherNetIncentiveProvider.SettleMeshTip`.
/// It receives the full signed ``TipPacketPayload`` off the mesh and decides how (if at all) to
/// interpret its value. The default no-op settles nothing.
public protocol MeshTipSettlementProvider: Sendable {
    /// Invoked for every inbound, well-formed tip payload. Implementations (e.g. SDPKT / BhenguPay)
    /// wire their wallet settlement here. A thrown error is logged by the caller but never propagated
    /// to the wire — a settlement failure must not break relaying.
    func settleMeshTip(_ payload: TipPacketPayload) async throws
}

/// Default no-op settlement provider — accepts the tip and settles nothing. A bare node carries the
/// tip signal but never moves value.
public struct NoopMeshTipSettlementProvider: MeshTipSettlementProvider {
    public init() {}
    public func settleMeshTip(_ payload: TipPacketPayload) async throws {}
}

// MARK: - Service

/// Builds, signs, sends, and handles mesh tip packets (``PacketType/tipPacket`` = 24).
///
/// Thread-safety: implemented as a Swift `actor`; all state mutations run on the actor's executor.
public actor MeshTipService {

    private let sender: any MeshSender
    private let signing: any TipPacketSigner
    private let identity: any TipIdentitySigner
    private let routing: (any TipRouteResolver)?
    private let settle: any MeshTipSettlementProvider

    /// TTL applied to every outbound tip (ProtocolConstants.defaultTtl).
    private static let defaultTtl: Int32 = ProtocolConstants.defaultTtl

    /// - Parameters:
    ///   - sender:   Mesh transport surface (unicast + broadcast).
    ///   - signing:  Enclosing-packet envelope signer.
    ///   - identity: Local Ed25519 identity signer for the payload's canonical bytes.
    ///   - routing:  Optional next-hop resolver. Pass `nil` to always broadcast.
    ///   - settle:   Optional settlement hook. Pass `nil` for the default no-op provider.
    public init(
        sender: any MeshSender,
        signing: any TipPacketSigner,
        identity: any TipIdentitySigner,
        routing: (any TipRouteResolver)? = nil,
        settle: (any MeshTipSettlementProvider)? = nil
    ) {
        self.sender = sender
        self.signing = signing
        self.identity = identity
        self.routing = routing
        self.settle = settle ?? NoopMeshTipSettlementProvider()
    }

    /// Builds, signs, and routes a `tipPacket` (24) addressed to `recipientUhid`. `amount` is the
    /// caller's input verbatim (the invariant decimal string) — the protocol imposes NO policy on it.
    /// It is signed into the payload and carried as-is. Returns the signed ``MeshPacket`` that was
    /// routed onto the mesh.
    @discardableResult
    public func sendTip(
        recipientUhid: String,
        amount: String,
        trafficType: String,
        referenceId: UUID? = nil,
        timestampUnixMs: Int64
    ) async throws -> MeshPacket {
        var payload = TipPacketPayload(
            tipperUhid: sender.localUhid,
            recipientUhid: recipientUhid,
            amount: amount,
            trafficType: trafficType,
            referenceId: referenceId,
            timestampUnixMs: timestampUnixMs
        )

        // Sign the payload's canonical bytes with the local identity key (real Ed25519).
        payload.signature = try await identity.signData(payload.buildCanonicalData())

        let body = try payload.toJSON()

        var packet = MeshPacket(
            type: .tipPacket,
            sourceUhid: sender.localUhid,
            destinationUhid: recipientUhid,
            ttl: Self.defaultTtl,
            priority: 0,
            payload: body
        )

        // Sign the enclosing MeshPacket (fills nonce/timestamp + envelope signature).
        try await signing.sign(packet: &packet)

        // Route toward the recipient: unicast over a discovered route, else broadcast.
        if let routing = routing, let nextHop = await routing.findNextHop(destinationUhid: recipientUhid) {
            _ = await sender.send(packet, nextHopUhid: nextHop)
            return packet
        }
        _ = await sender.broadcast(packet)
        return packet
    }

    /// Processes an inbound `tipPacket` (24) received off the mesh.
    ///
    /// Returns `true` when the payload was accepted and handed to the settlement provider.
    /// Returns `false` when the packet should be silently discarded (wrong type, malformed payload,
    /// missing/malformed signature).
    @discardableResult
    public func handleTipPacket(_ packet: MeshPacket) async -> Bool {
        guard packet.type == .tipPacket else { return false }

        // 1. Deserialise the payload. A malformed payload is dropped.
        guard let payload = try? TipPacketPayload.parse(packet.payload) else {
            return false
        }
        guard !payload.tipperUhid.isEmpty, !payload.recipientUhid.isEmpty else {
            return false
        }

        // 2. Best-effort signature check: an Ed25519 signature is exactly 64 bytes. A payload carrying
        //    no signature, or a malformed one, is unverifiable — dropped. The host's settlement
        //    provider is responsible for any stronger, key-bound verification it needs.
        guard let signature = payload.signature, signature.count == ed25519SignatureLength else {
            return false
        }

        // 3. Hand to the host's settlement provider. Default no-op settles nothing. A settlement error
        //    is swallowed but never breaks relaying.
        do {
            try await settle.settleMeshTip(payload)
        } catch {
            // Logged at the host level; relaying continues regardless.
        }

        // 4. Relay onward toward the addressed recipient if this node is not the destination and the
        //    packet may still be forwarded. The tip is ordinary addressed traffic.
        if packet.destinationUhid != sender.localUhid && packet.canForward {
            if let routing = routing, let nextHop = await routing.findNextHop(destinationUhid: packet.destinationUhid) {
                _ = await sender.send(packet, nextHopUhid: nextHop)
            } else {
                _ = await sender.broadcast(packet)
            }
        }

        return true
    }
}
