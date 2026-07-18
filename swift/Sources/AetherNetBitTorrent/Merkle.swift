// SPDX-License-Identifier: MIT

import Foundation

/// The BitTorrent v2 leaf-block size (BEP-52).
public let merkleBlockSize = 16384

/// Computes the SHA-256 merkle root of `data` split into 16 KiB leaf blocks (BEP-52):
/// each block SHA-256'd, the leaf layer zero-padded (a zero hash is 32 zero bytes) to
/// the next power of two, internal nodes SHA-256(left||right). Byte-identical to the
/// C# MerkleTree.ComputeRoot and Go MerkleRoot.
public func merkleRoot(_ data: [UInt8]) -> [UInt8] {
    merkleRoot(data, blockSize: merkleBlockSize)
}

/// merkleRoot with an explicit block size.
public func merkleRoot(_ data: [UInt8], blockSize: Int) -> [UInt8] {
    precondition(blockSize > 0, "block size must be positive")
    var leaves: [[UInt8]] = []
    var i = 0
    while i < data.count {
        let end = min(i + blockSize, data.count)
        leaves.append(BTHash.sha256(Array(data[i..<end])))
        i += blockSize
    }
    if leaves.isEmpty {
        return [UInt8](repeating: 0, count: 32)  // empty content → zero root
    }
    return merkleRootOf(leaves)
}

/// Combines leaf hashes into a root, zero-padding to the next power of two.
private func merkleRootOf(_ leafHashes: [[UInt8]]) -> [UInt8] {
    if leafHashes.isEmpty {
        return [UInt8](repeating: 0, count: 32)
    }
    var level = leafHashes

    var width = 1
    while width < level.count { width <<= 1 }
    let zero = [UInt8](repeating: 0, count: 32)
    while level.count < width { level.append(zero) }

    while level.count > 1 {
        var next: [[UInt8]] = []
        next.reserveCapacity(level.count / 2)
        var i = 0
        while i < level.count {
            var combined = [UInt8]()
            combined.reserveCapacity(64)
            combined.append(contentsOf: level[i])
            combined.append(contentsOf: level[i + 1])
            next.append(BTHash.sha256(combined))
            i += 2
        }
        level = next
    }
    return level[0]
}

/// The full 32-byte v2 info-hash: SHA-256 of the bencoded info dict.
public func bitTorrentV2InfoHash(_ infoDictBytes: [UInt8]) -> [UInt8] {
    BTHash.sha256(infoDictBytes)
}

/// The v2 info-hash truncated to 20 bytes.
public func bitTorrentV2InfoHashTruncated(_ infoDictBytes: [UInt8]) -> [UInt8] {
    Array(bitTorrentV2InfoHash(infoDictBytes)[0..<20])
}
