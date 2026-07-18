// SPDX-License-Identifier: MIT

import Foundation

/// Holds verified pieces of a torrent in memory, verifying each against its SHA-1
/// before accepting it, and can serve blocks or assemble the whole content.
public final class PieceStore {
    private let pieceLength: Int
    private let totalLength: Int64
    private(set) var pieceHashes: [[UInt8]]
    private var pieces: [Int: [UInt8]] = [:]

    /// Creates an empty store for the given layout.
    public init(pieceLength: Int, totalLength: Int64, pieceHashes: [[UInt8]]) {
        self.pieceLength = pieceLength
        self.totalLength = totalLength
        self.pieceHashes = pieceHashes
    }

    /// The number of pieces.
    public func pieceCount() -> Int { pieceHashes.count }

    /// The byte length of a piece (the last may be short).
    public func lengthOfPiece(_ i: Int) -> Int {
        if i < 0 || i >= pieceHashes.count { return 0 }
        if i == pieceHashes.count - 1 {
            return Int(totalLength - Int64(i) * Int64(pieceLength))
        }
        return pieceLength
    }

    /// Reports whether a verified piece is present.
    public func has(_ i: Int) -> Bool { pieces[i] != nil }

    /// Verifies data against the piece's SHA-1 and stores it on success.
    @discardableResult
    public func tryComplete(_ i: Int, _ data: [UInt8]) -> Bool {
        if i < 0 || i >= pieceHashes.count { return false }
        if data.count != lengthOfPiece(i) { return false }
        if BTHash.sha1(data) != pieceHashes[i] { return false }
        pieces[i] = data
        return true
    }

    /// Returns a block from a stored piece.
    public func readBlock(_ i: Int, _ begin: Int, _ length: Int) -> [UInt8]? {
        guard let p = pieces[i], begin >= 0, length >= 0, begin + length <= p.count else {
            return nil
        }
        return Array(p[begin..<(begin + length)])
    }

    /// Returns a bitfield of currently-held pieces.
    public func buildBitfield() -> Bitfield {
        let bf = Bitfield(pieceCount: pieceHashes.count)
        for i in 0..<pieceHashes.count where has(i) {
            bf.set(i)
        }
        return bf
    }

    /// Reports whether every piece is present.
    public func isComplete() -> Bool { pieces.count == pieceHashes.count }

    /// Returns the full content if complete.
    public func assemble() -> [UInt8]? {
        if !isComplete() { return nil }
        var out = [UInt8]()
        out.reserveCapacity(Int(totalLength))
        for i in 0..<pieceHashes.count {
            out.append(contentsOf: pieces[i]!)
        }
        return out
    }

    /// Builds a complete store from raw content (a seeder's side).
    public static func fromContent(_ data: [UInt8], pieceLength: Int) -> PieceStore {
        let pieceCount = (data.count + pieceLength - 1) / pieceLength
        var hashes = [[UInt8]](repeating: [], count: pieceCount)
        let s = PieceStore(pieceLength: pieceLength, totalLength: Int64(data.count), pieceHashes: [])
        for i in 0..<pieceCount {
            let start = i * pieceLength
            let end = min(start + pieceLength, data.count)
            let block = Array(data[start..<end])
            hashes[i] = BTHash.sha1(block)
            s.pieces[i] = block
        }
        s.pieceHashes = hashes
        return s
    }
}
