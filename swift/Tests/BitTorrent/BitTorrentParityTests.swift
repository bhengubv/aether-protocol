// SPDX-License-Identifier: MIT

import Foundation
import XCTest
@testable import AetherNetBitTorrent

/// Ports the non-fixture Go tests (bencode edge cases, handshake, bitfield, picker,
/// piece store, µTP parsing, merkle edges, v2 info-hash, DHT, KRPC round-trips,
/// extensions, magnet) so the whole codec core — not just the 27 wire vectors — is
/// exercised against the same expectations as the Go reference.
final class BitTorrentParityTests: XCTestCase {

    private func fill(_ n: Int, mult: Int, add: Int) -> [UInt8] {
        var b = [UInt8](repeating: 0, count: n)
        for i in 0..<n { b[i] = UInt8(truncatingIfNeeded: i * mult + add) }
        return b
    }

    // MARK: - Bencode

    func testBencodeRoundtrips() throws {
        for s in ["i0e", "i42e", "i-42e", "0:", "4:spam", "le", "li1ei2ee",
                  "l4:spam4:eggse", "de", "d3:cow3:moo4:spam4:eggse",
                  "d4:infod6:lengthi3ee4:name3:bare"] {
            let v = try bencodeDecode(Array(s.utf8))
            XCTAssertEqual(String(decoding: bencodeEncode(v), as: UTF8.self), s)
        }
    }

    func testBencodeSortsDictKeysCanonically() throws {
        let d = BDict()
        try d.add("spam", .text("eggs"))
        try d.add("cow", .text("moo"))
        XCTAssertEqual(String(decoding: bencodeEncode(.dict(d)), as: UTF8.self),
                       "d3:cow3:moo4:spam4:eggse")
    }

    func testBencodeRejects() {
        for bad in ["i03e", "i-0e", "i-03e", "ie", "i42", "01:a",
                    "4:spam4:eggs", "d3:cow3:moo3:cow3:mooe",
                    "d4:spam4:eggs3:cow3:mooe", "3:ab", ""] {
            XCTAssertThrowsError(try bencodeDecode(Array(bad.utf8)), "expected reject for \(bad)")
        }
    }

    func testBencodeDecodeNReportsConsumed() throws {
        let (v, n) = try bencodeDecodeN(Array("i1e2:xx".utf8), 0)
        XCTAssertEqual(try v.intValue(), 1)
        XCTAssertEqual(n, 3)
    }

    // MARK: - Metainfo / magnet

    func testMetainfoBuildParseRoundtrip() throws {
        let data = fill(70000, mult: 31, add: 7)
        let pieceLength = 32768
        let tb = try buildSingleFileTorrent(name: "payload.bin", data: data,
                                            pieceLength: pieceLength,
                                            announce: "http://tracker.example/announce")
        let m = try parseTorrent(tb)
        XCTAssertEqual(m.name, "payload.bin")
        XCTAssertEqual(m.totalLength, Int64(data.count))
        XCTAssertEqual(m.pieceLength, Int64(pieceLength))
        XCTAssertTrue(m.isSingleFile)
        XCTAssertEqual(m.announceURLs, ["http://tracker.example/announce"])

        let want = (data.count + pieceLength - 1) / pieceLength
        XCTAssertEqual(m.pieceHashes.count, want)
        for i in 0..<want {
            let start = i * pieceLength
            let end = min(start + pieceLength, data.count)
            XCTAssertEqual(m.pieceHashes[i], BTHash.sha1(Array(data[start..<end])), "piece \(i)")
        }
    }

    func testMagnetHexAndBase32ResolveToSameHash() throws {
        let hexHash = "0123456789abcdef0123456789abcdef01234567"
        let m1 = try parseMagnet("magnet:?xt=urn:btih:" + hexHash
                                 + "&dn=test&tr=http%3A%2F%2Ftracker%2Fannounce")
        XCTAssertEqual(m1.infoHashHex, hexHash)
        XCTAssertEqual(m1.displayName, "test")
        XCTAssertEqual(m1.trackers, ["http://tracker/announce"])

        // Base32 of the same 20 bytes must resolve to the same hash.
        let raw = BTHex.decode(hexHash)
        let b32 = base32Encode(raw)
        let m2 = try parseMagnet("magnet:?xt=urn:btih:" + b32)
        XCTAssertEqual(m2.infoHash, m1.infoHash)
    }

