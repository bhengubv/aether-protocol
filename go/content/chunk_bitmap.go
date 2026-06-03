// SPDX-License-Identifier: MIT
//
// Package content implements the ChunkBitmap wire format for the Aether
// Chunk Shuffle / Self-Assembling Peer Interleaving protocol.
//
// Wire format:
//   - JSON, snake_case property names.
//   - Bitset: LSB-first within each byte — bit i is set in byte (i/8), at
//     position (i%8). Length = ceil(chunk_count / 8).
//   - Bitset transmitted as standard Base64 (with padding).
//   - Field order in canonical JSON: root_hash, chunk_count, have_bitset, generation.
package content

import (
	"encoding/base64"
	"fmt"
	"strings"
)

// BitsetEncode encodes a set of present-chunk indices into an LSB-first
// compact bitset. Bit i is set in byte (i>>3) at bit-position (i&7).
//
// Returns a byte slice of length ceil(chunkCount/8), with all trailing bits
// zero. Returns an empty slice if chunkCount <= 0. Returns an error if any
// index in haveIndices is outside [0, chunkCount).
func BitsetEncode(chunkCount int, haveIndices []int) ([]byte, error) {
	if chunkCount <= 0 {
		return []byte{}, nil
	}
	buf := make([]byte, (chunkCount+7)/8)
	for _, i := range haveIndices {
		if i < 0 || i >= chunkCount {
			return nil, fmt.Errorf("aether/content: index %d is out of range [0, %d)", i, chunkCount)
		}
		buf[i>>3] |= 1 << (i & 7)
	}
	return buf, nil
}

// BitsetDecode decodes a compact LSB-first bitset into a sorted list of set
// chunk indices. Bits at positions >= chunkCount are ignored.
func BitsetDecode(bitset []byte, chunkCount int) []int {
	result := []int{}
	limit := chunkCount
	if max := len(bitset) * 8; limit > max {
		limit = max
	}
	for i := 0; i < limit; i++ {
		if bitset[i>>3]&(1<<(i&7)) != 0 {
			result = append(result, i)
		}
	}
	return result
}

// MarshalJSON produces the canonical wire JSON for a ChunkBitmapPayload.
// Fields are emitted in the fixed order required by the wire spec:
// root_hash → chunk_count → have_bitset → generation.
//
// haveBitset is encoded using standard Base64 (RFC 4648) with padding.
func MarshalJSON(rootHash string, chunkCount int, haveBitset []byte, generation uint32) string {
	b64 := base64.StdEncoding.EncodeToString(haveBitset)
	var sb strings.Builder
	sb.WriteString(`{"root_hash":`)
	sb.WriteByte('"')
	sb.WriteString(rootHash)
	sb.WriteByte('"')
	sb.WriteString(`,"chunk_count":`)
	fmt.Fprintf(&sb, "%d", chunkCount)
	sb.WriteString(`,"have_bitset":`)
	sb.WriteByte('"')
	sb.WriteString(b64)
	sb.WriteByte('"')
	sb.WriteString(`,"generation":`)
	fmt.Fprintf(&sb, "%d", generation)
	sb.WriteByte('}')
	return sb.String()
}
