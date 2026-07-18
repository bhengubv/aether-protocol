// SPDX-License-Identifier: MIT

import Foundation
import XCTest
@testable import AetherNetBitTorrent

/// Cross-language BitTorrent fixture verifier: asserts this Swift implementation
/// reproduces every vector in `fixtures/bittorrent/vectors.json` byte-for-byte. This
/// mirrors `go/bittorrent/fixture_test.go` exactly — the canonical gate. Every
/// language SDK ships the equivalent test; any wire drift fails on the language that
/// diverges.
final class BitTorrentFixtureTests: XCTestCase {

    // MARK: - Corpus model (mirrors the btCorpus struct in fixture_test.go)

    private struct Corpus: Decodable {
        let bencode_roundtrip: [String]
        let info_hash: [InfoHashVec]
        let peer_messages: [PeerMsgVec]
        let utp_packets: [UtpVec]
        let merkle: [MerkleVec]
        let compact: [CompactVec]
        let krpc: [KrpcVec]
    }

    private struct InfoHashVec: Decodable {
        let name: String
        let size: Int
        let mult: Int
        let add: Int
        let name_str: String
        let piece_length: Int
        let info_hash_hex: String
    }

    private struct PeerMsgVec: Decodable {
        let name: String
        let kind: String
        let a: UInt32
        let b: UInt32
        let c: UInt32
        let wire_hex: String
    }

    private struct UtpVec: Decodable {
        let name: String
        let type: Int
        let conn_id: UInt16
        let timestamp: UInt32
        let timestamp_diff: UInt32
        let window: UInt32
        let seq: UInt16
        let ack: UInt16
        let payload_hex: String
        let wire_hex: String
    }

    private struct MerkleVec: Decodable {
        let name: String
        let size: Int
        let mult: Int
        let add: Int
        let root_hex: String
    }

    private struct CompactPeerVec: Decodable {
        let ip: String
        let port: UInt16
    }

    private struct CompactVec: Decodable {
        let name: String
        let kind: String
        let id_hex: String?
        let peers: [CompactPeerVec]?
        let wire_hex: String
    }

    private struct KrpcVec: Decodable {
        let name: String
        let kind: String
        let tx_hex: String
        let id_hex: String?
        let info_hash_hex: String?
        let error_code: Int64?
        let error_message: String?
        let wire_hex: String
    }

    // MARK: - Corpus loading

    /// `#filePath` = .../swift/Tests/BitTorrent/BitTorrentFixtureTests.swift →
    /// repo root is four levels up.
    private func repoRoot() -> URL {
        URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()  // .../swift/Tests/BitTorrent
            .deletingLastPathComponent()  // .../swift/Tests
            .deletingLastPathComponent()  // .../swift
            .deletingLastPathComponent()  // repo root
    }

    private func loadCorpus() throws -> Corpus {
        let url = repoRoot().appendingPathComponent("fixtures/bittorrent/vectors.json")
        return try JSONDecoder().decode(Corpus.self, from: Data(contentsOf: url))
    }

    /// byte[i] = (i*mult + add) & 0xFF — mirrors fillBytes in fixture_test.go.
    private func fillBytes(_ n: Int, _ mult: Int, _ add: Int) -> [UInt8] {
        var b = [UInt8](repeating: 0, count: n)
        for i in 0..<n {
            b[i] = UInt8(truncatingIfNeeded: i * mult + add)
        }
        return b
    }

    // MARK: - (1) bencode round-trip

    func testFixturesBencode() throws {
        for hs in try loadCorpus().bencode_roundtrip {
            let raw = BTHex.decode(hs)
            let v = try bencodeDecode(raw)
            XCTAssertEqual(BTHex.encode(bencodeEncode(v)), hs, "bencode roundtrip \(hs)")
        }
    }

    // MARK: - (2) info-hash (SHA-1 of the raw bencoded info dict)

    func testFixturesInfoHash() throws {
        for ic in try loadCorpus().info_hash {
            let tb = try buildSingleFileTorrent(
                name: ic.name_str,
                data: fillBytes(ic.size, ic.mult, ic.add),
                pieceLength: ic.piece_length,
                announce: ""
            )
            let m = try parseTorrent(tb)
            XCTAssertEqual(m.infoHashV1Hex, ic.info_hash_hex, "\(ic.name) info-hash")
        }
    }

