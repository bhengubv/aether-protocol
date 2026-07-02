// SPDX-License-Identifier: MIT

import Foundation

// ─── PreKeyBundleReceived ─────────────────────────────────

/// Event raised when a peer's ``PreKeyBundle`` arrives in a
/// ``PacketType/preKeyResponse``.
///
/// Mirrors C# `PreKeyBundleReceivedEventArgs`. Feed ``bundle`` to
/// ``SignalProtocolService/processPreKeyBundle(_:)`` to establish the X3DH
/// session — no key agreement happens in the exchange service itself.
public struct PreKeyBundleReceived: Sendable, Equatable {
    /// The request id echoed from the original ``PacketType/preKeyRequest``
    /// (all-zero UUID if unsolicited).
    public let requestId: UUID
    /// UHID of the peer that sent the bundle.
    public let fromUhid: String
    /// The received pre-key bundle.
    public let bundle: PreKeyBundle

    public init(requestId: UUID, fromUhid: String, bundle: PreKeyBundle) {
        self.requestId = requestId
        self.fromUhid = fromUhid
        self.bundle = bundle
    }
}

// ─── PreKeyExchangeService ────────────────────────────────

/// Mesh pre-key exchange over ``PacketType/preKeyRequest`` (25) and
/// ``PacketType/preKeyResponse`` (26) — directed request/response transport of a
/// ``PreKeyBundle`` so a peer can start an X3DH session while the other side is
/// offline.
///
/// A node publishes its current bundle via ``setLocalBundle(_:)`` (the host
/// produces it with ``SignalProtocolService/generatePreKeyBundle(localUhid:)``).
/// A peer asks for it with ``requestBundle(_:)``; the responder replies with its
/// bundle; the requester caches it and surfaces it via ``onBundleReceived``. This
/// is the mesh TRANSPORT of bundles only — the host performs the actual X3DH by
/// feeding the received bundle to ``SignalProtocolService/processPreKeyBundle(_:)``
/// (Signal-canonical: no key agreement happens here).
///
/// Directed request/response — never broadcast — so bundle requests do not leak
/// identity-interest to the whole mesh. Mirrors C# `PreKeyExchangeService`.
public actor PreKeyExchangeService {
    private let sender: any MeshSender

    private var local: PreKeyBundle?
    private var received: [String: PreKeyBundle] = [:]

    /// Raised when a peer's pre-key bundle arrives in a ``PacketType/preKeyResponse``.
    public var onBundleReceived: (@Sendable (PreKeyBundleReceived) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    public func setOnBundleReceived(_ callback: (@Sendable (PreKeyBundleReceived) -> Void)?) {
        onBundleReceived = callback
    }

    // MARK: – Local bundle

    /// Set (or replace) this node's published bundle — served in reply to inbound requests.
    public func setLocalBundle(_ bundle: PreKeyBundle) {
        local = bundle
    }

    /// The currently-published local bundle, or nil if none has been set.
    public func getLocalBundle() -> PreKeyBundle? {
        local
    }

    // MARK: – Requester

    /// Ask `peerUhid` for its pre-key bundle: mint a request id and directed-send a
    /// ``PacketType/preKeyRequest``. Returns the new request id (echoed by the response).
    @discardableResult
    public func requestBundle(_ peerUhid: String) async -> UUID {
        let requestId = UUID()
        guard !peerUhid.isEmpty else { return requestId }

        let body = encodePreKeyRequestWire(requestId: requestId, requesterUhid: sender.localUhid)
        let packet = MeshPacket(
            type: .preKeyRequest,
            sourceUhid: sender.localUhid,
            destinationUhid: peerUhid,
            ttl: ProtocolConstants.defaultTtl,
            payload: body
        )

        _ = await sender.send(packet, nextHopUhid: peerUhid)
        return requestId
    }

    /// The most recently received bundle for `uhid`, or nil.
    public func getReceivedBundle(_ uhid: String) -> PreKeyBundle? {
        received[uhid]
    }

    // MARK: – Inbound dispatch

    /// Process an incoming pre-key packet. On ``PacketType/preKeyRequest``, reply with
    /// the local bundle (if set) via a directed ``PacketType/preKeyResponse`` to the
    /// requester. On ``PacketType/preKeyResponse``, cache the peer bundle and fire
    /// ``onBundleReceived``. Returns false for the wrong packet type, a malformed
    /// payload, or a request received when no local bundle is set.
    @discardableResult
    public func handle(_ packet: MeshPacket) async -> Bool {
        switch packet.type {
        case .preKeyRequest:
            return await handleRequest(packet)
        case .preKeyResponse:
            return handleResponse(packet)
        default:
            return false
        }
    }

    private func handleRequest(_ packet: MeshPacket) async -> Bool {
        guard let req = parsePreKeyRequestWire(packet.payload) else { return false }
        guard let bundle = local else { return false }

        // Reply to the requester's advertised UHID, falling back to the packet source.
        let replyTo = !req.requesterUhid.isEmpty ? req.requesterUhid : packet.sourceUhid

        let body = encodePreKeyResponseWire(requestId: req.requestId, bundle: bundle)
        let reply = MeshPacket(
            type: .preKeyResponse,
            sourceUhid: sender.localUhid,
            destinationUhid: replyTo,
            ttl: ProtocolConstants.defaultTtl,
            payload: body
        )

        _ = await sender.send(reply, nextHopUhid: replyTo)
        return true
    }

    private func handleResponse(_ packet: MeshPacket) -> Bool {
        guard let resp = parsePreKeyResponseWire(packet.payload), !resp.bundle.uhid.isEmpty else {
            return false
        }

        received[resp.bundle.uhid] = resp.bundle
        onBundleReceived?(PreKeyBundleReceived(
            requestId: resp.requestId,
            fromUhid: packet.sourceUhid,
            bundle: resp.bundle
        ))
        return true
    }
}

// ─── PreKey exchange wire (PacketType 25 / 26) ───
//
// Two payloads, both UTF-8 JSON, snake_case, no whitespace, GUID lowercase-dashed,
// integer ids bare, and every byte-key field as STANDARD base64 (RFC 4648, '+/'
// alphabet, '=' padding — Foundation's `Data.base64EncodedString()` default).
//
// PreKeyRequest(25) field order: request_id, requester_uhid.
// PreKeyResponse(26) field order: request_id, uhid, identity_key, identity_key_x25519,
//   pre_key_id, pre_key, signed_pre_key_id, signed_pre_key, signed_pre_key_signature.
//
// This is the byte-identity gate (fixtures/prekey/vectors.json).

// Foundation's JSONEncoder does NOT emit keys in a deterministic declaration order — it
// hash-reorders them per process, breaking cross-language byte-identity. So the wire JSON
// is built by hand in the exact field order, mirroring the other language ports (and the
// Swift ChannelMessageService / VideoCallControlService). Decode still uses JSONDecoder
// below, which is order-independent.
private func jsonEscaped(_ s: String) -> String {
    var out = "\""
    for scalar in s.unicodeScalars {
        switch scalar {
        case "\"": out += "\\\""
        case "\\": out += "\\\\"
        case "\n": out += "\\n"
        case "\r": out += "\\r"
        case "\t": out += "\\t"
        default:
            if scalar.value < 0x20 { out += String(format: "\\u%04x", scalar.value) }
            else { out.unicodeScalars.append(scalar) }
        }
    }
    out += "\""
    return out
}

/// STANDARD base64 (RFC 4648, '+/' alphabet, '=' padding) — Foundation's default. Matches
/// System.Text.Json's `byte[]` encoding and every other language SDK.
private func base64(_ data: Data) -> String {
    data.base64EncodedString()
}

private func encodePreKeyRequestWire(requestId: UUID, requesterUhid: String) -> Data {
    let json = "{\"request_id\":\"\(requestId.uuidString.lowercased())\","
        + "\"requester_uhid\":\(jsonEscaped(requesterUhid))}"
    return Data(json.utf8)
}

private func encodePreKeyResponseWire(requestId: UUID, bundle: PreKeyBundle) -> Data {
    let json = "{\"request_id\":\"\(requestId.uuidString.lowercased())\","
        + "\"uhid\":\(jsonEscaped(bundle.uhid)),"
        + "\"identity_key\":\"\(base64(bundle.identityKey))\","
        + "\"identity_key_x25519\":\"\(base64(bundle.identityKeyX25519))\","
        + "\"pre_key_id\":\(bundle.preKeyId),"
        + "\"pre_key\":\"\(base64(bundle.preKey))\","
        + "\"signed_pre_key_id\":\(bundle.signedPreKeyId),"
        + "\"signed_pre_key\":\"\(base64(bundle.signedPreKey))\","
        + "\"signed_pre_key_signature\":\"\(base64(bundle.signedPreKeySignature))\"}"
    return Data(json.utf8)
}

// Order-independent parse of the inbound payloads. `Data` fields decode from base64
// automatically under JSONDecoder's default `.base64` data strategy.
private struct PreKeyRequestWire: Decodable {
    let request_id: UUID
    let requester_uhid: String
}

private struct PreKeyResponseWire: Decodable {
    let request_id: UUID
    let uhid: String
    let identity_key: Data
    let identity_key_x25519: Data
    let pre_key_id: Int32
    let pre_key: Data
    let signed_pre_key_id: Int32
    let signed_pre_key: Data
    let signed_pre_key_signature: Data
}

private func parsePreKeyRequestWire(_ data: Data) -> (requestId: UUID, requesterUhid: String)? {
    guard let w = try? JSONDecoder().decode(PreKeyRequestWire.self, from: data) else { return nil }
    return (w.request_id, w.requester_uhid)
}

private func parsePreKeyResponseWire(_ data: Data) -> (requestId: UUID, bundle: PreKeyBundle)? {
    guard let w = try? JSONDecoder().decode(PreKeyResponseWire.self, from: data) else { return nil }
    let bundle = PreKeyBundle(
        uhid: w.uhid,
        identityKey: w.identity_key,
        identityKeyX25519: w.identity_key_x25519,
        preKeyId: w.pre_key_id,
        preKey: w.pre_key,
        signedPreKeyId: w.signed_pre_key_id,
        signedPreKey: w.signed_pre_key,
        signedPreKeySignature: w.signed_pre_key_signature
    )
    return (w.request_id, bundle)
}

/// Test-only shims exposing the real wire serialization paths (the encoders stay
/// `private`) so the byte-identity vectors in `fixtures/prekey/vectors.json` can be verified.
internal func _preKeyRequestWireBytesForTests(requestId: UUID, requesterUhid: String) -> Data {
    encodePreKeyRequestWire(requestId: requestId, requesterUhid: requesterUhid)
}

internal func _preKeyResponseWireBytesForTests(requestId: UUID, bundle: PreKeyBundle) -> Data {
    encodePreKeyResponseWire(requestId: requestId, bundle: bundle)
}
