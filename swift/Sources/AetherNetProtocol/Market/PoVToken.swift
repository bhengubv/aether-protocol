// SPDX-License-Identifier: MIT
//
// Proof-of-Vicinity token model and canonical signable-body codec. Swift port of
// AetherNet.Market.Models.PoVToken / PoVTransportType / PoVScore and AetherNet.Market.PoVTokenCodec,
// byte-identical to the C# reference and every other language implementation (Go, TypeScript, etc.).
//
// The canonical body that BOTH the witness and the subject sign with their real Ed25519 identity keys
// must stay byte-identical across every language implementation so a token signed by one node
// verifies on any other:
//
//   SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1 byte)
//
// timestamp_ticks is .NET DateTime.Ticks (100ns intervals since 0001-01-01).

import Foundation

/// Transport used for a co-presence Proof-of-Vicinity exchange. Only short-range transports are valid
/// (prevents remote minting). Raw values are the wire bytes: ble=0, nfc=1, nearlink=2.
public enum PoVTransportType: UInt8, Codable, Equatable {
    /// Bluetooth Low Energy (short range — prevents remote forgery).
    case ble = 0
    /// Near-Field Communication (requires physical proximity).
    case nfc = 1
    /// Huawei NearLink (short range, similar to BLE).
    case nearLink = 2

    /// Whether this transport is a valid short-range PoV channel.
    public var isShortRange: Bool {
        switch self {
        case .ble, .nfc, .nearLink: return true
        }
    }

    /// Lowercase wire name of the transport.
    public var wireName: String {
        switch self {
        case .ble: return "ble"
        case .nfc: return "nfc"
        case .nearLink: return "nearlink"
        }
    }
}

/// Number of .NET DateTime ticks (100ns) per second.
public let ticksPerSecond: Int64 = 10_000_000

/// The .NET DateTime.Ticks value at the Unix epoch (1970-01-01T00:00:00Z), i.e. ticks between
/// 0001-01-01 and 1970-01-01. Used to convert between .NET ticks and a Swift `Date`.
public let unixEpochTicks: Int64 = 621_355_968_000_000_000

/// A Proof-of-Vicinity token issued by one node (the witness) to another (the subject) during a
/// physical co-presence event. Both parties must countersign — this prevents unilateral forgery. The
/// token is transmitted over a short-range transport (BLE/NFC/NearLink only) to prevent remote
/// minting. The JSON wire form is snake_case, matching the C# serializer.
public struct PoVToken: Equatable {
    /// UHID of the node issuing the voucher.
    public var witnessUhid: String

    /// UHID of the node being vouched for.
    public var subjectUhid: String

    /// Co-presence event time as .NET DateTime.Ticks (100ns since 0001-01-01). Stored as ticks (an
    /// `Int64`, not a Swift `Date`) so the signed canonical body is byte-identical to C#.
    public var timestampTicks: Int64

    /// Transport channel used (must be short-range).
    public var transportUsed: PoVTransportType

    /// Ed25519 signature by the witness over the canonical body, or `nil`.
    public var witnessSignature: Data?

    /// Ed25519 countersignature by the subject — required for token validity, or `nil` until set.
    public var subjectSignature: Data?

    public init(
        witnessUhid: String,
        subjectUhid: String,
        timestampTicks: Int64,
        transportUsed: PoVTransportType,
        witnessSignature: Data? = nil,
        subjectSignature: Data? = nil
    ) {
        self.witnessUhid = witnessUhid
        self.subjectUhid = subjectUhid
        self.timestampTicks = timestampTicks
        self.transportUsed = transportUsed
        self.witnessSignature = witnessSignature
        self.subjectSignature = subjectSignature
    }

    /// Returns the canonical signable bytes for this token.
    public func signableData() -> Data {
        PoVTokenCodec.buildSignableTokenData(
            subjectUhid: subjectUhid,
            timestampTicks: timestampTicks,
            transport: transportUsed
        )
    }

    // MARK: - JSON wire form

    /// snake_case wire shape (UTF-8 JSON) matching the C# serializer. `transport_used` is the raw
    /// transport byte; signatures are Base64 (omitted when absent).
    private struct Wire: Codable {
        var witnessUhid: String
        var subjectUhid: String
        var timestampTicks: Int64
        var transportUsed: UInt8
        var witnessSignature: Data?
        var subjectSignature: Data?

        enum CodingKeys: String, CodingKey {
            case witnessUhid = "witness_uhid"
            case subjectUhid = "subject_uhid"
            case timestampTicks = "timestamp_ticks"
            case transportUsed = "transport_used"
            case witnessSignature = "witness_signature"
            case subjectSignature = "subject_signature"
        }
    }

    /// Serialises the token to its snake_case UTF-8 JSON wire form.
    public func toJSON() throws -> Data {
        let wire = Wire(
            witnessUhid: witnessUhid,
            subjectUhid: subjectUhid,
            timestampTicks: timestampTicks,
            transportUsed: transportUsed.rawValue,
            witnessSignature: witnessSignature,
            subjectSignature: subjectSignature
        )
        return try JSONEncoder().encode(wire)
    }