    func testMagnetRejects() {
        for bad in ["http://not-a-magnet", "magnet:?dn=noxt", "magnet:?xt=urn:btih:tooshort"] {
            XCTAssertThrowsError(try parseMagnet(bad), "expected reject for \(bad)")
        }
    }

    // MARK: - Peer wire

    func testHandshakeRoundtrip() throws {
        var h = Handshake()
        h.reserved = Handshake.defaultReserved()
        h.infoHash = (0..<20).map { UInt8($0) }
        h.peerID = (0..<20).map { UInt8(200 - $0) }
        let wire = h.toBytes()
        XCTAssertEqual(wire.count, 68)
        XCTAssertEqual(wire[0], 19)
        XCTAssertEqual(Array(wire[1..<20]), Array(protocolString.utf8))
        let back = try parseHandshake(wire)
        XCTAssertEqual(back.infoHash, h.infoHash)
        XCTAssertEqual(back.peerID, h.peerID)
        XCTAssertEqual(back.reserved, h.reserved)
        XCTAssertTrue(back.supportsExtended)
        XCTAssertTrue(back.supportsDht)
    }

    func testPeerMessageDecoders() throws {
        XCTAssertEqual(try have(9).havePieceIndex(), 9)
        let (i, b, l) = try request(2, 16384, 16384).blockRef()
        XCTAssertEqual([i, b, l], [2, 16384, 16384])
        let (pi, pb, block) = try piece(3, 0, [1, 2, 3]).pieceBlock()
        XCTAssertEqual(pi, 3); XCTAssertEqual(pb, 0); XCTAssertEqual(block, [1, 2, 3])
    }

    func testBitfieldMsbFirst() {
        let b = Bitfield(pieceCount: 10)
        b.set(0); b.set(9)
        XCTAssertEqual(b.toBytes()[0], 0x80)
        XCTAssertTrue(b.get(0)); XCTAssertTrue(b.get(9)); XCTAssertFalse(b.get(1))
        XCTAssertEqual(b.popCount(), 2)
        XCTAssertFalse(b.hasAll())
        XCTAssertNotEqual(b.toBytes()[1] & 0x40, 0)
    }

    // MARK: - Picker / piece store

    func testRarestFirstPicker() {
        let p = RarestFirstPicker(pieceCount: 4)
        for i in [0, 1, 2] { p.peerHas("A", i) }
        for i in [1, 2, 3] { p.peerHas("B", i) }
        XCTAssertEqual(p.pickFor("A"), 0)  // rarest (availability 1)
        p.setHave(0)
        XCTAssertEqual(p.pickFor("A"), 1)  // {1,2} both avail 2 → first index
        XCTAssertEqual(p.pickFor("A"), 2)  // 1 in-flight → 2
        p.release(1)
        XCTAssertEqual(p.pickFor("B"), 3)  // B's {1,3}: 1 avail 2, 3 avail 1 → 3
    }

    func testPieceStoreFromContentAssembles() {
        let data = fill(5000, mult: 7, add: 0)
        let s = PieceStore.fromContent(data, pieceLength: 1024)
        XCTAssertEqual(s.pieceCount(), 5)
        XCTAssertTrue(s.isComplete())
        XCTAssertTrue(s.buildBitfield().hasAll())
        XCTAssertEqual(s.lengthOfPiece(4), 5000 - 4 * 1024)
        XCTAssertEqual(s.assemble(), data)
    }

    func testPieceStoreVerifiesOnComplete() {
        let data = fill(2048, mult: 1, add: 0)
        let src = PieceStore.fromContent(data, pieceLength: 1024)
        let dst = PieceStore(pieceLength: 1024, totalLength: 2048, pieceHashes: src.pieceHashes)
        let good = src.readBlock(0, 0, 1024)!
        XCTAssertTrue(dst.tryComplete(0, good))
        XCTAssertFalse(dst.tryComplete(1, [UInt8](repeating: 0, count: 1024)))
        XCTAssertFalse(dst.isComplete())
    }

    // MARK: - µTP

