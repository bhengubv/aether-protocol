// SPDX-License-Identifier: MIT

import Foundation
import XCTest
@testable import AetherNetProtocol

/// Cross-language multi-device-sync parity: drives the Swift port through
/// `fixtures/sync/vectors.json` and asserts byte-for-byte against the C#
/// reference (`src/AetherNet.Security/Sync/`) and every other AetherNet SDK.
///
/// Three components:
///  * `SyncRecord` binary envelope — serialize hex + deserialize round-trip.
///  * Reconcile (deterministic last-write-wins) — the winning record id, and the
///    same winner regardless of input order.
///  * `DeviceLink` — signed-body bytes, deterministic Ed25519 signature, full
///    serialized bytes, verify true for the right identity / false for the wrong
///    one, and a deserialize round-trip.
final class SyncFixtureTests: XCTestCase {

    // MARK: - fixture model

    private struct Vectors: Decodable {
        let identity_private: String
        let identity_public: String
        let wrong_identity_public: String
        let sync_records: [SyncRecordVector]
        let reconcile: [ReconcileVector]
        let device_links: [DeviceLinkVector]
    }

    private struct SyncRecordVector: Decodable {
        let record_id: String
        let device_id: String
        let op: UInt8
        let item_id: String
        let logical_clock: Int64
        let created_at_ms: Int64
        let payload_hex: String
        let serialized_hex: String
    }

    private struct ReconcileRecord: Decodable {
        let record_id: String
        let device_id: String
        let item_id: String
        let op: UInt8
        let logical_clock: Int64
        let created_at_ms: Int64
        let payload_hex: String
    }

    private struct ReconcileVector: Decodable {
        let name: String
        let records: [ReconcileRecord]
        let winner_record_id: String
    }

    private struct DeviceLinkVector: Decodable {
        let device_id: String
        let device_public_key: String
        let issued_at_ms: Int64
        let signed_body_hex: String
        let signature_hex: String
        let serialized_hex: String
    }

    // MARK: - loading / hex

