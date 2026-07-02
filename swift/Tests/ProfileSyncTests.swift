// SPDX-License-Identifier: MIT
import XCTest
import Foundation
@testable import AetherNetProtocol

/// Unit tests for ``ProfileService`` (PacketType.profileSync). Mirrors the C# `ProfileSyncTests`.
/// Directed exchange — a ``FakeMeshSender`` captures the directed send.
///
/// The byte-identity vectors match `fixtures/profiles/vectors.json` and the C# `[InlineData]`
/// vectors exactly (snake_case keys, field order uhid, display_name, avatar_ref, status_message,
/// updated_at_ms, no whitespace, updated_at_ms a bare integer, all string fields always present).
final class ProfileSyncTests: XCTestCase {

    private static let LOCAL = "aether:local:01"

    /// Mirror used only to DECODE captured ProfileSync payloads in assertions. The real wire struct
    /// (`ProfileSyncWire`) is `private` to the service; byte-identity of the real encoder is verified
    /// separately via `_profileSyncWireBytesForTests`.
    private struct ProfileSyncWireMirror: Codable {
        let uhid: String
        let display_name: String
        let avatar_ref: String
        let status_message: String
        let updated_at_ms: Int64
    }

    /// Build an inbound ProfileSync packet, serialised via the real wire encoder.
    private func profilePacket(
        uhid: String,
        name: String,
        avatar: String,
        status: String,
        updatedAtMs: Int64
    ) -> MeshPacket {
        MeshPacket(
            type: .profileSync,
            sourceUhid: uhid,
            destinationUhid: Self.LOCAL,
            payload: _profileSyncWireBytesForTests(
                uhid: uhid,
                displayName: name,
                avatarRef: avatar,
                statusMessage: status,
                updatedAtMs: updatedAtMs
            )
        )
    }

    // MARK: - Byte-identity vectors (fixtures/profiles/vectors.json)

    func test_profileSyncWire_basicVector_serializesExactBytes() {
        let data = _profileSyncWireBytesForTests(
            uhid: "aether:alice:01",
            displayName: "Alice",
            avatarRef: "blake3:abc",
            statusMessage: "available",
            updatedAtMs: 1_700_000_000_000
        )
        let json = String(data: data, encoding: .utf8)
        XCTAssertEqual(
            json,
            "{\"uhid\":\"aether:alice:01\",\"display_name\":\"Alice\",\"avatar_ref\":\"blake3:abc\",\"status_message\":\"available\",\"updated_at_ms\":1700000000000}"
        )
    }

    func test_profileSyncWire_minimalVector_serializesExactBytes() {
        let data = _profileSyncWireBytesForTests(
            uhid: "n",
            displayName: "",
            avatarRef: "",
            statusMessage: "",
            updatedAtMs: 0
        )
        let json = String(data: data, encoding: .utf8)
        XCTAssertEqual(
            json,
            "{\"uhid\":\"n\",\"display_name\":\"\",\"avatar_ref\":\"\",\"status_message\":\"\",\"updated_at_ms\":0}"
        )
    }

    // MARK: - PublishProfileTo (directed)

    func test_publishProfileTo_sendsDirectedProfileToPeer() async {
        let sender = FakeMeshSender(localUhid: "aether:alice:01")
        let svc = ProfileService(sender: sender)
        await svc.setLocalProfile(displayName: "Alice", avatarRef: "blake3:abc", statusMessage: "available")

        let ok = await svc.publishProfileTo("aether:bob:02")

        XCTAssertTrue(ok)
        let sends = sender.unicasts()
        XCTAssertEqual(sends.count, 1)
        XCTAssertEqual(sends[0].packet.type, .profileSync)
        XCTAssertEqual(sends[0].nextHopUhid, "aether:bob:02")
        XCTAssertEqual(sends[0].packet.destinationUhid, "aether:bob:02")

        let body = try? JSONDecoder().decode(ProfileSyncWireMirror.self, from: sends[0].packet.payload)
        XCTAssertEqual(body?.uhid, "aether:alice:01")
        XCTAssertEqual(body?.display_name, "Alice")
    }

    func test_publishProfileTo_emptyPeer_isNoOp() async {
        let sender = FakeMeshSender(localUhid: "aether:alice:01")
        let svc = ProfileService(sender: sender)
        let ok = await svc.publishProfileTo("")
        XCTAssertFalse(ok)
        XCTAssertEqual(sender.unicasts().count, 0)
    }

    // MARK: - Handle

    func test_handle_cachesPeerProfileAndRaisesEvent() async {
        let svc = ProfileService(sender: FakeMeshSender(localUhid: Self.LOCAL))
        let updated = Locked<ProfileSnapshot?>(nil)
        await svc.setOnProfileUpdated { e in updated.value = e }

        let ok = await svc.handle(profilePacket(
            uhid: "aether:bob:02", name: "Bob", avatar: "blake3:xyz", status: "busy", updatedAtMs: 1_700_000_000_000))

        XCTAssertTrue(ok)
        XCTAssertNotNil(updated.value)
        XCTAssertEqual(updated.value?.displayName, "Bob")

        let cached = await svc.getProfile("aether:bob:02")
        XCTAssertNotNil(cached)
        XCTAssertEqual(cached?.statusMessage, "busy")
        let known = await svc.getKnownProfiles()
        XCTAssertEqual(known.count, 1)
    }

    func test_handle_refreshesExistingProfile() async {
        let svc = ProfileService(sender: FakeMeshSender(localUhid: Self.LOCAL))
        _ = await svc.handle(profilePacket(uhid: "aether:bob:02", name: "Bob", avatar: "", status: "here", updatedAtMs: 1000))
        _ = await svc.handle(profilePacket(uhid: "aether:bob:02", name: "Bob", avatar: "", status: "away", updatedAtMs: 2000))

        let cached = await svc.getProfile("aether:bob:02")
        XCTAssertEqual(cached?.statusMessage, "away")
        let known = await svc.getKnownProfiles()
        XCTAssertEqual(known.count, 1)
    }

    func test_handle_ownProfile_isIgnored() async {
        let svc = ProfileService(sender: FakeMeshSender(localUhid: Self.LOCAL))
        let ok = await svc.handle(profilePacket(uhid: Self.LOCAL, name: "Me", avatar: "", status: "", updatedAtMs: 1))
        XCTAssertFalse(ok)
        let known = await svc.getKnownProfiles()
        XCTAssertTrue(known.isEmpty)
    }

    func test_handle_wrongPacketType_returnsFalse() async {
        let svc = ProfileService(sender: FakeMeshSender(localUhid: Self.LOCAL))
        var pkt = profilePacket(uhid: "aether:bob:02", name: "Bob", avatar: "", status: "", updatedAtMs: 1)
        pkt.type = .data
        let ok = await svc.handle(pkt)
        XCTAssertFalse(ok)
    }

    // MARK: - Local profile

    func test_setLocalProfile_stampsFieldsAndUhid() async {
        let svc = ProfileService(sender: FakeMeshSender(localUhid: "aether:alice:01"))
        await svc.setLocalProfile(displayName: "Alice", avatarRef: "blake3:abc", statusMessage: "hi")
        let local = await svc.getLocalProfile()
        XCTAssertEqual(local.uhid, "aether:alice:01")
        XCTAssertEqual(local.displayName, "Alice")
        XCTAssertEqual(local.avatarRef, "blake3:abc")
        XCTAssertEqual(local.statusMessage, "hi")
        XCTAssertGreaterThan(local.updatedAtMs, 0)
    }
}
