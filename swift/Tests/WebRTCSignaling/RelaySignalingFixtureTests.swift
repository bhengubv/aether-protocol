// SPDX-License-Identifier: MIT

import XCTest
import Foundation
@testable import AetherNetWebRTCSignaling

/// Cross-language WebRTC-signalling framing parity: the Swift carrier must reproduce the shared
/// oracle's byte vectors (`fixtures/webrtc/expected/<name>.bin`) byte-for-byte for every case in
/// `fixtures/webrtc/inputs.json` — the exact `AWS1` ++ JSON frame emitted by the C#/Go/TS carriers —
/// then deframe each back to matching fields. This replaces the previously hardcoded goldens with
/// ONE shared fixture consumed by every language port.
final class RelaySignalingFixtureTests: XCTestCase {

    private func fixturesDir() -> URL {
        var url = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
        for _ in 0..<10 {
            let candidate = url.appendingPathComponent("fixtures/webrtc/inputs.json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                return url.appendingPathComponent("fixtures/webrtc")
            }
            url = url.deletingLastPathComponent()
        }
        XCTFail("Could not locate fixtures/webrtc/inputs.json")
        return URL(fileURLWithPath: "fixtures/webrtc")
    }

    private struct Input: Decodable {
        let name: String
        let from_uhid: String?
        let to_uhid: String?
        let type: Int
        let sdp: String?
        let candidate: String?
        let sdp_mid: String?
        let sdp_mline_index: Int?
    }

    private func loadInputs() throws -> [Input] {
        let url = fixturesDir().appendingPathComponent("inputs.json")
        return try JSONDecoder().decode([Input].self, from: Data(contentsOf: url))
    }

    private func expectedBytes(_ name: String) throws -> Data {
        try Data(contentsOf: fixturesDir().appendingPathComponent("expected/\(name).bin"))
    }

    /// An empty `sdp` / `candidate` / `sdp_mid` in the fixture means "field omitted" -> `nil`.
    private func omit(_ s: String?) -> String? {
        if let s = s, !s.isEmpty { return s }
        return nil
    }

    private func toSignal(_ inp: Input) -> WebRtcSignal {
        let type: WebRtcSignalType
        switch inp.type {
        case 0: type = .offer
        case 1: type = .answer
        case 2: type = .iceCandidate
        default: fatalError("\(inp.name): unknown signal type \(inp.type)")
        }
        return WebRtcSignal(
            fromUhid: inp.from_uhid ?? "",
            toUhid: inp.to_uhid ?? "",
            type: type,
            sdp: omit(inp.sdp),
            candidate: omit(inp.candidate),
            sdpMLineIndex: UInt16(inp.sdp_mline_index ?? 0),
            sdpMid: omit(inp.sdp_mid))
    }

    func test_frameMatchesSharedFixture() throws {
        for inp in try loadInputs() {
            XCTAssertEqual(RelayWebRtcSignaling.frame(toSignal(inp)), try expectedBytes(inp.name),
                           "\(inp.name): frame byte mismatch — see fixtures/README.md")
        }
    }

    func test_deframeRoundTrip() throws {
        for inp in try loadInputs() {
            let data = try expectedBytes(inp.name)
            // Deframe: strip the 4-byte `AWS1` magic, then parse the JSON body — the inverse of `frame`.
            let body = data.subdata(in: 4 ..< data.count)
            guard let signal = WebRtcSignal.fromRelayJson(body) else {
                XCTFail("\(inp.name): deframe returned nil"); continue
            }
            XCTAssertEqual(signal, toSignal(inp),
                           "\(inp.name): deframed signal must round-trip to the fixture case")
        }
    }
}
