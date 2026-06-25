// SPDX-License-Identifier: MIT

import Foundation

/// Canonical binary DTN envelope (wire format v1). Byte-identical across all
/// eight AetherNet SDKs; the Go encoder (go/cmd/dtnfixturegen) is the oracle and
/// the fixtures/dtn/expected .bin vectors pin the bytes.
///
/// Every multi-byte integer is LITTLE-ENDIAN, except the 16-byte bundle id which
/// is the raw RFC-4122 big-endian UUID (Swift's `UUID.uuid` tuple is already in
/// that order). Cleartext routing fields come first and the opaque
/// `encryptedPayload` is last, so the future T1 privacy bump can move
/// sender/recipient into the ciphertext without a re-layout.
enum DtnEnvelope {
    static let version: UInt8 = 0x01
    static let maxPayload = 16 * 1024 * 1024  // AETHERNET_MAX_PAYLOAD_LEN

    // MARK: - DtnBundle

    static func serializeBundle(_ b: DtnBundle) -> Data {
        var out = Data()
        out.append(version)
        out.append(uuidBytes(b.id))
        out.append(UInt8(truncatingIfNeeded: b.priority))
        out.append(UInt8(truncatingIfNeeded: b.status))
        appendI32(&out, b.copyCount)
        appendI32(&out, b.maxCopies)
        appendI32(&out, b.hopCount)
        appendI64(&out, Int64(b.createdAt.timeIntervalSince1970 * 1000))
        appendI64(&out, Int64(b.expiresAt.timeIntervalSince1970 * 1000))
        appendStr(&out, b.senderUhid)
        appendStr(&out, b.recipientUhid)
        appendStr(&out, b.senderGeohash ?? "")
        appendStr(&out, b.recipientLastGeohash ?? "")
        appendBytes32(&out, b.encryptedPayload)
        return out
    }

    static func deserializeBundle(_ data: Data) -> DtnBundle? {
        var r = Reader(data)
        guard r.version(),
              let id = r.uuid(),
              let priority = r.u8(), priority <= 3,
              let status = r.u8(), status <= 4,
              let copyCount = r.i32(),
              let maxCopies = r.i32(),
              let hopCount = r.i32(),
              let createdMs = r.i64(),
              let expiresMs = r.i64(),
              let senderUhid = r.str(),
              let recipientUhid = r.str(),
              let senderGeohash = r.str(),
              let recipientLastGeohash = r.str(),
              let payload = r.bytes32()
        else { return nil }
        return DtnBundle(
            id: id,
            senderUhid: senderUhid,
            recipientUhid: recipientUhid,
            encryptedPayload: payload,
            priority: Int32(priority),
            status: Int32(status),
            copyCount: copyCount,
            maxCopies: maxCopies,
            senderGeohash: senderGeohash.isEmpty ? nil : senderGeohash,
            recipientLastGeohash: recipientLastGeohash.isEmpty ? nil : recipientLastGeohash,
            hopCount: hopCount,
            createdAt: Date(timeIntervalSince1970: Double(createdMs) / 1000),
            expiresAt: Date(timeIntervalSince1970: Double(expiresMs) / 1000)
        )
    }

    // MARK: - CustodyAck (18 bytes fixed)

    static func serializeCustodyAck(bundleId: UUID, accepted: Bool) -> Data {
        var out = Data()
        out.append(version)
        out.append(uuidBytes(bundleId))
        out.append(accepted ? 1 : 0)
        return out
    }

    static func deserializeCustodyAck(_ data: Data) -> (UUID, Bool)? {
        var r = Reader(data)
        guard r.version(), let id = r.uuid(), let acc = r.u8() else { return nil }
        return (id, acc != 0)
    }

    // MARK: - DeliveryReceipt

    static func serializeDeliveryReceipt(_ receipt: DtnDeliveryReceipt) -> Data {
        var out = Data()
        out.append(version)
        out.append(uuidBytes(receipt.bundleId))
        appendStr(&out, receipt.recipientUhid)
        appendI32(&out, receipt.totalHops)
        appendI32(&out, receipt.totalCustodyTransfers)
        appendI64(&out, Int64(receipt.deliveredAt.timeIntervalSince1970 * 1000))
        return out
    }

