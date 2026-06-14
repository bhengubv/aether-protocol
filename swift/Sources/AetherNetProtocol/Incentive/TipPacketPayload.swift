// SPDX-License-Identifier: MIT
//
// Generic "value-earned" relay-tip envelope carried inside a PacketType.tipPacket (24). Swift port of
// AetherNet.Incentive.TipPacketPayload, byte-identical to the C# reference and every other language
// implementation (Go, TypeScript, etc.).
//
// This model is deliberately value-agnostic. `amount` is a bare number with NO units, NO policy, and
// NO settlement semantics attached at the protocol layer. The protocol carries the signal that one
// node wishes to credit another for some kind of relayed traffic; what (if anything) that signal is
// worth is entirely the host's business. A bare node accepts and relays the packet but settles
// nothing — only a host that has wired a `MeshTipSettlementProvider` override decides how to interpret
// the value.
//
// The payload is self-signed by the tipper: `signature` is an Ed25519 signature over the canonical
// byte layout produced by `buildCanonicalData()`. The signature binds the tipper, recipient, amount,
// traffic type, reference, and timestamp together so an intermediate relay cannot tamper with any
// field without invalidating it.

import Foundation

/// The JSON body (snake_case) carried inside a `tipPacket` (24).
///
/// `amount` is the INVARIANT decimal string (the .NET `decimal.ToString(InvariantCulture)` round-trip
/// form, e.g. `"12.50"`, `"0.0001"`, `"123456.789"`) — NOT a `Double`/`Decimal`. Keeping it a `String`
/// is what makes the signed bytes stable across locales and decimal scales without baking in any unit
/// or fixed-point assumption, and is required for byte-identity with the C# canonical data.
public struct TipPacketPayload: Equatable {
    /// UHID of the node offering the tip (the signer of this payload).
    public var tipperUhid: String

    /// UHID of the node the tip is addressed to.
    public var recipientUhid: String

    /// Generic value being credited, carried verbatim as the invariant decimal string. The protocol
    /// imposes NO unit, NO minimum, NO maximum, and NO policy.
    public var amount: String

    /// Free-form tag describing the kind of relayed traffic this tip is for, e.g. `"message-relay"`
    /// or `"gateway-share"`. Opaque to the protocol.
    public var trafficType: String

    /// Optional correlation id linking this tip to some host-defined unit of work. `nil` when the tip
    /// stands alone (serialised as 16 zero bytes in the canonical data).
    public var referenceId: UUID?

    /// When the tipper created this payload, in Unix milliseconds (i64).
    public var timestampUnixMs: Int64

    /// Ed25519 signature over `buildCanonicalData()`, produced by the tipper's identity key. `nil`
    /// until the payload has been signed.
    public var signature: Data?

    public init(
        tipperUhid: String,
        recipientUhid: String,
        amount: String,
        trafficType: String,
        referenceId: UUID? = nil,
        timestampUnixMs: Int64,
        signature: Data? = nil
    ) {
        self.tipperUhid = tipperUhid
        self.recipientUhid = recipientUhid
        self.amount = amount
        self.trafficType = trafficType
        self.referenceId = referenceId
        self.timestampUnixMs = timestampUnixMs
        self.signature = signature
    }

    /// Builds the canonical byte array that is signed/verified for this payload. The `signature` field
    /// itself is excluded from the canonical data.
    ///
    /// Layout (little-endian lengths, matching `PacketSigningService.constructSignableData`
    /// conventions):
    ///
    ///     TipperLen(4 LE i32)    || Tipper(UTF-8)
    ///     RecipientLen(4 LE i32) || Recipient(UTF-8)
    ///     AmountLen(4 LE i32)    || Amount(UTF-8, invariant round-trip decimal string)
    ///     TrafficLen(4 LE i32)   || TrafficType(UTF-8)
    ///     ReferenceId(16, all-zero GUID when nil, .NET mixed-endian byte order)
    ///     TimestampUnixMs(8 LE i64)
    public func buildCanonicalData() -> Data {
        let tipperBytes = Data(tipperUhid.utf8)
        let recipientBytes = Data(recipientUhid.utf8)
        let amountBytes = Data(amount.utf8)
        let trafficBytes = Data(trafficType.utf8)

        var buffer = Data()
        buffer.reserveCapacity(
            4 + tipperBytes.count
                + 4 + recipientBytes.count
                + 4 + amountBytes.count
                + 4 + trafficBytes.count
                + 16  // ReferenceId GUID
                + 8   // Timestamp (i64 LE)
        )

        TipCanonicalEncoding.appendLengthPrefixed(&buffer, tipperBytes)
        TipCanonicalEncoding.appendLengthPrefixed(&buffer, recipientBytes)
        TipCanonicalEncoding.appendLengthPrefixed(&buffer, amountBytes)
        TipCanonicalEncoding.appendLengthPrefixed(&buffer, trafficBytes)

        // ReferenceId — 16 bytes, all-zero when nil, .NET GUID byte order otherwise.
        if let referenceId {
            buffer.append(TipCanonicalEncoding.guidBytesDotNet(referenceId))
        } else {
            buffer.append(Data(repeating: 0, count: 16))
        }

        // Timestamp — Unix milliseconds, little-endian int64.
        TipCanonicalEncoding.appendInt64LE(&buffer, timestampUnixMs)

        return buffer
    }

