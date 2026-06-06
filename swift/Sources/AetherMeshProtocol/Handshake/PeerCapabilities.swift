// SPDX-License-Identifier: MIT

import Foundation

/// The negotiated protocol-version + capability set for a remote peer,
/// locked in once the Hello/HelloAck exchange completes (or after the
/// backward-compat fallback for peers that never replied).
///
/// The `negotiatedVersion` is the highest protocol version both sides
/// advertised support for. The `capabilities` set is the intersection of
/// both sides' advertised capability tags — services should gate optional
/// features (Double Ratchet, DTN custody, voice, etc.) on capability
/// presence rather than on raw protocol version.
public struct PeerCapabilities: Equatable, Sendable {
    /// UHID of the peer this record describes.
    public let peerUhid: String

    /// Highest mutually-supported protocol version. Defaults to `1` for peers
    /// that never replied with a HelloAck (backward-compat).
    public let negotiatedVersion: UInt8

    /// Intersection of capability tags both sides claim to support. Empty for
    /// peers that never replied.
    public let capabilities: Set<String>

    /// Free-form implementation banner the peer announced (e.g.
    /// `"aether-csharp/1.0.0"`). Empty for peers that never replied.
    public let implementationVersion: String

    /// UTC timestamp when negotiation completed.
    public let negotiatedAt: Date

    public init(
        peerUhid: String,
        negotiatedVersion: UInt8,
        capabilities: Set<String>,
        implementationVersion: String,
        negotiatedAt: Date
    ) {
        self.peerUhid = peerUhid
        self.negotiatedVersion = negotiatedVersion
        self.capabilities = capabilities
        self.implementationVersion = implementationVersion
        self.negotiatedAt = negotiatedAt
    }
}

/// Payload for the `IncompatiblePeer` event — fired when a peer's
/// announced version range does not overlap with ours and we cannot speak
/// to them.
///
/// Mirrors the C# `IncompatiblePeerEventArgs` class.
public struct IncompatiblePeerEvent: Equatable, Sendable {
    /// UHID of the incompatible peer.
    public let peerUhid: String

    /// Lowest version the peer claimed to support.
    public let theirMinVersion: UInt8

    /// Highest version the peer claimed to support.
    public let theirMaxVersion: UInt8

    /// Lowest version we accept.
    public let ourMinVersion: UInt8

    /// Highest version we speak.
    public let ourMaxVersion: UInt8

    /// Human-readable explanation for the mismatch.
    public let reason: String

    public init(
        peerUhid: String,
        theirMinVersion: UInt8,
        theirMaxVersion: UInt8,
        ourMinVersion: UInt8,
        ourMaxVersion: UInt8,
        reason: String
    ) {
        self.peerUhid = peerUhid
        self.theirMinVersion = theirMinVersion
        self.theirMaxVersion = theirMaxVersion
        self.ourMinVersion = ourMinVersion
        self.ourMaxVersion = ourMaxVersion
        self.reason = reason
    }
}