    static func deserializeDeliveryReceipt(_ data: Data) -> DtnDeliveryReceipt? {
        var r = Reader(data)
        guard r.version(),
              let id = r.uuid(),
              let recipient = r.str(),
              let hops = r.i32(),
              let transfers = r.i32(),
              let deliveredMs = r.i64()
        else { return nil }
        return DtnDeliveryReceipt(
            bundleId: id,
            recipientUhid: recipient,
            totalHops: hops,
            totalCustodyTransfers: transfers,
            deliveredAt: Date(timeIntervalSince1970: Double(deliveredMs) / 1000)
        )
    }

    // MARK: - primitives

    /// 16 bytes, RFC-4122 big-endian (the `uuid_t` tuple is already in that order).
    private static func uuidBytes(_ id: UUID) -> Data {
        var u = id.uuid
        return withUnsafeBytes(of: &u) { Data($0) }
    }

    private static func appendI32(_ out: inout Data, _ v: Int32) {
        let u = UInt32(bitPattern: v)
        out.append(UInt8(u & 0xff))
        out.append(UInt8((u >> 8) & 0xff))
        out.append(UInt8((u >> 16) & 0xff))
        out.append(UInt8((u >> 24) & 0xff))
    }

    private static func appendI64(_ out: inout Data, _ v: Int64) {
        let u = UInt64(bitPattern: v)
        for i in 0..<8 { out.append(UInt8((u >> (8 * i)) & 0xff)) }
    }

    private static func appendU16(_ out: inout Data, _ v: Int) {
        out.append(UInt8(v & 0xff))
        out.append(UInt8((v >> 8) & 0xff))
    }

    private static func appendStr(_ out: inout Data, _ s: String) {
        let bytes = Array(s.utf8)
        let n = min(bytes.count, 0xFFFF)
        appendU16(&out, n)
        out.append(contentsOf: bytes[0..<n])
    }

    private static func appendBytes32(_ out: inout Data, _ b: Data) {
        appendI32(&out, Int32(truncatingIfNeeded: b.count))
        out.append(b)
    }

    private struct Reader {
        let d: [UInt8]
        var o = 0

        init(_ data: Data) { d = Array(data) }

        mutating func version() -> Bool {
            guard let v = u8() else { return false }
            return v == DtnEnvelope.version
        }

        mutating func u8() -> UInt8? {
            guard o + 1 <= d.count else { return nil }
            defer { o += 1 }
            return d[o]
        }

        mutating func uuid() -> UUID? {
            guard o + 16 <= d.count else { return nil }
            let t: uuid_t = (
                d[o], d[o + 1], d[o + 2], d[o + 3], d[o + 4], d[o + 5], d[o + 6], d[o + 7],
                d[o + 8], d[o + 9], d[o + 10], d[o + 11], d[o + 12], d[o + 13], d[o + 14], d[o + 15]
            )
            o += 16
            return UUID(uuid: t)
        }

        mutating func i32() -> Int32? {
            guard o + 4 <= d.count else { return nil }
            let u = UInt32(d[o]) | (UInt32(d[o + 1]) << 8) | (UInt32(d[o + 2]) << 16) | (UInt32(d[o + 3]) << 24)
            o += 4
            return Int32(bitPattern: u)
        }

        mutating func i64() -> Int64? {
            guard o + 8 <= d.count else { return nil }
            var u: UInt64 = 0
            for i in 0..<8 { u |= UInt64(d[o + i]) << (8 * i) }
            o += 8
            return Int64(bitPattern: u)
        }

        mutating func u16() -> Int? {
            guard o + 2 <= d.count else { return nil }
            let v = Int(d[o]) | (Int(d[o + 1]) << 8)
            o += 2
            return v
        }

        mutating func str() -> String? {
            guard let n = u16(), o + n <= d.count else { return nil }
            let s = String(decoding: d[o..<o + n], as: UTF8.self)
            o += n
            return s
        }

        mutating func bytes32() -> Data? {
            guard let n = i32() else { return nil }
            let len = Int(n)
            guard n >= 0, len <= DtnEnvelope.maxPayload, o + len <= d.count else { return nil }
            let slice = Data(d[o..<o + len])
            o += len
            return slice
        }
    }
}