    /// `#filePath` = .../swift/Tests/SyncFixtureTests.swift → repo root is three levels up.
    private func repoRoot() -> URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // .../swift/Tests
            .deletingLastPathComponent()  // .../swift
            .deletingLastPathComponent()  // repo root
    }

    private func loadVectors() throws -> Vectors {
        let url = repoRoot().appendingPathComponent("fixtures/sync/vectors.json")
        return try JSONDecoder().decode(Vectors.self, from: Data(contentsOf: url))
    }

    private func hexDecode(_ hex: String) -> Data {
        var data = Data(capacity: hex.count / 2)
        var idx = hex.startIndex
        while idx < hex.endIndex {
            let next = hex.index(idx, offsetBy: 2)
            data.append(UInt8(hex[idx..<next], radix: 16)!)
            idx = next
        }
        return data
    }

    private func hexEncode(_ data: Data) -> String {
        data.map { String(format: "%02x", $0) }.joined()
    }

    // MARK: - builders from fixture rows

    private func record(_ v: SyncRecordVector) -> SyncRecord {
        SyncRecord(
            recordIdString: v.record_id,
            deviceId: v.device_id,
            op: SyncOp(rawValue: v.op)!,
            itemId: v.item_id,
            logicalClock: v.logical_clock,
            createdAtMs: v.created_at_ms,
            encryptedPayload: hexDecode(v.payload_hex))!
    }

    private func record(_ v: ReconcileRecord) -> SyncRecord {
        SyncRecord(
            recordIdString: v.record_id,
            deviceId: v.device_id,
            op: SyncOp(rawValue: v.op)!,
            itemId: v.item_id,
            logicalClock: v.logical_clock,
            createdAtMs: v.created_at_ms,
            encryptedPayload: hexDecode(v.payload_hex))!
    }

    // MARK: - SyncRecord envelope

    func testSyncRecordSerializeAndRoundTrip() throws {
        let corpus = try loadVectors()
        XCTAssertFalse(corpus.sync_records.isEmpty)

        for v in corpus.sync_records {
            let rec = record(v)

            // serialize → exact fixture bytes
            let bytes = try SyncRecordSerializer.serialize(rec)
            XCTAssertEqual(hexEncode(bytes), v.serialized_hex,
                           "serialize mismatch for record \(v.record_id)")

            // deserialize → identical fields (round-trip from the fixture bytes)
            let back = try SyncRecordSerializer.deserialize(hexDecode(v.serialized_hex))
            XCTAssertEqual(back.recordId, rec.recordId, "\(v.record_id) recordId")
            XCTAssertEqual(back.recordIdString, v.record_id, "\(v.record_id) recordIdString")
            XCTAssertEqual(back.deviceId, v.device_id, "\(v.record_id) deviceId")
            XCTAssertEqual(back.op, SyncOp(rawValue: v.op)!, "\(v.record_id) op")
            XCTAssertEqual(back.itemId, v.item_id, "\(v.record_id) itemId")
            XCTAssertEqual(back.logicalClock, v.logical_clock, "\(v.record_id) logicalClock")
            XCTAssertEqual(back.createdAtMs, v.created_at_ms, "\(v.record_id) createdAtMs")
            XCTAssertEqual(back.encryptedPayload, hexDecode(v.payload_hex), "\(v.record_id) payload")
            XCTAssertEqual(back, rec, "\(v.record_id) full round-trip")
        }
    }

    // MARK: - Reconcile (deterministic last-write-wins)

    func testReconcileWinnerIsDeterministic() throws {
        let corpus = try loadVectors()
        XCTAssertFalse(corpus.reconcile.isEmpty)

        for v in corpus.reconcile {
            let records = v.records.map(record)

            // forward order
            let winner = SyncReconciler.winner(records)
            XCTAssertNotNil(winner, "\(v.name): no winner")
            XCTAssertEqual(winner?.recordIdString, v.winner_record_id,
                           "\(v.name): winner mismatch")

            // reversed order → identical winner (order-independence)
            let reversedWinner = SyncReconciler.winner(records.reversed())
            XCTAssertEqual(reversedWinner?.recordIdString, v.winner_record_id,
                           "\(v.name): reversed winner mismatch")

            // merge keys the winner under its itemId
            let merged = SyncReconciler.merge(records)
            let itemId = v.records.first!.item_id
            XCTAssertEqual(merged[itemId]?.recordIdString, v.winner_record_id,
                           "\(v.name): merge winner mismatch")
        }
    }

    // MARK: - DeviceLink

    func testDeviceLinkSignVerifyAndRoundTrip() throws {
        let corpus = try loadVectors()
        XCTAssertFalse(corpus.device_links.isEmpty)

        let identitySeed = hexDecode(corpus.identity_private)
        let identityPublic = hexDecode(corpus.identity_public)
        let wrongPublic = hexDecode(corpus.wrong_identity_public)

        for v in corpus.device_links {
            let devicePublicKey = hexDecode(v.device_public_key)

            // signed body → exact fixture bytes
            let body = try DeviceLinkCodec.signedBody(
                deviceId: v.device_id,
                devicePublicKey: devicePublicKey,
                issuedAtMs: v.issued_at_ms)
            XCTAssertEqual(hexEncode(body), v.signed_body_hex,
                           "\(v.device_id): signed body mismatch")

            // create → a VALID signature. NOTE: swift-crypto / Apple CryptoKit
            // produces *randomized* Ed25519 signatures, unlike the deterministic
            // RFC-8032 libraries the other 7 SDKs use (libsodium, ed25519-dalek,
            // NSec, tweetnacl, cryptography, Go stdlib, JDK). So Swift's signature
            // bytes are valid but do NOT equal the fixture's — parity here is
            // *verification*, not signature bytes. (The signed body above IS
            // byte-identical, and every link cross-verifies on every SDK.)
            let link = try DeviceLinkCodec.create(
                deviceId: v.device_id,
                devicePublicKey: devicePublicKey,
                issuedAtMs: v.issued_at_ms,
                identitySeed: identitySeed)
            XCTAssertEqual(link.signature.count, 64, "\(v.device_id): signature length")
            XCTAssertTrue(DeviceLinkCodec.verify(link, identityPublicKey: identityPublic),
                          "\(v.device_id): Swift-signed link must verify under the signing identity")
            XCTAssertFalse(DeviceLinkCodec.verify(link, identityPublicKey: wrongPublic),
                           "\(v.device_id): must NOT verify under a different identity")

            // Swift's own serialize → deserialize round-trips and still verifies.
            let reSwift = try DeviceLinkCodec.deserialize(try DeviceLinkCodec.serialize(link))
            XCTAssertEqual(reSwift.deviceId, v.device_id)
            XCTAssertEqual(reSwift.devicePublicKey, devicePublicKey)
            XCTAssertEqual(reSwift.issuedAtMs, v.issued_at_ms)
            XCTAssertTrue(DeviceLinkCodec.verify(reSwift, identityPublicKey: identityPublic))

            // Cross-SDK: the deterministic fixture link (produced by C#/Python/etc.)
            // deserializes byte-for-byte and VERIFIES in Swift.
            let back = try DeviceLinkCodec.deserialize(hexDecode(v.serialized_hex))
            XCTAssertEqual(back.deviceId, v.device_id, "\(v.device_id): deviceId")
            XCTAssertEqual(back.devicePublicKey, devicePublicKey, "\(v.device_id): devicePublicKey")
            XCTAssertEqual(back.issuedAtMs, v.issued_at_ms, "\(v.device_id): issuedAtMs")
            XCTAssertEqual(hexEncode(back.signature), v.signature_hex, "\(v.device_id): fixture signature preserved")
            XCTAssertTrue(DeviceLinkCodec.verify(back, identityPublicKey: identityPublic),
                          "\(v.device_id): cross-SDK fixture link must verify in Swift")
            XCTAssertFalse(DeviceLinkCodec.verify(back, identityPublicKey: wrongPublic),
                           "\(v.device_id): fixture link must NOT verify under a different identity")
        }
    }
}
