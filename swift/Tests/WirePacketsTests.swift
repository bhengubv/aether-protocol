// SPDX-License-Identifier: MIT
import XCTest
import Foundation
@testable import AetherNetProtocol

/// Unit tests for the Phase-2 WIRE bindings: SpaceBreadcrumb(40), ForgeAnnounce(41),
/// VaultShardRequest(42). Byte-identity gates + broadcast/handle behaviour. Mirrors the
/// C# `WirePacketsTests`. A shared ``FakeMeshSender`` captures broadcasts — no transport
/// needed; its `broadcasts()` is synchronous and returns the recorded packets.
///
/// Byte-identity is checked two ways: (1) inline vectors matching the C# `[Fact]`s, and
/// (2) every vector in the SHARED `fixtures/{space,forge,vaultshard}/vectors.json`, located
/// by walking up from `#file`. The fixtures are canonical and cross-language — never edited.
final class WirePacketsTests: XCTestCase {

    // Two peers so FakeMeshSender.broadcast() returns 2 (matches the C# fake's fixed 2).
    private func senderWith2Peers(localUhid: String) -> FakeMeshSender {
        let s = FakeMeshSender(localUhid: localUhid)
        s.addPeer(PeerInfo(uhid: "aether:peer:a"))
        s.addPeer(PeerInfo(uhid: "aether:peer:b"))
        return s
    }

