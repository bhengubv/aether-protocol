// SPDX-License-Identifier: MIT

import Foundation

/// A peer (or local) profile exchanged over ``PacketType/profileSync`` (PacketType 23).
///
/// Mirrors C# `ProfileSyncPayload`. All string fields are always present (empty when unset) — no
/// nulls — so the wire encoding cannot diverge across languages. The `uhid` is self-identifying so
/// a cached profile stays attributable to its owner.
public struct ProfileSnapshot: Sendable, Equatable {
    /// UHID this profile describes (the sender).
    public let uhid: String
    /// Human-readable display name (empty if unset).
    public let displayName: String
    /// Content-addressed reference to an avatar (e.g. "blake3:…"), empty if none.
    public let avatarRef: String
    /// Free-text status / presence message (empty if unset).
    public let statusMessage: String
    /// Unix timestamp in milliseconds when the profile was last updated by its owner.
    public let updatedAtMs: Int64

    public init(uhid: String, displayName: String, avatarRef: String, statusMessage: String, updatedAtMs: Int64) {
        self.uhid = uhid
        self.displayName = displayName
        self.avatarRef = avatarRef
        self.statusMessage = statusMessage
        self.updatedAtMs = updatedAtMs
    }
}

/// Exchanges peer profile metadata over ``PacketType/profileSync``.
///
/// Profiles are shared **directed** (point-to-point to a specific peer), NOT broadcast, for privacy:
/// broadcasting display names to every device in range is exactly the metadata leak the privacy
/// roadmap forbids. A peer you interact with learns your profile; strangers do not. Received profiles
/// are cached (keyed by their `uhid`) and surfaced via ``onProfileUpdated``.
///
/// Mirrors C# `ProfileService`.
public actor ProfileService {
    private let sender: any MeshSender

    private var local: ProfileSnapshot
    private var peerProfiles: [String: ProfileSnapshot] = [:]

    /// Raised when a peer's profile is received or refreshed.
    public var onProfileUpdated: (@Sendable (ProfileSnapshot) -> Void)?

    public init(sender: any MeshSender) {
        self.sender = sender
        self.local = ProfileSnapshot(
            uhid: sender.localUhid,
            displayName: "",
            avatarRef: "",
            statusMessage: "",
            updatedAtMs: 0
        )
    }

    public func setOnProfileUpdated(_ callback: (@Sendable (ProfileSnapshot) -> Void)?) {
        onProfileUpdated = callback
    }

    /// Set this node's own profile (stamps `updatedAtMs` to now).
    public func setLocalProfile(displayName: String, avatarRef: String, statusMessage: String) {
        local = ProfileSnapshot(
            uhid: sender.localUhid,
            displayName: displayName,
            avatarRef: avatarRef,
            statusMessage: statusMessage,
            updatedAtMs: Int64(Date().timeIntervalSince1970 * 1000)
        )
    }

    /// This node's current local profile.
    public func getLocalProfile() -> ProfileSnapshot {
        local
    }

    /// Send this node's local profile directly to `peerUhid`. Best-effort; returns delivery success.
    /// No-op (returns false) for an empty peer uhid.
    @discardableResult
    public func publishProfileTo(_ peerUhid: String) async -> Bool {
        guard !peerUhid.isEmpty else { return false }

        let body = encodeProfileSyncWire(
            uhid: local.uhid,
            displayName: local.displayName,
            avatarRef: local.avatarRef,
            statusMessage: local.statusMessage,
            updatedAtMs: local.updatedAtMs
        )

        let packet = MeshPacket(
            type: .profileSync,
            sourceUhid: sender.localUhid,
            destinationUhid: peerUhid,
            ttl: ProtocolConstants.defaultTtl,
            payload: body
        )

        return await sender.send(packet, nextHopUhid: peerUhid)
    }

    /// Process an incoming ``PacketType/profileSync`` packet: cache the sender's profile (keyed by its
    /// `uhid`) and fire ``onProfileUpdated``. Returns false for the wrong packet type, a malformed
    /// payload, or our own profile echoed back.
    @discardableResult
    public func handle(_ packet: MeshPacket) async -> Bool {
        guard packet.type == .profileSync else { return false }

        guard let body = parseProfileSyncWire(packet.payload), !body.uhid.isEmpty else {
            return false
        }

        // Ignore our own profile echoed back.
        if body.uhid == sender.localUhid { return false }

        peerProfiles[body.uhid] = body
        onProfileUpdated?(body)
        return true
    }

    /// The cached profile for `uhid`, or nil if none is known.
    public func getProfile(_ uhid: String) -> ProfileSnapshot? {
        peerProfiles[uhid]
    }

    /// Snapshot of every peer profile this node has cached.
    public func getKnownProfiles() -> [ProfileSnapshot] {
        Array(peerProfiles.values)
    }
}

// ─── ProfileSync wire (PacketType 23) ───
//
// Serialises to snake_case keys, field order uhid, display_name, avatar_ref, status_message,
// updated_at_ms, no whitespace, updated_at_ms a bare integer, all string fields always present
// (empty when unset). This is the byte-identity gate (fixtures/profiles/vectors.json).

private struct ProfileSyncWire: Codable {
    let uhid: String
    let display_name: String
    let avatar_ref: String
    let status_message: String
    let updated_at_ms: Int64
}

// Foundation's JSONEncoder does NOT emit keys in a deterministic declaration order — with 3+
// fields it hash-reorders them, breaking cross-language byte-identity. So the wire JSON is built
// by hand in the exact field order, mirroring the other language ports. (Decode still uses
// JSONDecoder above, which is order-independent.)
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

private func encodeProfileSyncWire(
    uhid: String,
    displayName: String,
    avatarRef: String,
    statusMessage: String,
    updatedAtMs: Int64
) -> Data {
    let json = "{\"uhid\":\(jsonEscaped(uhid)),"
        + "\"display_name\":\(jsonEscaped(displayName)),"
        + "\"avatar_ref\":\(jsonEscaped(avatarRef)),"
        + "\"status_message\":\(jsonEscaped(statusMessage)),"
        + "\"updated_at_ms\":\(updatedAtMs)}"
    return Data(json.utf8)
}

private func parseProfileSyncWire(
    _ data: Data
) -> ProfileSnapshot? {
    guard let w = try? JSONDecoder().decode(ProfileSyncWire.self, from: data) else { return nil }
    return ProfileSnapshot(
        uhid: w.uhid,
        displayName: w.display_name,
        avatarRef: w.avatar_ref,
        statusMessage: w.status_message,
        updatedAtMs: w.updated_at_ms
    )
}

/// Test-only shim exposing the real ``ProfileSyncWire`` serialization path (the struct itself stays
/// `private`) so byte-identity vectors in `fixtures/profiles/vectors.json` can be verified.
internal func _profileSyncWireBytesForTests(
    uhid: String,
    displayName: String,
    avatarRef: String,
    statusMessage: String,
    updatedAtMs: Int64
) -> Data {
    encodeProfileSyncWire(
        uhid: uhid,
        displayName: displayName,
        avatarRef: avatarRef,
        statusMessage: statusMessage,
        updatedAtMs: updatedAtMs
    )
}