    func testUtpRoundtripAndExtensions() throws {
        for ty in [UtpPacketType.syn, .state, .fin, .reset, .data] {
            let p = UtpPacket(type: ty, connectionID: 42, windowSize: 1024, seqNr: 1)
            let back = try parseUtpPacket(p.toBytes())
            XCTAssertEqual(back.type, ty)
            XCTAssertEqual(back.connectionID, 42)
            XCTAssertEqual(back.seqNr, 1)
        }
        // Extension chain [next=0][len=4][4 bytes] then payload.
        let base = UtpPacket(type: .data, connectionID: 1, seqNr: 1).toBytes()
        var withExt = [UInt8](repeating: 0, count: 20 + 6 + 3)
        for i in 0..<20 { withExt[i] = base[i] }
        withExt[1] = 1   // first extension present
        withExt[20] = 0  // next = none
        withExt[21] = 4  // ext len
        withExt[26] = 0xAA; withExt[27] = 0xBB; withExt[28] = 0xCC
        let parsed = try parseUtpPacket(withExt)
        XCTAssertEqual(parsed.payload, [0xAA, 0xBB, 0xCC])
    }

    func testUtpRejects() {
        XCTAssertThrowsError(try parseUtpPacket([UInt8](repeating: 0, count: 10)))
        var bad = UtpPacket(type: .syn).toBytes()
        bad[0] = (4 << 4) | 2  // version 2
        XCTAssertThrowsError(try parseUtpPacket(bad))
    }

    // MARK: - Merkle

    func testMerkleEdges() {
        XCTAssertEqual(merkleRoot([]), [UInt8](repeating: 0, count: 32))
        let one = fill(100, mult: 7, add: 1)
        XCTAssertEqual(merkleRoot(one), BTHash.sha256(one))

        let two = fill(merkleBlockSize + 50, mult: 7, add: 1)
        let h0 = BTHash.sha256(Array(two[0..<merkleBlockSize]))
        let h1 = BTHash.sha256(Array(two[merkleBlockSize...]))
        XCTAssertEqual(merkleRoot(two), BTHash.sha256(h0 + h1))
    }

    func testV2InfoHash() throws {
        let info = BDict()
        try info.add("meta version", .int(2))
        try info.add("name", .text("v2.bin"))
        try info.add("piece length", .int(65536))
        let b = bencodeEncode(.dict(info))
        XCTAssertEqual(bitTorrentV2InfoHash(b), BTHash.sha256(b))
        XCTAssertEqual(bitTorrentV2InfoHashTruncated(b).count, 20)
    }

    // MARK: - DHT

    func testNodeIDXorDistance() {
        var a = NodeID(); a.bytes[0] = 0xF0
        var b = NodeID(); b.bytes[0] = 0x0F
        XCTAssertEqual(a.distanceTo(b).bytes[0], 0xFF)
        var same = NodeID(); same.bytes[19] = 1
        XCTAssertEqual(same.distanceTo(same).leadingZeros(), 160)
    }

    func testCompactNodeRoundtrip() throws {
        let id = NodeID((0..<20).map { UInt8($0) })
        let enc = encodeCompactNodes([DhtContact(id: id, ip: [1, 2, 3, 4], port: 6881)])
        XCTAssertEqual(enc.count, 26)
        XCTAssertEqual(Array(enc[20..<24]), [1, 2, 3, 4])
        XCTAssertEqual(Array(enc[24..<26]), [0x1a, 0xe1])
        let back = try decodeCompactNodes(enc)
        XCTAssertEqual(back.count, 1)
        XCTAssertEqual(back[0].port, 6881)
        XCTAssertEqual(back[0].id, id)
        XCTAssertEqual(back[0].ip, [1, 2, 3, 4])
    }

    func testRoutingTableAddAndClosest() {
        let rt = RoutingTable(selfID: NodeID())
        for i in 1...20 {
            var id = NodeID(); id.bytes[0] = UInt8(i)
            rt.tryAdd(DhtContact(id: id, ip: [127, 0, 0, 1], port: UInt16(6000 + i)))
        }
        XCTAssertGreaterThan(rt.count(), 0)
        var target = NodeID(); target.bytes[0] = 1
        let closest = rt.closestTo(target, 3)
        XCTAssertFalse(closest.isEmpty)
        XCTAssertEqual(closest[0].id.bytes[0], 1)
    }

