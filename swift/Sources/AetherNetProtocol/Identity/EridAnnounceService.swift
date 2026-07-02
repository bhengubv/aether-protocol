// SPDX-License-Identifier: MIT

import Foundation

// ─── EridAnnounceReceived ─────────────────────────────────────────────────

/// Event surfaced when an ERID announcement arrives from a peer (payload still encrypted).
///
/// The ``encryptedAnnouncement`` is a Signal `EncryptedPayload` whose plaintext is an
/// ``EridAnnouncementCodec`` frame — decrypting + parsing it is the host's concern. Mirrors C#
/// `EridAnnounceReceived`.
public struct EridAnnounceReceived: Sendable, Equatable {
    /// The packet body — a Signal-encrypted announcement (opaque to this service).
    public let encryptedAnnouncement: Data
    /// UHID of the peer that sent the announcement.
    public let fromUhid: String

    public init(encryptedAnnouncement: Data, fromUhid: String) {
        self.encryptedAnnouncement = encryptedAnnouncement
        self.fromUhid = fromUhid
    }
}

// ─── EridAnnounceService ──────────────────────────────────────────────────

/// Binds ``PacketType/eridAnnounce`` (PacketType 56) to the mesh: a node shares its rotating-address
/// routing key with an established peer by sending the (already Signal-encrypted) announcement
/// directly.
///
/// Transport only — the plaintext framing (``EridAnnouncementCodec``) and the encryption are done
/// by the host/EridExchangeService; this service just carries the opaque encrypted blob as a
/// directed packet and surfaces inbound ones via ``onAnnounceReceived``. Mirrors C#
/// `EridAnnounceService`.
public actor EridAnnounceService {
    private let sender: any MeshSender

    /// Raised when an ERID announcement arrives from a peer (payload still encrypted).
    public var onAnnounceReceived: (@Sendable (EridAnnounceReceived) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
    }

    public func setOnAnnounceReceived(_ callback: (@Sendable (EridAnnounceReceived) -> Void)?) {
        onAnnounceReceived = callback
    }

    // MARK: – Send

    /// Send an encrypted ERID announcement directly to `peerUhid`. Returns delivery success.
    /// No-op (returns false) for an empty peer UHID or an empty announcement body.
    @discardableResult
    public func sendAnnounce(_ peerUhid: String, encrypted: Data) async -> Bool {
        guard !peerUhid.isEmpty, !encrypted.isEmpty else { return false }

        let packet = MeshPacket(
            type: .eridAnnounce,
            sourceUhid: sender.localUhid,
            destinationUhid: peerUhid,
            ttl: ProtocolConstants.defaultTtl,
            payload: encrypted
        )
        return await sender.send(packet, nextHopUhid: peerUhid)
    }

    // MARK: – Inbound dispatch

    /// Process an incoming ``PacketType/eridAnnounce`` packet: fire ``onAnnounceReceived``.
    /// Returns false for the wrong packet type or an empty body, true once the event has been
    /// surfaced.
    @discardableResult
    public func handle(_ packet: MeshPacket) async -> Bool {
        guard packet.type == .eridAnnounce else { return false }
        guard !packet.payload.isEmpty else { return false }

        onAnnounceReceived?(EridAnnounceReceived(
            encryptedAnnouncement: packet.payload,
            fromUhid: packet.sourceUhid
        ))
        return true
    }
}
