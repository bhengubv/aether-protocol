// SPDX-License-Identifier: MIT

package bittorrent

import "crypto/sha256"

// MerkleBlockSize is the BitTorrent v2 leaf-block size (BEP-52).
const MerkleBlockSize = 16384

// MerkleRoot computes the SHA-256 merkle root of data split into 16 KiB leaf blocks
// (BEP-52): each block SHA-256'd, the leaf layer zero-padded (a zero hash is 32 zero
// bytes) to the next power of two, internal nodes SHA-256(left||right). Byte-identical
// to the C# MerkleTree.ComputeRoot.
func MerkleRoot(data []byte) []byte {
	return MerkleRootBlock(data, MerkleBlockSize)
}

// MerkleRootBlock is MerkleRoot with an explicit block size.
func MerkleRootBlock(data []byte, blockSize int) []byte {
	if blockSize <= 0 {
		panic("block size must be positive")
	}
	var leaves [][]byte
	for i := 0; i < len(data); i += blockSize {
		end := i + blockSize
		if end > len(data) {
			end = len(data)
		}
		h := sha256.Sum256(data[i:end])
		leaves = append(leaves, h[:])
	}
	if len(leaves) == 0 {
		return make([]byte, 32) // empty content → zero root
	}
	return merkleRootOf(leaves)
}

// merkleRootOf combines leaf hashes into a root, zero-padding to the next power of two.
func merkleRootOf(leafHashes [][]byte) []byte {
	if len(leafHashes) == 0 {
		return make([]byte, 32)
	}
	level := make([][]byte, len(leafHashes))
	copy(level, leafHashes)

	width := 1
	for width < len(level) {
		width <<= 1
	}
	zero := make([]byte, 32)
	for len(level) < width {
		level = append(level, zero)
	}

	for len(level) > 1 {
		next := make([][]byte, 0, len(level)/2)
		for i := 0; i < len(level); i += 2 {
			var combined [64]byte
			copy(combined[0:32], level[i])
			copy(combined[32:64], level[i+1])
			h := sha256.Sum256(combined[:])
			next = append(next, h[:])
		}
		level = next
	}
	return level[0]
}

// BitTorrentV2InfoHash is the full 32-byte v2 info-hash: SHA-256 of the bencoded info dict.
func BitTorrentV2InfoHash(infoDictBytes []byte) []byte {
	h := sha256.Sum256(infoDictBytes)
	return h[:]
}

// BitTorrentV2InfoHashTruncated is the v2 info-hash truncated to 20 bytes.
func BitTorrentV2InfoHashTruncated(infoDictBytes []byte) []byte {
	return BitTorrentV2InfoHash(infoDictBytes)[:20]
}
