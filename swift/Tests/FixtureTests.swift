// SPDX-License-Identifier: MIT
import Foundation
import XCTest
@testable import AetherProtocol

/// Cross-language wire-format fixture verifier. Reads
/// `../../fixtures/inputs.json` and `../../fixtures/expected/<name>.bin` and
/// asserts that this language's PacketSerializer produces byte-identical
/// output for each canonical input. See `fixtures/README.md`.
final class FixtureTests: XCTestCase {

    private struct FixtureInput: Decodable {
        let name: String
        let id: String
        let type: Int
        let source_uhid: String
        let destination_uhid: String
        let ttl: Int32
        let priority: Int
        let payload_hex: String
        let packet_nonce_hex: String
        let signature_hex: String
        let timestamp_ms: Int64
        let protocol_version: Int
    }

    private func fixturesDir() -> URL {
        // CWD when xctest runs from `swift test` is the package root.
        var url = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        for _ in 0..<8 {
            let candidate = url.appendingPathComponent("fixtures/inputs.json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                return url.appendingPathComponent("fixtures")
            }
            let parent = url.deletingLastPathComponent()
            if parent.path == url.path { break }
            url = parent
        }
        XCTFail("Could not locate fixtures/inputs.json")
        return URL(fileURLWithPath: "fixtures")
    }

    private func loadInputs() throws -> [FixtureInput] {
        let url = fixturesDir().appendingPathComponent("inputs.json")
        let data = try Data(contentsOf: url)
        return try JSONDecoder().decode([FixtureInput].self, from: data)
    }

    private func hex(_ s: String) -> Data {
        var out = Data(capacity: s.count / 2)
        var i = s.startIndex
        while i < s.endIndex {
            let next = s.index(i, offsetBy: 2)
            out.append(UInt8(s[i..<next], radix: 16) ?? 0)
            i = next
        }
        return out
    }

    private func packet(from input: FixtureInput) -> MeshPacket {
        var p = MeshPacket(
            id: UUID(uuidString: input.id)!,
            type: PacketType(rawValue: UInt8(input.type))!,
            sourceUhid: input.source_uhid,
            destinationUhid: input.destination_uhid,
            ttl: input.ttl,
            priority: UInt8(input.priority),
            payload: hex(input.payload_hex)
        )
        p.packetNonce = hex(input.packet_nonce_hex)
        p.signature = hex(input.signature_hex)
        p.timestampMs = input.timestamp_ms
        p.protocolVersion = UInt8(input.protocol_version)
        return p
    }

    func testSerializeMatchesExpectedBytes() throws {
        for input in try loadInputs() {
            let got = PacketSerializer.serialize(packet(from: input))
            let expected = try Data(contentsOf:
                fixturesDir().appendingPathComponent("expected/\(input.name).bin"))
            XCTAssertEqual(got, expected, "\(input.name): see fixtures/README.md")
        }
    }

    func testDeserializeFromExpectedMatchesInputFields() throws {
        for input in try loadInputs() {
            let bytes = try Data(contentsOf:
                fixturesDir().appendingPathComponent("expected/\(input.name).bin"))
            let got = try PacketSerializer.deserialize(bytes)

            XCTAssertEqual(got.id, UUID(uuidString: input.id)!, input.name)
            XCTAssertEqual(got.type.rawValue, UInt8(input.type), input.name)
            XCTAssertEqual(got.sourceUhid, input.source_uhid, input.name)
            XCTAssertEqual(got.destinationUhid, input.destination_uhid, input.name)
            XCTAssertEqual(got.ttl, input.ttl, input.name)
            XCTAssertEqual(got.priority, UInt8(input.priority), input.name)
            XCTAssertEqual(got.payload, hex(input.payload_hex), input.name)
            XCTAssertEqual(got.packetNonce, hex(input.packet_nonce_hex), input.name)
            XCTAssertEqual(got.signature, hex(input.signature_hex), input.name)
            XCTAssertEqual(got.timestampMs, input.timestamp_ms, input.name)
            XCTAssertEqual(got.protocolVersion, UInt8(input.protocol_version), input.name)
        }
    }
}