    // MARK: - KRPC round-trips

    func testKrpcQueryRoundtrip() throws {
        let args = BDict()
        try args.add("id", .bytes([UInt8](repeating: 0xAA, count: 20)))
        try args.add("info_hash", .bytes([UInt8](repeating: 0xBB, count: 20)))
        let m = KrpcMessage(transactionID: Array("aa".utf8), type: .query,
                            method: "get_peers", arguments: args)
        let enc = try m.encode()
        XCTAssertEqual(enc, try m.encode())  // deterministic
        let dec = try decodeKrpc(enc)
        XCTAssertEqual(dec.type, .query)
        XCTAssertEqual(dec.method, "get_peers")
        XCTAssertEqual(dec.transactionID, Array("aa".utf8))
        let ih = try dec.arguments!.get("info_hash")!.bytesValue()
        XCTAssertEqual(ih, [UInt8](repeating: 0xBB, count: 20))
    }

    func testKrpcErrorRoundtrip() throws {
        let m = KrpcMessage(transactionID: Array("zz".utf8), type: .error,
                            errorCode: 201, errorMessage: "Generic Error")
        let dec = try decodeKrpc(try m.encode())
        XCTAssertEqual(dec.type, .error)
        XCTAssertEqual(dec.errorCode, 201)
        XCTAssertEqual(dec.errorMessage, "Generic Error")
    }

    // MARK: - Extensions (BEP-10 / BEP-9 / BEP-11)

    func testExtensionsHandshake() throws {
        let payload = buildExtensionHandshake(["ut_metadata": 1, "ut_pex": 2], metadataSize: 1024)
        let (sub, body) = try splitExtended(payload)
        XCTAssertEqual(sub, extensionHandshakeID)
        let h = try parseExtensionHandshake(body)
        XCTAssertEqual(h.metadataMessageID, 1)
        XCTAssertEqual(h.pexMessageID, 2)
        XCTAssertEqual(h.metadataSize, 1024)
    }

    func testExtensionsUtMetadata() throws {
        let req = try parseMetadata(buildMetadataRequest(3))
        XCTAssertEqual(req.type, .request); XCTAssertEqual(req.piece, 3)
        let data = try parseMetadata(buildMetadataData(0, 100, [1, 2, 3]))
        XCTAssertEqual(data.type, .data); XCTAssertEqual(data.piece, 0)
        XCTAssertEqual(data.totalSize, 100); XCTAssertEqual(data.data, [1, 2, 3])
        let rej = try parseMetadata(buildMetadataReject(5))
        XCTAssertEqual(rej.type, .reject); XCTAssertEqual(rej.piece, 5)
    }

    func testExtensionsMetadataAssemblerVerifies() {
        let info = Array("d4:name6:v1.bine".utf8)
        let ih = BTHash.sha1(info)
        let asm = MetadataAssembler(totalSize: info.count)
        asm.add(0, info)
        XCTAssertEqual(asm.tryFinish(ih), info)
        XCTAssertNil(asm.tryFinish([UInt8](repeating: 0, count: 20)))
    }

    func testExtensionsPex() throws {
        let peers = [PeerAddr(ip: [1, 2, 3, 4], port: 1000), PeerAddr(ip: [5, 6, 7, 8], port: 2000)]
        let got = try parsePexAdded(buildPexAdded(peers))
        XCTAssertEqual(got.count, 2)
        XCTAssertEqual(got[0].port, 1000)
        XCTAssertEqual(got[0].ip, [1, 2, 3, 4])
    }
}

/// RFC 4648 base32 encode (no padding) — test-only helper, the inverse of the decoder
/// in Magnet.swift, used to prove hex and base32 info-hashes resolve identically.
private func base32Encode(_ data: [UInt8]) -> String {
    let alphabet = Array("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567")
    var bits = 0, buffer = 0, out = ""
    for b in data {
        buffer = (buffer << 8) | Int(b)
        bits += 8
        while bits >= 5 {
            bits -= 5
            out.append(alphabet[(buffer >> bits) & 0x1F])
        }
    }
    if bits > 0 {
        out.append(alphabet[(buffer << (5 - bits)) & 0x1F])
    }
    return out
}
