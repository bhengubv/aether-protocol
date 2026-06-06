// SPDX-License-Identifier: MIT

import Foundation

/// Protocol-version + capability negotiation service.
///
/// Peers exchange a `PacketType.hello` / `PacketType.helloAck` pair on
/// first contact: each side announces the protocol-version range it can
/// speak and the capability tags it supports; the receiver replies with the
/// highest mutually-supported version + the intersection of capability
/// tags. Once locked in, subsequent traffic is gated against this record.
///
/// Wire flow:
///
///     A → B   Hello       { min:1, max:2, caps:[X,Y,Z], impl:"…" }
///     A ← B   HelloAck    { min:1, max:2, caps:[X,Y],   impl:"…" }
///
/// Negotiation rules:
///   - Negotiated version = `min(ourMax, theirMax)`
///   - If `min(ourMax,theirMax) < max(ourMin,theirMin)` the ranges do not
///     overlap → fire `incompatiblePeer`, refuse to lock in.
///   - Locked-in capability set = `ourCaps ∩ theirCaps`
///
/// The handshake itself is unencrypted and unauthenticated — it runs before
/// any Signal session exists. Peer identity is verified later via Ed25519
/// packet signatures on data packets. The capability set must therefore be
/// treated as a hint, not as an authenticated claim.
///
/// Backward-compat: a peer that never replies with a HelloAck is assumed to
/// be running protocol version 1 with no advertised capabilities. Hosts
/// drive the timeout via `assumeLegacyV1(peerUhid:)`.
public actor HandshakeService {
    /// Default capability tags advertised by this implementation. Mirrors
    /// the C# `HandshakeService.DefaultCapabilities` set so we always
    /// announce the same baseline.
    public static let defaultCapabilities: Set<String> = [
        "signal-x3dh",
        "double-ratchet",
        "dtn-custody",
        "sos",
        "voice",
        "stream",
    ]

    /// Default implementation banner emitted in our Hello/HelloAck.
    public static let defaultImplementation: String = "aether-swift/1.0.0"

    /// Default highest protocol version we speak. Matches
    /// `ProtocolConstants.protocolVersionSigned` (the current signed-packet
    /// version) so newly negotiated peers run on the signed wire by default.
    public static var defaultMaxVersion: UInt8 {
        ProtocolConstants.protocolVersionSigned
    }

    private let sender: MeshSender
    private let ourMinVersion: UInt8
    private let ourMaxVersion: UInt8
    private let ourCapabilities: Set<String>
    private let ourImplementation: String

    /// Peers we've already sent a Hello to, to suppress duplicate sends.
    private var helloSent: Set<String> = []

    /// Peers we've finished negotiating with.
    private var negotiated: [String: PeerCapabilities] = [:]

    /// Listeners for the negotiated event (analogue of the C# event handler).
    /// Hosts subscribe via `addPeerNegotiatedListener` to be notified when a
    /// handshake locks in for a peer.
    private var negotiatedListeners: [(@Sendable (PeerCapabilities) -> Void)] = []

    /// Listeners for the incompatible-peer event.
    private var incompatibleListeners: [(@Sendable (IncompatiblePeerEvent) -> Void)] = []

    /// Construct a handshake service. Defaults match this codebase: we speak
    /// versions 1..`ProtocolConstants.protocolVersionSigned` and advertise
    /// `defaultCapabilities`.
    public init(
        sender: MeshSender,
        ourMinVersion: UInt8 = 1,
        ourMaxVersion: UInt8? = nil,
        ourCapabilities: Set<String>? = nil,
        ourImplementation: String = HandshakeService.defaultImplementation
    ) {
        self.sender = sender
        self.ourMinVersion = ourMinVersion
        let maxVer = ourMaxVersion ?? HandshakeService.defaultMaxVersion
        precondition(
            ourMinVersion <= maxVer,
            "ourMinVersion (\(ourMinVersion)) cannot exceed ourMaxVersion (\(maxVer))."
        )
        self.ourMaxVersion = maxVer
        self.ourCapabilities = ourCapabilities ?? HandshakeService.defaultCapabilities
        self.ourImplementation = ourImplementation
    }

    /// Subscribe to the peer-negotiated event. Closures fire once the
    /// Hello/HelloAck handshake locks in (either via a real reply or via
    /// `assumeLegacyV1`).
    public func addPeerNegotiatedListener(_ listener: @escaping @Sendable (PeerCapabilities) -> Void) {
        negotiatedListeners.append(listener)
    }

    /// Subscribe to the incompatible-peer event. Closures fire when a peer's
    /// announced version range does not overlap with ours.
    public func addIncompatiblePeerListener(_ listener: @escaping @Sendable (IncompatiblePeerEvent) -> Void) {
        incompatibleListeners.append(listener)
    }

    /// Initiate a Hello towards a freshly discovered peer. No-op if a Hello
    /// has already been sent to this peer in the current session — re-broadcasts
    /// can cause duplicate Hellos otherwise.
    public func initiate(peerUhid: String) async {
        guard !peerUhid.isEmpty else { return }
        if peerUhid == sender.localUhid { return }

        // Suppress duplicate Hellos.
        if helloSent.contains(peerUhid) { return }
        helloSent.insert(peerUhid)

        let hello = buildPacket(type: .hello, destinationUhid: peerUhid)
        _ = await sender.send(hello, nextHopUhid: peerUhid)
    }

    /// Handle an inbound `PacketType.hello`: lock in their announced
    /// capabilities and reply with a HelloAck. Throws on packet-type mismatch.
    public func handleHello(_ helloPacket: MeshPacket) async throws {
        guard helloPacket.type == .hello else {
            throw HandshakeError.unexpectedPacketType(expected: .hello, actual: helloPacket.type)
        }
        if helloPacket.sourceUhid.isEmpty { return }
        if helloPacket.sourceUhid == sender.localUhid { return }

        guard let theirs = HelloPayload.fromJsonBytes(helloPacket.payload) else {
            // Malformed payload — drop, matching the C# warn-and-ignore path.
            return
        }

        guard let negotiation = tryNegotiate(peerUhid: helloPacket.sourceUhid, theirs: theirs) else {
            return // incompatiblePeer already fired
        }

        negotiated[helloPacket.sourceUhid] = negotiation
        firePeerNegotiated(negotiation)

        // Reply with HelloAck — even if we already sent them an unprompted
        // Hello, the spec is symmetric and the ack carries our own range/caps.
        let ack = buildPacket(type: .helloAck, destinationUhid: helloPacket.sourceUhid)
        _ = await sender.send(ack, nextHopUhid: helloPacket.sourceUhid)
    }

    /// Handle an inbound `PacketType.helloAck`: lock in the negotiated
    /// capabilities for the replying peer. Throws on packet-type mismatch.
    public func handleHelloAck(_ helloAckPacket: MeshPacket) throws {
        guard helloAckPacket.type == .helloAck else {
            throw HandshakeError.unexpectedPacketType(expected: .helloAck, actual: helloAckPacket.type)
        }
        if helloAckPacket.sourceUhid.isEmpty { return }
        if helloAckPacket.sourceUhid == sender.localUhid { return }

        guard let theirs = HelloPayload.fromJsonBytes(helloAckPacket.payload) else {
            return
        }

        guard let negotiation = tryNegotiate(peerUhid: helloAckPacket.sourceUhid, theirs: theirs) else {
            return
        }

        negotiated[helloAckPacket.sourceUhid] = negotiation
        firePeerNegotiated(negotiation)
    }

    /// Look up the locked-in capabilities for a peer. Returns nil if the
    /// handshake has not yet completed.
    public func getPeerCapabilities(peerUhid: String) -> PeerCapabilities? {
        return negotiated[peerUhid]
    }

    /// Drop a peer's cached capabilities and re-issue a Hello on the next
    /// outbound contact. Used when a version-mismatch is detected in
    /// subsequent traffic.
    public func renegotiate(peerUhid: String) {
        negotiated.removeValue(forKey: peerUhid)
        helloSent.remove(peerUhid)
    }

    /// Snapshot of every peer that has finished negotiating, for diagnostics.
    public func getAllNegotiated() -> [PeerCapabilities] {
        return Array(negotiated.values)
    }

    /// Backward-compat: install a "v1, no caps" record for a peer that never
    /// replied to our Hello within the timeout window. Hosts call this from
    /// their own timer / heartbeat loop. Idempotent — if the peer has since
    /// replied with a HelloAck, the existing record wins.
    public func assumeLegacyV1(peerUhid: String) {
        guard !peerUhid.isEmpty else { return }
        if peerUhid == sender.localUhid { return }
        if negotiated[peerUhid] != nil { return }

        let fallback = PeerCapabilities(
            peerUhid: peerUhid,
            negotiatedVersion: 1,
            capabilities: [],
            implementationVersion: "",
            negotiatedAt: Date()
        )
        negotiated[peerUhid] = fallback
        firePeerNegotiated(fallback)
    }

    // MARK: - Private

    private func buildPacket(type: PacketType, destinationUhid: String) -> MeshPacket {
        let payload = HelloPayload(
            minVersion: ourMinVersion,
            maxVersion: ourMaxVersion,
            capabilities: Array(ourCapabilities),
            implementation: ourImplementation
        )
        // toJsonBytes() can't realistically throw for our four-field shape
        // (no Data fields, no enums with associated values). Fall back to
        // an empty payload on the impossible path so we don't crash.
        let payloadBytes = (try? payload.toJsonBytes()) ?? Data()

        return MeshPacket(
            type: type,
            sourceUhid: sender.localUhid,
            destinationUhid: destinationUhid,
            ttl: 1, // direct hop only — handshake never relays
            priority: 0,
            payload: payloadBytes,
            protocolVersion: ourMaxVersion
        )
    }

    /// Returns the negotiated peer-capabilities record on success, or nil if
    /// the peer is incompatible (in which case `incompatiblePeer` has been
    /// fired with the reason).
    private func tryNegotiate(peerUhid: String, theirs: HelloPayload) -> PeerCapabilities? {
        if theirs.minVersion > theirs.maxVersion {
            fireIncompatible(peerUhid: peerUhid, theirs: theirs, reason: "inverted version range")
            return nil
        }

        // Overlap check: highest min must be ≤ lowest max.
        let overlapMin = max(ourMinVersion, theirs.minVersion)
        let overlapMax = min(ourMaxVersion, theirs.maxVersion)
        if overlapMin > overlapMax {
            let reason =
                "no version overlap (ours=\(ourMinVersion)..\(ourMaxVersion), " +
                "theirs=\(theirs.minVersion)..\(theirs.maxVersion))"
            fireIncompatible(peerUhid: peerUhid, theirs: theirs, reason: reason)
            return nil
        }

        // Pick the highest mutually-supported version.
        let chosenVersion = overlapMax

        // Capability intersection (case-sensitive — capability names are
        // wire constants).
        var intersection = Set<String>()
        for cap in theirs.capabilities where !cap.isEmpty && ourCapabilities.contains(cap) {
            intersection.insert(cap)
        }

        return PeerCapabilities(
            peerUhid: peerUhid,
            negotiatedVersion: chosenVersion,
            capabilities: intersection,
            implementationVersion: theirs.implementation,
            negotiatedAt: Date()
        )
    }

    private func firePeerNegotiated(_ caps: PeerCapabilities) {
        for listener in negotiatedListeners {
            listener(caps)
        }
    }

    private func fireIncompatible(peerUhid: String, theirs: HelloPayload, reason: String) {
        let event = IncompatiblePeerEvent(
            peerUhid: peerUhid,
            theirMinVersion: theirs.minVersion,
            theirMaxVersion: theirs.maxVersion,
            ourMinVersion: ourMinVersion,
            ourMaxVersion: ourMaxVersion,
            reason: reason
        )
        for listener in incompatibleListeners {
            listener(event)
        }
    }
}

public enum HandshakeError: Error, Equatable {
    case unexpectedPacketType(expected: PacketType, actual: PacketType)
}
