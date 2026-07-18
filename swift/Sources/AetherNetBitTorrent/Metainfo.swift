// SPDX-License-Identifier: MIT

import Foundation

/// One file within a torrent: its path components and length.
public struct TorrentFileEntry {
    public let path: [String]
    public let length: Int64

    public init(path: [String], length: Int64) {
        self.path = path
        self.length = length
    }

    /// Path components joined with '/'.
    public var joinedPath: String { path.joined(separator: "/") }
}

/// A parsed BitTorrent v1 metainfo (.torrent). `infoHashV1` is the SHA-1 of the RAW
/// bencoded info dictionary as it appears in the file (not a re-encode), extracted by
/// byte-offset, so it matches real clients byte-for-byte.
public struct TorrentMetainfo {
    public let root: BDict
    public let info: BDict
    public let infoHashV1: [UInt8]      // 20 bytes
    public let name: String
    public let pieceLength: Int64
    public let pieceHashes: [[UInt8]]   // each 20-byte SHA-1
    public let files: [TorrentFileEntry]
    public let totalLength: Int64
    public let announceURLs: [String]
    public let isSingleFile: Bool

    /// Lowercase hex of `infoHashV1` (40 chars).
    public var infoHashV1Hex: String { BTHex.encode(infoHashV1) }
}

public enum MetainfoError: Error, Equatable {
    case malformed(String)
}

/// Parses .torrent bytes.
public func parseTorrent(_ data: [UInt8]) throws -> TorrentMetainfo {
    let rootVal = try bencodeDecode(data)
    let root = try rootVal.dictValue()

    guard let infoVal = root.get("info") else {
        throw MetainfoError.malformed("metainfo has no 'info' dictionary")
    }
    let info = try infoVal.dictValue()

    let infoSpan = try extractInfoSpan(data)
    let infoHash = BTHash.sha1(infoSpan)

    guard let nameVal = info.get("name") else {
        throw MetainfoError.malformed("info has no 'name'")
    }
    let name = try nameVal.textValue()

    guard let plVal = info.get("piece length") else {
        throw MetainfoError.malformed("info has no 'piece length'")
    }
    let pieceLength = try plVal.intValue()
    if pieceLength <= 0 {
        throw MetainfoError.malformed("'piece length' must be positive")
    }

    guard let piecesVal = info.get("pieces") else {
        throw MetainfoError.malformed("info has no 'pieces'")
    }
    let piecesBytes = try piecesVal.bytesValue()
    if piecesBytes.count % 20 != 0 {
        throw MetainfoError.malformed("'pieces' length \(piecesBytes.count) is not a multiple of 20")
    }
    var pieceHashes: [[UInt8]] = []
    pieceHashes.reserveCapacity(piecesBytes.count / 20)
    var pi = 0
    while pi < piecesBytes.count {
        pieceHashes.append(Array(piecesBytes[pi..<(pi + 20)]))
        pi += 20
    }

    var files: [TorrentFileEntry] = []
    var total: Int64 = 0
    var singleFile = false
    if let filesVal = info.get("files") {
        let list = try filesVal.listValue()
        for f in list {
            let fd = try f.dictValue()
            guard let lenVal = fd.get("length") else {
                throw MetainfoError.malformed("file entry has no 'length'")
            }
            let length = try lenVal.intValue()
            guard let pathVal = fd.get("path") else {
                throw MetainfoError.malformed("file entry has no 'path'")
            }
            let pathList = try pathVal.listValue()
            var parts: [String] = []
            parts.reserveCapacity(pathList.count)
            for p in pathList {
                parts.append(try p.textValue())
            }
            if parts.isEmpty {
                throw MetainfoError.malformed("file entry has an empty 'path'")
            }
            files.append(TorrentFileEntry(path: parts, length: length))
            total += length
        }
    } else {
        singleFile = true
        guard let lenVal = info.get("length") else {
            throw MetainfoError.malformed("single-file info has neither 'length' nor 'files'")
        }
        let length = try lenVal.intValue()
        files.append(TorrentFileEntry(path: [name], length: length))
        total = length
    }

    // Trackers: announce + announce-list, de-duplicated, order preserved.
    var announce: [String] = []
    var seen = Set<String>()
    func addTracker(_ u: String) {
        if !u.isEmpty && !seen.contains(u) {
            seen.insert(u)
            announce.append(u)
        }
    }
    if let a = root.get("announce"), let s = try? a.textValue() {
        addTracker(s)
    }
    if let al = root.get("announce-list"), let tiers = try? al.listValue() {
        for tier in tiers {
            if let ts = try? tier.listValue() {
                for t in ts {
                    if let s = try? t.textValue() {
                        addTracker(s)
                    }
                }
            }
        }
    }

    return TorrentMetainfo(
        root: root,
        info: info,
        infoHashV1: infoHash,
        name: name,
        pieceLength: pieceLength,
        pieceHashes: pieceHashes,
        files: files,
        totalLength: total,
        announceURLs: announce,
        isSingleFile: singleFile
    )
}

/// Returns the raw bencoded bytes of the top-level "info" value by walking the
/// dictionary with byte-offset tracking (structure already validated by parseTorrent).
func extractInfoSpan(_ data: [UInt8]) throws -> [UInt8] {
    if data.isEmpty || data[0] != UInt8(ascii: "d") {
        throw MetainfoError.malformed("metainfo is not a bencoded dictionary")
    }
    var pos = 1
    let infoKey = Array("info".utf8)
    while pos < data.count && data[pos] != UInt8(ascii: "e") {
        let (keyVal, keyEnd) = try bencodeDecodeN(data, pos)
        guard case .bytes(let key) = keyVal else {
            throw MetainfoError.malformed("dictionary key is not a byte string")
        }
        pos = keyEnd
        let valStart = pos
        let (_, valEnd) = try bencodeDecodeN(data, pos)
        pos = valEnd
        if key == infoKey {
            return Array(data[valStart..<valEnd])
        }
    }
    throw MetainfoError.malformed("metainfo has no 'info' key")
}

/// Creates single-file .torrent bytes for `data`, splitting into `pieceLength`-byte
/// pieces and SHA-1-hashing each. Byte-identical to the C# TorrentBuilder and Go
/// BuildSingleFileTorrent.
public func buildSingleFileTorrent(
    name: String,
    data: [UInt8],
    pieceLength: Int,
    announce: String
) throws -> [UInt8] {
    if name.isEmpty {
        throw MetainfoError.malformed("name is required")
    }
    if pieceLength <= 0 {
        throw MetainfoError.malformed("piece length must be positive")
    }
    let pieceCount = (data.count + pieceLength - 1) / pieceLength
    var pieces = [UInt8](repeating: 0, count: pieceCount * 20)
    for i in 0..<pieceCount {
        let start = i * pieceLength
        let end = min(start + pieceLength, data.count)
        let h = BTHash.sha1(Array(data[start..<end]))
        for j in 0..<20 {
            pieces[i * 20 + j] = h[j]
        }
    }

    let info = BDict()
    try info.add("length", .int(Int64(data.count)))
    try info.add("name", .text(name))
    try info.add("piece length", .int(Int64(pieceLength)))
    try info.add("pieces", .bytes(pieces))

    let root = BDict()
    if !announce.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
        try root.add("announce", .text(announce))
    }
    try root.add("info", .dict(info))
    return bencodeEncode(.dict(root))
}
