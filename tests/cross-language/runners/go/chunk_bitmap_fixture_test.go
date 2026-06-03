// SPDX-License-Identifier: MIT
// Cross-language ChunkBitmap wire-format fixture verifier — Go runner.
//
// Reads fixtures/content/chunk_bitmap_vectors.json and verifies that this
// implementation produces bit-identical bitsets and JSON payloads for each
// pinned test vector.
//
// The same fixture corpus is exercised by the C#, Python, TypeScript, Rust,
// Kotlin, Swift, and C runners. Any divergence here == a cross-language
// wire-break that must be fixed before shipping.
//
// Wire format:
//   - JSON, snake_case property names.
//   - Bitset: LSB-first within each byte — bit i is set in byte (i/8), at
//     position (i%8). Length = ceil(chunk_count / 8).
//   - Bitset transmitted as standard Base64 (with padding).
//   - Field order in canonical JSON: root_hash, chunk_count, have_bitset,
//     generation.
package chunkmaprunner

import (
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// ── Fixture model ─────────────────────────────────────────────────────────────

type chunkBitmapVector struct {
	Name             string `json:"name"`
	Description      string `json:"description"`
	RootHash         string `json:"root_hash"`
	ChunkCount       int    `json:"chunk_count"`
	HaveIndices      []int  `json:"have_indices"`
	HaveBitsetHex    string `json:"have_bitset_hex"`
	HaveBitsetBase64 string `json:"have_bitset_base64"`
	Generation       uint32 `json:"generation"`
	ExpectedJSON     string `json:"expected_json"`
}

// ── Fixture loader ────────────────────────────────────────────────────────────

// fixturePath walks upward from the directory containing this test file
// until it finds fixtures/content/chunk_bitmap_vectors.json, up to maxLevels.
func fixturePath(maxLevels int) (string, error) {
	// Start from the directory of this source file at compile-time.
	// At test runtime the working directory is set to the package directory,
	// so we start there.
	dir, err := os.Getwd()
	if err != nil {
		return "", err
	}
	for i := 0; i < maxLevels; i++ {
		candidate := filepath.Join(dir, "fixtures", "content", "chunk_bitmap_vectors.json")
		if _, err := os.Stat(candidate); err == nil {
			return candidate, nil
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			break
		}
		dir = parent
	}
	return "", fmt.Errorf("could not locate fixtures/content/chunk_bitmap_vectors.json walking up from %s", dir)
}

func loadVectors(t *testing.T) []chunkBitmapVector {
	t.Helper()
	path, err := fixturePath(10)
	if err != nil {
		t.Fatalf("fixture loader: %v", err)
	}
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("fixture loader: reading %s: %v", path, err)
	}
	var vectors []chunkBitmapVector
	if err := json.Unmarshal(data, &vectors); err != nil {
		t.Fatalf("fixture loader: parsing JSON: %v", err)
	}
	return vectors
}

// ── Inline BitsetCodec ────────────────────────────────────────────────────────

// bitsetEncode encodes a set of present-chunk indices into an LSB-first
// compact bitset. Bit i is set in byte (i>>3) at bit-position (i&7).
// Returns ceil(chunkCount/8) bytes. Returns an error if any index is out of
// [0, chunkCount). Returns an empty slice if chunkCount <= 0.
func bitsetEncode(chunkCount int, haveIndices []int) ([]byte, error) {
	if chunkCount <= 0 {
		return []byte{}, nil
	}
	buf := make([]byte, (chunkCount+7)/8)
	for _, i := range haveIndices {
		if i < 0 || i >= chunkCount {
			return nil, fmt.Errorf("index %d is out of range [0, %d)", i, chunkCount)
		}
		buf[i>>3] |= 1 << (i & 7)
	}
	return buf, nil
}

