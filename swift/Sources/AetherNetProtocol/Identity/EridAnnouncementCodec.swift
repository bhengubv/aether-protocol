// SPDX-License-Identifier: MIT

import Foundation

/// Frames the in-session ERID announcement — the message a node sends a peer INSIDE an
/// established Signal session to share its secret `routingKey` (plus the rotation
/// parameters it uses), so the peer can resolve its rotating wire address via
/// ``EridDirectory``.
///
/// The bytes are carried *encrypted* by the Signal session, so this is framing only — no
/// encryption of its own. A 4-byte magic sentinel + version lets a receiver tell an ERID
/// announcement apart from other in-session application data before trying to parse it.
///
/// Layout: magic `AERD` (4) + version (1) + epochSeconds (Int32 BE) + eridLength (Int32 BE)
/// + routingKeyLen (Int32 BE) + routingKey. Integer fields big-endian so every language
/// port frames byte-identically. Port of the C# reference.
public enum EridAnnouncementCodec {

    /// A decoded in-session ERID announcement.
    public struct Announcement: Equatable {
        public let routingKey: [UInt8]
        public let epochSeconds: Int32
        public let eridLength: Int32

        public init(routingKey: [UInt8], epochSeconds: Int32, eridLength: Int32) {
            self.routingKey = routingKey
            self.epochSeconds = epochSeconds
            self.eridLength = eridLength
        }
    }

    public enum CodecError: Error, Equatable {
        case emptyRoutingKey
        case invalidEpochSeconds
        case invalidLength
    }

    // 'A' 'E' 'R' 'D' — "AetherNet ERID Directory announcement".
    private static let magic: [UInt8] = [0x41, 0x45, 0x52, 0x44]
    private static let version: UInt8 = 1
    // magic(4) + version(1) + epochSeconds(4) + eridLength(4) + routingKeyLen(4).
    private static let headerLength = 17

    /// Frame an announcement carrying `routingKey` and the rotation params.
    /// - Throws: ``CodecError`` if any field is out of range.
    public static func encode(
        _ routingKey: [UInt8],
        epochSeconds: Int32 = Int32(EphemeralRoutingId.defaultEpochSeconds),
        eridLength: Int32 = Int32(EphemeralRoutingId.defaultLength)
    ) throws -> [UInt8] {
        guard !routingKey.isEmpty else { throw CodecError.emptyRoutingKey }
        guard epochSeconds > 0 else { throw CodecError.invalidEpochSeconds }
        guard eridLength >= 1 && eridLength <= 51 else { throw CodecError.invalidLength }

        var buf = [UInt8]()
        buf.reserveCapacity(headerLength + routingKey.count)
        buf.append(contentsOf: magic)
        buf.append(version)
        buf.append(contentsOf: int32BE(epochSeconds))
        buf.append(contentsOf: int32BE(eridLength))
        buf.append(contentsOf: int32BE(Int32(routingKey.count)))
        buf.append(contentsOf: routingKey)
        return buf
    }

    /// Parse an announcement. Returns `nil` (rather than throwing) when the bytes are not
    /// a well-formed ERID announcement, so a receiver can cheaply test an arbitrary
    /// decrypted in-session payload against the magic.
    public static func tryDecode(_ data: [UInt8]) -> Announcement? {
        guard data.count >= headerLength else { return nil }
        guard Array(data[0..<4]) == magic else { return nil }
        guard data[4] == version else { return nil }

        let epochSeconds = readInt32BE(data, 5)
        let eridLength = readInt32BE(data, 9)
        let keyLen = readInt32BE(data, 13)

        guard epochSeconds > 0 else { return nil }
        guard eridLength >= 1 && eridLength <= 51 else { return nil }
        guard keyLen > 0 && headerLength + Int(keyLen) <= data.count else { return nil }

        let key = Array(data[headerLength..<(headerLength + Int(keyLen))])
        return Announcement(routingKey: key, epochSeconds: epochSeconds, eridLength: eridLength)
    }

    // MARK: - Private

    private static func int32BE(_ value: Int32) -> [UInt8] {
        let u = UInt32(bitPattern: value)
        return [
            UInt8((u >> 24) & 0xFF), UInt8((u >> 16) & 0xFF),
            UInt8((u >> 8) & 0xFF), UInt8(u & 0xFF),
        ]
    }

    private static func readInt32BE(_ data: [UInt8], _ offset: Int) -> Int32 {
        let u = (UInt32(data[offset]) << 24) | (UInt32(data[offset + 1]) << 16)
            | (UInt32(data[offset + 2]) << 8) | UInt32(data[offset + 3])
        return Int32(bitPattern: u)
    }
}