    // MARK: - JSON wire form

    /// snake_case wire shape (UTF-8 JSON) matching the C# serializer. `timestamp` is a bare JSON
    /// integer; `reference_id` is the hyphenated GUID string (omitted when nil); `signature` is
    /// Base64 (omitted until signed).
    private struct Wire: Codable {
        var tipperUhid: String
        var recipientUhid: String
        var amount: String
        var trafficType: String
        var referenceId: String?
        var timestamp: Int64
        var signature: Data?

        enum CodingKeys: String, CodingKey {
            case tipperUhid = "tipper_uhid"
            case recipientUhid = "recipient_uhid"
            case amount
            case trafficType = "traffic_type"
            case referenceId = "reference_id"
            case timestamp
            case signature
        }
    }

    /// Serialises the payload to its snake_case UTF-8 JSON wire form.
    public func toJSON() throws -> Data {
        let wire = Wire(
            tipperUhid: tipperUhid,
            recipientUhid: recipientUhid,
            amount: amount,
            trafficType: trafficType,
            referenceId: referenceId.map { Self.lowercasedUuidString($0) },
            timestamp: timestampUnixMs,
            signature: signature
        )
        // JSONEncoder encodes Data as Base64 by default (matches C# byte[] -> Base64 JSON).
        return try JSONEncoder().encode(wire)
    }

    /// Deserialises a snake_case UTF-8 JSON tip payload.
    public static func parse(_ data: Data) throws -> TipPacketPayload {
        let wire = try JSONDecoder().decode(Wire.self, from: data)
        return TipPacketPayload(
            tipperUhid: wire.tipperUhid,
            recipientUhid: wire.recipientUhid,
            amount: wire.amount,
            trafficType: wire.trafficType,
            referenceId: wire.referenceId.flatMap { UUID(uuidString: $0) },
            timestampUnixMs: wire.timestamp,
            signature: wire.signature
        )
    }

    /// .NET `Guid.ToString()` is lowercase hyphenated; mirror that on the wire so the JSON form is
    /// byte-identical to C#.
    private static func lowercasedUuidString(_ id: UUID) -> String {
        id.uuidString.lowercased()
    }
}

/// Shared little-endian / .NET-GUID byte-packing helpers for the canonical tip layout. Kept free of
/// host-endianness assumptions: every multi-byte integer is emitted explicitly low-byte-first.
enum TipCanonicalEncoding {
    /// Appends a 4-byte LE int32 length prefix followed by `value`.
    static func appendLengthPrefixed(_ buffer: inout Data, _ value: Data) {
        appendInt32LE(&buffer, Int32(value.count))
        buffer.append(value)
    }

    /// Appends `value` as 4 little-endian bytes (explicit packing — independent of host endianness).
    static func appendInt32LE(_ buffer: inout Data, _ value: Int32) {
        let v = UInt32(bitPattern: value)
        buffer.append(UInt8(v & 0xFF))
        buffer.append(UInt8((v >> 8) & 0xFF))
        buffer.append(UInt8((v >> 16) & 0xFF))
        buffer.append(UInt8((v >> 24) & 0xFF))
    }

    /// Appends `value` as 8 little-endian bytes (explicit packing — independent of host endianness).
    static func appendInt64LE(_ buffer: inout Data, _ value: Int64) {
        let v = UInt64(bitPattern: value)
        for shift in stride(from: 0, through: 56, by: 8) {
            buffer.append(UInt8((v >> UInt64(shift)) & 0xFF))
        }
    }

    /// Returns the 16-byte .NET in-memory representation of a UUID, which is what
    /// `System.Guid.TryWriteBytes` produces. Swift `UUID.uuid` exposes the bytes in big-endian
    /// (RFC 4122) order; .NET stores the first three groups little-endian (Data1: 4 bytes, Data2:
    /// 2 bytes, Data3: 2 bytes) and the final 8 bytes as-is. This mixed-endian layout is required for
    /// byte-identity with the C# canonical data.
    static func guidBytesDotNet(_ id: UUID) -> Data {
        let u = id.uuid // 16-tuple in RFC 4122 (big-endian) order.
        var out = [UInt8](repeating: 0, count: 16)
        // Data1 (bytes 0..3) — reversed.
        out[0] = u.3; out[1] = u.2; out[2] = u.1; out[3] = u.0
        // Data2 (bytes 4..5) — reversed.
        out[4] = u.5; out[5] = u.4
        // Data3 (bytes 6..7) — reversed.
        out[6] = u.7; out[7] = u.6
        // Data4 (bytes 8..15) — as-is.
        out[8] = u.8; out[9] = u.9; out[10] = u.10; out[11] = u.11
        out[12] = u.12; out[13] = u.13; out[14] = u.14; out[15] = u.15
        return Data(out)
    }
}