    /// Errors raised while parsing a PoV token wire body.
    public enum ParseError: Error, Equatable {
        /// `transport_used` carried a byte outside the valid short-range set {0, 1, 2}. A transport a
        /// closed Swift enum cannot represent is treated as malformed (the C#/Go `IsShortRange`
        /// `default` arm refuses the same value), so the token is rejected rather than silently
        /// coerced.
        case unknownTransport(UInt8)
    }

    /// Deserialises a snake_case UTF-8 JSON PoV token. Throws ``ParseError/unknownTransport(_:)`` if
    /// the transport byte is not one of the valid short-range values, so a malformed token is dropped
    /// by the receive path rather than misinterpreted.
    public static func parse(_ data: Data) throws -> PoVToken {
        let wire = try JSONDecoder().decode(Wire.self, from: data)
        guard let transport = PoVTransportType(rawValue: wire.transportUsed) else {
            throw ParseError.unknownTransport(wire.transportUsed)
        }
        return PoVToken(
            witnessUhid: wire.witnessUhid,
            subjectUhid: wire.subjectUhid,
            timestampTicks: wire.timestampTicks,
            transportUsed: transport,
            witnessSignature: wire.witnessSignature,
            subjectSignature: wire.subjectSignature
        )
    }
}

/// Canonical byte layout for the content of a ``PoVToken`` that BOTH the witness and the subject sign
/// with their real Ed25519 identity keys.
///
///     SubjectLen(4 LE i32) || Subject(UTF-8) || TimestampTicks(8 LE i64) || Transport(1 byte)
public enum PoVTokenCodec {
    /// Builds the canonical signable bytes for a PoV token body. The same layout is signed by the
    /// witness (on issue) and counter-signed by the subject (on accept). Multi-byte integers are
    /// packed explicitly little-endian, independent of host endianness.
    public static func buildSignableTokenData(
        subjectUhid: String,
        timestampTicks: Int64,
        transport: PoVTransportType
    ) -> Data {
        let subjectBytes = Data(subjectUhid.utf8)

        var data = Data()
        data.reserveCapacity(4 + subjectBytes.count + 8 + 1)

        // SubjectLen — 4-byte LE int32.
        let len = UInt32(bitPattern: Int32(subjectBytes.count))
        data.append(UInt8(len & 0xFF))
        data.append(UInt8((len >> 8) & 0xFF))
        data.append(UInt8((len >> 16) & 0xFF))
        data.append(UInt8((len >> 24) & 0xFF))

        // Subject (UTF-8).
        data.append(subjectBytes)

        // TimestampTicks — 8-byte LE int64 (full i64; .NET ticks exceed 2^32).
        let t = UInt64(bitPattern: timestampTicks)
        for shift in stride(from: 0, through: 56, by: 8) {
            data.append(UInt8((t >> UInt64(shift)) & 0xFF))
        }

        // Transport — 1 byte.
        data.append(transport.rawValue)

        return data
    }
}

/// The Proof-of-Vicinity trust score for a node — a purely local anti-Sybil routing/identity signal
/// that attaches NO value semantics.
public struct PoVScore: Equatable {
    /// UHID of the scored node.
    public var uhid: String
    /// Number of distinct witnesses who have issued PoV tokens to this node.
    public var uniqueWitnesses: Int
    /// Weighted score (0.0–1.0).
    public var weightedScore: Double
    /// Time of the most recent score update.
    public var lastUpdated: Date

    public init(uhid: String, uniqueWitnesses: Int, weightedScore: Double, lastUpdated: Date) {
        self.uhid = uhid
        self.uniqueWitnesses = uniqueWitnesses
        self.weightedScore = weightedScore
        self.lastUpdated = lastUpdated
    }
}

/// Converts a .NET DateTime.Ticks value to a Swift `Date`. Provided for hosts that want a `Date`.
///
/// PRECISION: `Date` is internally a `Double` of seconds, so it CANNOT hold the full 100ns (i64-ticks)
/// resolution at modern timestamps — a ticks → `Date` → ticks round-trip may differ by a few ticks.
/// This is a fundamental limitation of `Date`, NOT of the protocol: the canonical signable body always
/// uses the raw `Int64` ticks (see ``PoVToken/timestampTicks`` and ``PoVTokenCodec``), so signature
/// parity is unaffected. Use the raw ticks, not this `Date`, anywhere byte-exactness matters.
public func povTicksToDate(_ ticks: Int64) -> Date {
    let unixTicks = ticks - unixEpochTicks
    let seconds = Double(unixTicks) / Double(ticksPerSecond)
    return Date(timeIntervalSince1970: seconds)
}

/// Converts a Swift `Date` to a .NET DateTime.Ticks value. Subject to the same `Date`-precision caveat
/// as ``povTicksToDate(_:)`` — exact only to the resolution `Date`'s `Double` seconds can represent.
public func povDateToTicks(_ date: Date) -> Int64 {
    let unixSeconds = date.timeIntervalSince1970
    return Int64((unixSeconds * Double(ticksPerSecond)).rounded()) + unixEpochTicks
}