// bitsetDecode decodes a compact LSB-first bitset into a sorted list of set
// chunk indices. Bits at positions >= chunkCount are ignored.
func bitsetDecode(bitset []byte, chunkCount int) []int {
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

// ── Inline JSON serialiser ────────────────────────────────────────────────────

// marshalJSON produces the canonical wire JSON for a ChunkBitmapPayload.
// Fields are emitted in the fixed order: root_hash, chunk_count, have_bitset,
// generation. haveBitset is standard Base64 (RFC 4648) with padding.
func marshalJSON(rootHash string, chunkCount int, haveBitset []byte, generation uint32) string {
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

// ── Tests — 5 subtests × 4 vectors = 20 subtests ─────────────────────────────

// TestChunkBitmapFixtures is the cross-language fixture runner.
// It runs 5 sub-tests for each of the 4 canonical vectors (20 total).
func TestChunkBitmapFixtures(t *testing.T) {
	t.Parallel()
	vectors := loadVectors(t)

	for _, v := range vectors {
		v := v // capture
		t.Run(v.Name, func(t *testing.T) {
			t.Parallel()

			// ── Subtest 1: Encode_ProducesCorrectHex ─────────────────────────
			t.Run("Encode_ProducesCorrectHex", func(t *testing.T) {
				bitset, err := bitsetEncode(v.ChunkCount, v.HaveIndices)
				if err != nil {
					t.Fatalf("bitsetEncode: %v", err)
				}
				got := hex.EncodeToString(bitset)
				if got != v.HaveBitsetHex {
					t.Errorf("hex mismatch: got %q, want %q", got, v.HaveBitsetHex)
				}
			})

			// ── Subtest 2: Encode_ProducesCorrectBase64 ───────────────────────
			t.Run("Encode_ProducesCorrectBase64", func(t *testing.T) {
				bitset, err := bitsetEncode(v.ChunkCount, v.HaveIndices)
				if err != nil {
					t.Fatalf("bitsetEncode: %v", err)
				}
				got := base64.StdEncoding.EncodeToString(bitset)
				if got != v.HaveBitsetBase64 {
					t.Errorf("base64 mismatch: got %q, want %q", got, v.HaveBitsetBase64)
				}
			})

			// ── Subtest 3: Decode_RecoversCorrectIndices ──────────────────────
			t.Run("Decode_RecoversCorrectIndices", func(t *testing.T) {
				bitset, err := base64.StdEncoding.DecodeString(v.HaveBitsetBase64)
				if err != nil {
					t.Fatalf("base64 decode: %v", err)
				}
				recovered := bitsetDecode(bitset, v.ChunkCount)
				if len(recovered) != len(v.HaveIndices) {
					t.Fatalf("index count: got %d, want %d", len(recovered), len(v.HaveIndices))
				}
				for j := range v.HaveIndices {
					if recovered[j] != v.HaveIndices[j] {
						t.Errorf("index[%d]: got %d, want %d", j, recovered[j], v.HaveIndices[j])
					}
				}
			})

			// ── Subtest 4: JsonSerialize_MatchesExpected ──────────────────────
			t.Run("JsonSerialize_MatchesExpected", func(t *testing.T) {
				bitset, err := bitsetEncode(v.ChunkCount, v.HaveIndices)
				if err != nil {
					t.Fatalf("bitsetEncode: %v", err)
				}
				got := marshalJSON(v.RootHash, v.ChunkCount, bitset, v.Generation)
				if got != v.ExpectedJSON {
					t.Errorf("JSON mismatch:\n  got:  %s\n  want: %s", got, v.ExpectedJSON)
				}
			})

			// ── Subtest 5: Encode_BitsetLengthIsCeilDiv8 ─────────────────────
			t.Run("Encode_BitsetLengthIsCeilDiv8", func(t *testing.T) {
				bitset, err := bitsetEncode(v.ChunkCount, v.HaveIndices)
				if err != nil {
					t.Fatalf("bitsetEncode: %v", err)
				}
				want := (v.ChunkCount + 7) / 8
				if len(bitset) != want {
					t.Errorf("length: got %d, want %d (ceil(%d/8))", len(bitset), want, v.ChunkCount)
				}
			})
		})
	}
}

// TestChunkBitmapTrailingBitsAreZero verifies that bits beyond chunk_count in
// the last byte are zero for all fixture vectors. Kept as a top-level test
// (not nested inside the 5-subtest loop above) to mirror the C# structure.
func TestChunkBitmapTrailingBitsAreZero(t *testing.T) {
	t.Parallel()
	vectors := loadVectors(t)

	for _, v := range vectors {
		v := v
		t.Run(v.Name+"/Encode_TrailingBitsAreZero", func(t *testing.T) {
			t.Parallel()
			bitset, err := bitsetEncode(v.ChunkCount, v.HaveIndices)
			if err != nil {
				t.Fatalf("bitsetEncode: %v", err)
			}
			if len(bitset) == 0 {
				return
			}
			trailingBits := v.ChunkCount % 8
			if trailingBits == 0 {
				return
			}
			lastByte := bitset[len(bitset)-1]
			validMask := byte((1 << trailingBits) - 1)
			trailingSet := lastByte & ^validMask
			if trailingSet != 0 {
				t.Errorf("trailing bits non-zero: lastByte=0x%02x validMask=0x%02x trailingSet=0x%02x",
					lastByte, validMask, trailingSet)
			}
		})
	}
}