    // MARK: - (3) peer-wire messages

    func testFixturesPeerMessages() throws {
        for pm in try loadCorpus().peer_messages {
            let msg: PeerMessage
            switch pm.kind {
            case "keepalive":  msg = keepAlive()
            case "choke":      msg = choke()
            case "unchoke":    msg = unchoke()
            case "interested": msg = interested()
            case "have":       msg = have(pm.a)
            case "request":    msg = request(pm.a, pm.b, pm.c)
            case "port":       msg = port(UInt16(pm.a))
            default:
                XCTFail("unknown kind \(pm.kind)")
                continue
            }
            XCTAssertEqual(BTHex.encode(msg.toBytes()), pm.wire_hex, "\(pm.name) wire")
        }
    }

    // MARK: - (4) µTP packets

    func testFixturesUtp() throws {
        for uc in try loadCorpus().utp_packets {
            let payload = BTHex.decode(uc.payload_hex)
            let p = UtpPacket(
                type: UtpPacketType(rawValue: UInt8(uc.type))!,
                connectionID: uc.conn_id,
                timestampMicros: uc.timestamp,
                timestampDiff: uc.timestamp_diff,
                windowSize: uc.window,
                seqNr: uc.seq,
                ackNr: uc.ack,
                payload: payload
            )
            XCTAssertEqual(BTHex.encode(p.toBytes()), uc.wire_hex, "\(uc.name) wire")
        }
    }

    // MARK: - (5) v2 SHA-256 merkle root

    func testFixturesMerkle() throws {
        for mc in try loadCorpus().merkle {
            let root = merkleRoot(fillBytes(mc.size, mc.mult, mc.add))
            XCTAssertEqual(BTHex.encode(root), mc.root_hex, "\(mc.name) root")
        }
    }

    // MARK: - (6) compact node/peer info

    func testFixturesCompact() throws {
        for cc in try loadCorpus().compact {
            let wire = BTHex.decode(cc.wire_hex)
            var reencoded: [UInt8] = []
            switch cc.kind {
            case "node":
                let nodes = try decodeCompactNodes(wire)
                reencoded = encodeCompactNodes(nodes)
            case "peers":
                let peers = try decodeCompactPeers(wire)
                reencoded = encodeCompactPeers(peers)
            default:
                XCTFail("unknown compact kind \(cc.kind)")
                continue
            }
            XCTAssertEqual(BTHex.encode(reencoded), cc.wire_hex, "\(cc.name) compact roundtrip")

            // Additionally: build peers straight from the {ip,port} list and assert the wire.
            if cc.kind == "peers", let plist = cc.peers {
                let built = plist.map { PeerAddr(ipString: $0.ip, port: $0.port)! }
                XCTAssertEqual(BTHex.encode(encodeCompactPeers(built)), cc.wire_hex,
                               "\(cc.name) compact peers from list")
            }
        }
    }

    // MARK: - (7) KRPC (bencode) messages

    func testFixturesKrpc() throws {
        for kc in try loadCorpus().krpc {
            let tx = BTHex.decode(kc.tx_hex)
            let m: KrpcMessage
            switch kc.kind {
            case "get_peers":
                let id = BTHex.decode(kc.id_hex!)
                let ih = BTHex.decode(kc.info_hash_hex!)
                let args = BDict()
                try args.add("id", .bytes(id))
                try args.add("info_hash", .bytes(ih))
                m = KrpcMessage(transactionID: tx, type: .query, method: "get_peers", arguments: args)
            case "error":
                m = KrpcMessage(transactionID: tx, type: .error,
                                errorCode: kc.error_code!, errorMessage: kc.error_message!)
            default:
                XCTFail("unknown krpc kind \(kc.kind)")
                continue
            }
            let enc = try m.encode()
            XCTAssertEqual(BTHex.encode(enc), kc.wire_hex, "\(kc.name) krpc")
        }
    }
}