    /// Locate the repo-root `fixtures/` dir by walking up from THIS source file.
    private func fixturesDir() -> URL {
        var url = URL(fileURLWithPath: #file)
        for _ in 0..<10 {
            let candidate = url.appendingPathComponent("fixtures/space/vectors.json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                return url.appendingPathComponent("fixtures")
            }
            let parent = url.deletingLastPathComponent()
            if parent.path == url.path { break }
            url = parent
        }
        XCTFail("Could not locate fixtures/space/vectors.json by walking up from \(#file)")
        return URL(fileURLWithPath: "fixtures")
    }

    private func loadVectors(_ subdir: String) throws -> [[String: Any]] {
        let url = fixturesDir().appendingPathComponent("\(subdir)/vectors.json")
        let data = try Data(contentsOf: url)
        let obj = try JSONSerialization.jsonObject(with: data) as? [String: Any]
        return (obj?["vectors"] as? [[String: Any]]) ?? []
    }

    // ── SpaceBreadcrumb (40) ────────────────────────────────────────────────

    func test_spaceBreadcrumb_emergency_serializesToCanonicalBytes() {
        let crumb = SpaceBreadcrumb(
            contentHash: "QmContentHashExample123",
            geoHash: "u4pruy",
            anchorUhid: "aether:alice:01",
            createdAtUtc: Date(timeIntervalSince1970: 1_700_000_000.0),
            ttlHours: 720,
            type: .emergency,
            signature: Data(repeating: 0x99, count: 64)
        )
        let json = String(data: _spaceBreadcrumbWireBytesForTests(crumb), encoding: .utf8)
        XCTAssertEqual(
            json,
            "{\"content_hash\":\"QmContentHashExample123\",\"geo_hash\":\"u4pruy\",\"anchor_uhid\":\"aether:alice:01\","
            + "\"created_at_ms\":1700000000000,\"ttl_hours\":720,\"type\":1,"
            + "\"signature\":\"mZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmQ==\"}"
        )
    }

    func test_spaceBreadcrumb_noticeUnsigned_serializesToCanonicalBytes() {
        let crumb = SpaceBreadcrumb(
            contentHash: "QmNotice777",
            geoHash: "gcpvj0",
            anchorUhid: "aether:bob:02",
            createdAtUtc: Date(timeIntervalSince1970: 0),
            ttlHours: 72,
            type: .notice,
            signature: Data()
        )
        let json = String(data: _spaceBreadcrumbWireBytesForTests(crumb), encoding: .utf8)
        XCTAssertEqual(
            json,
            "{\"content_hash\":\"QmNotice777\",\"geo_hash\":\"gcpvj0\",\"anchor_uhid\":\"aether:bob:02\","
            + "\"created_at_ms\":0,\"ttl_hours\":72,\"type\":0,\"signature\":\"\"}"
        )
    }

    func test_spaceBreadcrumb_allFixtureVectors_serializeExactBytes() throws {
        for v in try loadVectors("space") {
            let name = v["name"] as? String ?? "?"
            let sigB64 = v["signature"] as? String ?? ""
            let crumb = SpaceBreadcrumb(
                contentHash: v["content_hash"] as? String ?? "",
                geoHash: v["geo_hash"] as? String ?? "",
                anchorUhid: v["anchor_uhid"] as? String ?? "",
                createdAtUtc: Date(timeIntervalSince1970: Double(v["created_at_ms"] as? Int64 ?? Int64(v["created_at_ms"] as? Int ?? 0)) / 1000.0),
                ttlHours: v["ttl_hours"] as? Int ?? 0,
                type: BreadcrumbType(rawValue: UInt8(truncatingIfNeeded: v["type"] as? Int ?? 0)) ?? .notice,
                signature: Data(base64Encoded: sigB64) ?? Data()
            )
            let json = String(data: _spaceBreadcrumbWireBytesForTests(crumb), encoding: .utf8)
            XCTAssertEqual(json, v["expected_json"] as? String, "space vector \(name)")
        }
    }

    func test_space_broadcast_emitsBreadcrumbPacket_andHandleRaisesEvent() async {
        let sender = senderWith2Peers(localUhid: "aether:alice:01")
        let svc = SpaceBreadcrumbService(sender: sender)

        let crumb = SpaceBreadcrumb(
            contentHash: "QmX",
            geoHash: "u4pruy",
            anchorUhid: "aether:alice:01",
            createdAtUtc: Date(timeIntervalSince1970: 1_700_000_000.0),
            ttlHours: 720,
            type: .emergency,
            signature: Data(repeating: 0x99, count: 64)
        )
        let reached = await svc.broadcast(crumb)
        XCTAssertEqual(reached, 2)

        let broadcasts = sender.broadcasts()
        XCTAssertEqual(broadcasts.count, 1)
        let sent = broadcasts[0]
        XCTAssertEqual(sent.type, .spaceBreadcrumb)
        XCTAssertEqual(sent.destinationUhid, "*")
        XCTAssertEqual(sent.ttl, ProtocolConstants.defaultTtl)

        let got = Locked<SpaceBreadcrumb?>(nil)
        await svc.setOnBreadcrumbReceived { got.value = $0 }
        let ok = await svc.handle(sent)
        XCTAssertTrue(ok)

        let received = got.value
        XCTAssertNotNil(received)
        XCTAssertEqual(received?.geoHash, "u4pruy")
        XCTAssertEqual(received?.type, .emergency)
        XCTAssertEqual(received?.ttlHours, 720)
        XCTAssertEqual(received?.signature.count, 64)
    }

    func test_space_handle_wrongType_returnsFalse() async {
        let svc = SpaceBreadcrumbService(sender: FakeMeshSender(localUhid: "local"))
        let ok = await svc.handle(MeshPacket(type: .data, payload: Data()))
        XCTAssertFalse(ok)
    }

    // ── ForgeAnnounce (41) ──────────────────────────────────────────────────

    func test_forgeAnnounce_serializesToCanonicalBytes() {
        let data = _forgeAnnounceWireBytesForTests(
            packageId: "npm:react@18.2.0",
            contentHash: "QmForgeHash456",
            sizeBytes: 294912,
            announcedAtMs: 1_700_000_000_000
        )
        let json = String(data: data, encoding: .utf8)
        XCTAssertEqual(
            json,
            "{\"package_id\":\"npm:react@18.2.0\",\"content_hash\":\"QmForgeHash456\",\"size_bytes\":294912,\"announced_at_ms\":1700000000000}"
        )
    }

    func test_forgeAnnounce_allFixtureVectors_serializeExactBytes() throws {
        for v in try loadVectors("forge") {
            let name = v["name"] as? String ?? "?"
            let data = _forgeAnnounceWireBytesForTests(
                packageId: v["package_id"] as? String ?? "",
                contentHash: v["content_hash"] as? String ?? "",
                sizeBytes: Int64(v["size_bytes"] as? Int ?? 0),
                announcedAtMs: Int64(v["announced_at_ms"] as? Int64 ?? Int64(v["announced_at_ms"] as? Int ?? 0))
            )
            let json = String(data: data, encoding: .utf8)
            XCTAssertEqual(json, v["expected_json"] as? String, "forge vector \(name)")
        }
    }

    func test_forge_broadcast_emitsAnnouncePacket_andHandleRaisesEvent() async {
        let sender = senderWith2Peers(localUhid: "aether:alice:01")
        let svc = ForgeAnnounceService(sender: sender)

        let reached = await svc.broadcast(
            packageId: "npm:react@18.2.0",
            contentHash: "QmForgeHash456",
            sizeBytes: 294912,
            announcedAtMs: 1_700_000_000_000
        )
        XCTAssertEqual(reached, 2)

        let broadcasts = sender.broadcasts()
        XCTAssertEqual(broadcasts.count, 1)
        let sent = broadcasts[0]
        XCTAssertEqual(sent.type, .forgeAnnounce)

        let got = Locked<ForgeAnnouncement?>(nil)
        await svc.setOnAnnounceReceived { got.value = $0 }
        let ok = await svc.handle(sent)
        XCTAssertTrue(ok)

        let received = got.value
        XCTAssertNotNil(received)
        XCTAssertEqual(received?.packageId, "npm:react@18.2.0")
        XCTAssertEqual(received?.sizeBytes, 294912)
    }

    func test_forge_handle_wrongType_returnsFalse() async {
        let svc = ForgeAnnounceService(sender: FakeMeshSender(localUhid: "local"))
        let ok = await svc.handle(MeshPacket(type: .data, payload: Data()))
        XCTAssertFalse(ok)
    }

    // ── VaultShardRequest (42) ──────────────────────────────────────────────

    func test_vaultShardRequest_serializesToCanonicalBytes() {
        let data = _vaultShardRequestWireBytesForTests(shardHash: "QmShardHash789", requesterUhid: "aether:bob:02")
        let json = String(data: data, encoding: .utf8)
        XCTAssertEqual(json, "{\"shard_hash\":\"QmShardHash789\",\"requester_uhid\":\"aether:bob:02\"}")
    }

    func test_vaultShardRequest_allFixtureVectors_serializeExactBytes() throws {
        for v in try loadVectors("vaultshard") {
            let name = v["name"] as? String ?? "?"
            let data = _vaultShardRequestWireBytesForTests(
                shardHash: v["shard_hash"] as? String ?? "",
                requesterUhid: v["requester_uhid"] as? String ?? ""
            )
            let json = String(data: data, encoding: .utf8)
            XCTAssertEqual(json, v["expected_json"] as? String, "vaultshard vector \(name)")
        }
    }

    func test_vault_request_emitsShardRequestPacket_andHandleRaisesEvent() async {
        let sender = senderWith2Peers(localUhid: "aether:bob:02")
        let svc = VaultShardRequestService(sender: sender)

        let reached = await svc.requestShard("QmShardHash789")
        XCTAssertEqual(reached, 2)

        let broadcasts = sender.broadcasts()
        XCTAssertEqual(broadcasts.count, 1)
        let sent = broadcasts[0]
        XCTAssertEqual(sent.type, .vaultShardRequest)

        // Sender stamps requester = local UHID.
        let body = try? JSONDecoder().decode(VaultShardRequestWireMirror.self, from: sent.payload)
        XCTAssertEqual(body?.shard_hash, "QmShardHash789")
        XCTAssertEqual(body?.requester_uhid, "aether:bob:02")

        let got = Locked<VaultShardRequest?>(nil)
        await svc.setOnShardRequested { got.value = $0 }
        let ok = await svc.handle(sent)
        XCTAssertTrue(ok)

        let received = got.value
        XCTAssertNotNil(received)
        XCTAssertEqual(received?.shardHash, "QmShardHash789")
        XCTAssertEqual(received?.requesterUhid, "aether:bob:02")
    }

    func test_vault_handle_wrongType_returnsFalse() async {
        let svc = VaultShardRequestService(sender: FakeMeshSender(localUhid: "local"))
        let ok = await svc.handle(MeshPacket(type: .data, payload: Data()))
        XCTAssertFalse(ok)
    }

    /// Decode-only mirror to inspect captured VaultShardRequest payloads in assertions.
    private struct VaultShardRequestWireMirror: Codable {
        let shard_hash: String
        let requester_uhid: String
    }
}
