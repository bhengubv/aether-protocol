// SPDX-License-Identifier: MIT
// Unit tests for RlncEngine — GF(2⁸) arithmetic, encoder, decoder, codec.

import XCTest
@testable import AetherMeshProtocol

// ── Helpers ───────────────────────────────────────────────────────────────────

private func makeSource(k: Int, symSize: Int) -> [[UInt8]] {
    (0..<k).map { i in
        (0..<symSize).map { j in UInt8((i * symSize + j) & 0xFF) }
    }
}

private func splitPackets(_ buf: Data, count: Int) -> [Data] {
    let pktSize = buf.count / count
    return (0..<count).map { i in buf[i * pktSize ..< (i + 1) * pktSize] }
}

// ── RlncEncoder ───────────────────────────────────────────────────────────────

final class RlncEncoderTests: XCTestCase {

    func testSystematicFirstKPacketsEqualSource() {
        let k = 4, sym = 8
        let source = makeSource(k: k, symSize: sym)
        let enc = RlncEncoder(source: source, systematic: true)

        for i in 0..<k {
            let (coeff, data) = enc.nextPacket()
            XCTAssertEqual(coeff[i], 1, "coeff[\(i)] should be 1")
            for j in 0..<k where j != i { XCTAssertEqual(coeff[j], 0) }
            XCTAssertEqual(data, source[i], "systematic pkt \(i) data mismatch")
        }
    }

    func testRepairPacketsNotAllZero() {
        let syms: [[UInt8]] = [[1,2,3],[4,5,6],[7,8,9]]
        let enc = RlncEncoder(source: syms, systematic: false)
        for i in 0..<20 {
            let (coeff, _) = enc.nextPacket()
            XCTAssert(coeff.contains { $0 != 0 },
                      "repair pkt \(i) has all-zero coefficient vector")
        }
    }
}

// ── RlncDecoder ───────────────────────────────────────────────────────────────

final class RlncDecoderTests: XCTestCase {

    func testRoundTripK4() throws {
        let k = 4, sym = 8
        let source = makeSource(k: k, symSize: sym)
        let enc = RlncEncoder(source: source, systematic: true)
        let dec = RlncDecoder(generationSize: k, symbolSize: sym)

        while !dec.isComplete {
            let (c, d) = enc.nextPacket()
            dec.addPacket(coefficients: c, encodedSymbol: d)
        }

        let decoded = try XCTUnwrap(dec.tryDecode())
        let expected = source.flatMap { $0 }
        XCTAssertEqual([UInt8](decoded), expected)
    }

    func testExactlyKSystematicPacketsComplete() {
        let k = 3, sym = 4
        let source = makeSource(k: k, symSize: sym)
        let enc = RlncEncoder(source: source, systematic: true)
        let dec = RlncDecoder(generationSize: k, symbolSize: sym)

        for _ in 0..<k {
            let (c, d) = enc.nextPacket()
            dec.addPacket(coefficients: c, encodedSymbol: d)
        }
        XCTAssertTrue(dec.isComplete)
        XCTAssertEqual(dec.rank, k)
    }

    func testLinearlyDependentPacketIgnored() {
        let k = 2, sym = 4
        let source = makeSource(k: k, symSize: sym)
        let enc = RlncEncoder(source: source, systematic: true)
        let dec = RlncDecoder(generationSize: k, symbolSize: sym)

        let (c0, d0) = enc.nextPacket()
        dec.addPacket(coefficients: c0, encodedSymbol: d0)
        let rankBefore = dec.rank
        dec.addPacket(coefficients: c0, encodedSymbol: d0) // duplicate
        XCTAssertEqual(dec.rank, rankBefore, "duplicate packet should not increase rank")
    }

    func testIsCompleteAtRankK() {
        let k = 2, sym = 3
        let source = makeSource(k: k, symSize: sym)
        let enc = RlncEncoder(source: source, systematic: true)
        let dec = RlncDecoder(generationSize: k, symbolSize: sym)

        XCTAssertFalse(dec.isComplete)
        let (c0, d0) = enc.nextPacket(); dec.addPacket(coefficients: c0, encodedSymbol: d0)
        XCTAssertFalse(dec.isComplete)
        let (c1, d1) = enc.nextPacket(); dec.addPacket(coefficients: c1, encodedSymbol: d1)
        XCTAssertTrue(dec.isComplete)
    }

    func testRepairOnlyRoundTrip() throws {
        let k = 4, sym = 8
        let source = makeSource(k: k, symSize: sym)
        let enc = RlncEncoder(source: source, systematic: false)
        let dec = RlncDecoder(generationSize: k, symbolSize: sym)

        var attempts = 0
        while !dec.isComplete {
            let (c, d) = enc.nextPacket()
            dec.addPacket(coefficients: c, encodedSymbol: d)
            attempts += 1
            XCTAssertLessThan(attempts, 200, "repair-only decoder stalled")
        }

        let decoded = try XCTUnwrap(dec.tryDecode())
        let expected = source.flatMap { $0 }
        XCTAssertEqual([UInt8](decoded), expected, "repair-only round-trip mismatch")
    }
}

// ── RlncCodec ─────────────────────────────────────────────────────────────────

final class RlncCodecTests: XCTestCase {

    func testMetadata() {
        let codec = RlncCodec(generationSize: 16)
        XCTAssertEqual(codec.codecName, "RLNC-GF256")
        XCTAssertEqual(codec.deviceTierRequired, 0)
        XCTAssertEqual(codec.overheadFraction, 0.05, accuracy: 1e-9)
        XCTAssertEqual(codec.fixedSymbolSizeBytes, 0)
    }

    func testK1SingleSymbolRoundTrip() throws {
        let codec  = RlncCodec(generationSize: 1)
        let source = Data([0xDE, 0xAD, 0xBE, 0xEF])
        let encoded = try codec.encode(source: source, targetSymbolCount: 2)
        let pkts    = splitPackets(encoded, count: 2)
        let decoded = try XCTUnwrap(codec.tryDecode(receivedSymbols: pkts, sourceSymbolCount: 1))
        XCTAssertEqual(decoded.prefix(source.count), source)
    }

    func testLargePayloadRoundTrip() throws {
        let codec  = RlncCodec(generationSize: 16)
        let source = Data((0..<1024).map { UInt8($0 & 0xFF) })
        let encoded = try codec.encode(source: source, targetSymbolCount: 20)
        let pkts    = splitPackets(encoded, count: 20)
        let decoded = try XCTUnwrap(codec.tryDecode(receivedSymbols: pkts, sourceSymbolCount: 16))
        XCTAssertEqual(decoded.prefix(source.count), source)
    }

    func testDecodeWithLosses() throws {
        let codec   = RlncCodec(generationSize: 16)
        let source  = Data((0..<512).map { UInt8($0 & 0xFF) })
        let encoded = try codec.encode(source: source, targetSymbolCount: 20)
        var pkts    = splitPackets(encoded, count: 20)
        // Drop 4 packets.
        for idx in [11, 7, 3, 0].sorted(by: >) { pkts.remove(at: idx) }
        let decoded = try XCTUnwrap(codec.tryDecode(receivedSymbols: pkts, sourceSymbolCount: 16))
        XCTAssertEqual(decoded.prefix(source.count), source)
    }
}
