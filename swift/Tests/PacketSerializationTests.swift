// SPDX-License-Identifier: MIT

import XCTest
@testable import AetherProtocol

final class PacketSerializationTests: XCTestCase {
    func testSerializeDeserializeRoundTrip() throws {
        // Create a packet
        var packet = MeshPacket(
            type: .data,
            sourceUhid: "alice-node",
            destinationUhid: "bob-node",
            ttl: 7,
            priority: 10,
            payload: "Hello, Aether!".data(using: .utf8)!
        )

        // Add nonce
        var nonce = Data(count: 8)
        _ = nonce.withUnsafeMutableBytes { buffer in
            SecRandomCopyBytes(kSecRandomDefault, 8, buffer.baseAddress!)
        }
        packet.packetNonce = nonce

        // Serialize
        let serialized = PacketSerializer.serialize(packet)

        // Deserialize
        let deserialized = try PacketSerializer.deserialize(serialized)

        // Verify
        XCTAssertEqual(deserialized.type, packet.type)
        XCTAssertEqual(deserialized.sourceUhid, packet.sourceUhid)
        XCTAssertEqual(deserialized.destinationUhid, packet.destinationUhid)
        XCTAssertEqual(deserialized.ttl, packet.ttl)
        XCTAssertEqual(deserialized.priority, packet.priority)
        XCTAssertEqual(deserialized.payload, packet.payload)
        XCTAssertEqual(deserialized.packetNonce, packet.packetNonce)
        XCTAssertEqual(deserialized.protocolVersion, packet.protocolVersion)
    }

    func testEmptyUhids() throws {
        var packet = MeshPacket(
            type: .sosBroadcast,
            sourceUhid: "node-1",
            destinationUhid: ""  // Broadcast
        )
        packet.packetNonce = Data(repeating: 0x00, count: 8)

        let serialized = PacketSerializer.serialize(packet)
        let deserialized = try PacketSerializer.deserialize(serialized)

        XCTAssertEqual(deserialized.sourceUhid, "node-1")
        XCTAssertEqual(deserialized.destinationUhid, "")
    }

    func testEmptyPayload() throws {
        var packet = MeshPacket(
            type: .heartbeat,
            sourceUhid: "node-1"
        )
        packet.packetNonce = Data(repeating: 0x00, count: 8)
        packet.payload = Data()

        let serialized = PacketSerializer.serialize(packet)
        let deserialized = try PacketSerializer.deserialize(serialized)

        XCTAssertEqual(deserialized.payload.count, 0)
    }

    func testLargePayload() throws {
        var packet = MeshPacket(
            type: .chunkData,
            sourceUhid: "node-1",
            destinationUhid: "node-2",
            payload: Data(repeating: 0xFF, count: 262144)  // 256 KB
        )
        packet.packetNonce = Data(repeating: 0x00, count: 8)

        let serialized = PacketSerializer.serialize(packet)
        let deserialized = try PacketSerializer.deserialize(serialized)

        XCTAssertEqual(deserialized.payload.count, 262144)
    }

    func testUuidRoundTrip() throws {
        let expectedUuid = UUID(uuidString: "550e8400-e29b-41d4-a716-446655440000")!

        var packet = MeshPacket(
            id: expectedUuid,
            type: .data,
            sourceUhid: "node-1"
        )
        packet.packetNonce = Data(repeating: 0x00, count: 8)

        let serialized = PacketSerializer.serialize(packet)
        let deserialized = try PacketSerializer.deserialize(serialized)

        XCTAssertEqual(deserialized.id, expectedUuid)
    }

    func testDataTooShortError() {
        let tooShort = Data([0x01, 0x02])
        XCTAssertThrowsError(try PacketSerializer.deserialize(tooShort)) { error in
            guard case .dataTooShort = error as! PacketSerializationError else {
                XCTFail("Expected dataTooShort error")
                return
            }
        }
    }

    func testAllPacketTypes() throws {
        let types: [PacketType] = [
            .routeRequest, .routeReply, .data, .ack, .sosBroadcast, .sosAck,
            .channelMessage, .chunkRequest, .chunkData, .heartbeat,
            .streamAnnounce, .streamSegment, .streamSubscribe, .streamUnsubscribe,
            .voicePtt, .voiceCall, .voiceSignaling, .dtnBundle, .dtnCustodyAck,
            .dtnDeliveryReceipt, .presenceBeacon, .presenceQuery, .profileSync,
            .tipPacket, .preKeyRequest, .preKeyResponse
        ]

        for type in types {
            var packet = MeshPacket(
                type: type,
                sourceUhid: "node-\(type.rawValue)"
            )
            packet.packetNonce = Data(repeating: 0x00, count: 8)

            let serialized = PacketSerializer.serialize(packet)
            let deserialized = try PacketSerializer.deserialize(serialized)

            XCTAssertEqual(deserialized.type, type, "Failed for type: \(type)")
        }
    }

    func testTimestampPreservation() throws {
        let testTimestamp: Int64 = 1710528000000  // 2024-03-15 12:00:00 UTC

        var packet = MeshPacket(
            type: .data,
            sourceUhid: "node-1"
        )
        packet.timestampMs = testTimestamp
        packet.packetNonce = Data(repeating: 0x00, count: 8)

        let serialized = PacketSerializer.serialize(packet)
        let deserialized = try PacketSerializer.deserialize(serialized)

        XCTAssertEqual(deserialized.timestampMs, testTimestamp)
    }

    func testUnicodeUhids() throws {
        var packet = MeshPacket(
            type: .data,
            sourceUhid: "노드-1",  // Korean
            destinationUhid: "узел-2"  // Russian
        )
        packet.packetNonce = Data(repeating: 0x00, count: 8)

        let serialized = PacketSerializer.serialize(packet)
        let deserialized = try PacketSerializer.deserialize(serialized)

        XCTAssertEqual(deserialized.sourceUhid, "노드-1")
        XCTAssertEqual(deserialized.destinationUhid, "узел-2")
    }

    func testSignaturePreservation() throws {
        var packet = MeshPacket(
            type: .data,
            sourceUhid: "node-1"
        )
        packet.packetNonce = Data(repeating: 0x00, count: 8)
        packet.signature = Data(repeating: 0xAB, count: 64)

        let serialized = PacketSerializer.serialize(packet)
        let deserialized = try PacketSerializer.deserialize(serialized)

        XCTAssertEqual(deserialized.signature, packet.signature)
    }
}
