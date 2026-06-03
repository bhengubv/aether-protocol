// SPDX-License-Identifier: MIT
package content

import (
	"encoding/base64"
	"encoding/hex"
	"testing"
)

// ── TestBitsetEncode ──────────────────────────────────────────────────────────

func TestBitsetEncode(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name           string
		chunkCount     int
		haveIndices    []int
		expectHex      string
		expectBase64   string
	}{
		{
			name:         "chunk_bitmap_sparse",
			chunkCount:   8,
			haveIndices:  []int{0, 2, 5},
			expectHex:    "25",
			expectBase64: "JQ==",
		},
		{
			name:         "chunk_bitmap_empty",
			chunkCount:   8,
			haveIndices:  []int{},
			expectHex:    "00",
			expectBase64: "AA==",
		},
		{
			name:         "chunk_bitmap_full",
			chunkCount:   8,
			haveIndices:  []int{0, 1, 2, 3, 4, 5, 6, 7},
			expectHex:    "ff",
			expectBase64: "/w==",
		},
		{
			name:         "chunk_bitmap_16chunks_partial",
			chunkCount:   16,
			haveIndices:  []int{0, 8},
			expectHex:    "0101",
			expectBase64: "AQE=",
		},
	}

	for _, tc := range cases {
		tc := tc
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			bitset, err := BitsetEncode(tc.chunkCount, tc.haveIndices)
			if err != nil {
				t.Fatalf("BitsetEncode returned unexpected error: %v", err)
			}
			gotHex := hex.EncodeToString(bitset)
			if gotHex != tc.expectHex {
				t.Errorf("hex mismatch: got %q, want %q", gotHex, tc.expectHex)
			}
			gotB64 := base64.StdEncoding.EncodeToString(bitset)
			if gotB64 != tc.expectBase64 {
				t.Errorf("base64 mismatch: got %q, want %q", gotB64, tc.expectBase64)
			}
		})
	}
}

// ── TestBitsetDecode ──────────────────────────────────────────────────────────

func TestBitsetDecode(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name        string
		chunkCount  int
		haveIndices []int
	}{
		{"chunk_bitmap_sparse", 8, []int{0, 2, 5}},
		{"chunk_bitmap_empty", 8, []int{}},
		{"chunk_bitmap_full", 8, []int{0, 1, 2, 3, 4, 5, 6, 7}},
		{"chunk_bitmap_16chunks_partial", 16, []int{0, 8}},
	}

	for _, tc := range cases {
		tc := tc
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			bitset, err := BitsetEncode(tc.chunkCount, tc.haveIndices)
			if err != nil {
				t.Fatalf("BitsetEncode: %v", err)
			}
			recovered := BitsetDecode(bitset, tc.chunkCount)

			// Compare lengths first.
			if len(recovered) != len(tc.haveIndices) {
				t.Fatalf("length mismatch: got %d indices, want %d", len(recovered), len(tc.haveIndices))
			}
			// BitsetDecode returns indices in ascending order; tc.haveIndices
			// are already sorted in the fixture table above.
			for j, idx := range tc.haveIndices {
				if recovered[j] != idx {
					t.Errorf("index[%d]: got %d, want %d", j, recovered[j], idx)
				}
			}
		})
	}
}

// ── TestMarshalJSON ───────────────────────────────────────────────────────────

func TestMarshalJSON(t *testing.T) {
	t.Parallel()

	const sha256Empty = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
	const sha256ABC = "ba7816bf8f01cfea414140de5dae2ec73b00361a396177a9cb410ff61f20015a"

	cases := []struct {
		name         string
		rootHash     string
		chunkCount   int
		haveIndices  []int
		generation   uint32
		expectedJSON string
	}{
		{
			name:        "chunk_bitmap_sparse",
			rootHash:    sha256Empty,
			chunkCount:  8,
			haveIndices: []int{0, 2, 5},
			generation:  1,
			expectedJSON: `{"root_hash":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","chunk_count":8,"have_bitset":"JQ==","generation":1}`,
		},
		{
			name:        "chunk_bitmap_empty",
			rootHash:    sha256Empty,
			chunkCount:  8,
			haveIndices: []int{},
			generation:  1,
			expectedJSON: `{"root_hash":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","chunk_count":8,"have_bitset":"AA==","generation":1}`,
		},
		{
			name:        "chunk_bitmap_full",
			rootHash:    sha256Empty,
			chunkCount:  8,
			haveIndices: []int{0, 1, 2, 3, 4, 5, 6, 7},
			generation:  2,
			expectedJSON: `{"root_hash":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","chunk_count":8,"have_bitset":"/w==","generation":2}`,
		},
		{
			name:        "chunk_bitmap_16chunks_partial",
			rootHash:    sha256ABC,
			chunkCount:  16,
			haveIndices: []int{0, 8},
			generation:  5,
			expectedJSON: `{"root_hash":"ba7816bf8f01cfea414140de5dae2ec73b00361a396177a9cb410ff61f20015a","chunk_count":16,"have_bitset":"AQE=","generation":5}`,
		},
	}

	for _, tc := range cases {
		tc := tc
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			bitset, err := BitsetEncode(tc.chunkCount, tc.haveIndices)
			if err != nil {
				t.Fatalf("BitsetEncode: %v", err)
			}
			got := MarshalJSON(tc.rootHash, tc.chunkCount, bitset, tc.generation)
			if got != tc.expectedJSON {
				t.Errorf("JSON mismatch:\n  got:  %s\n  want: %s", got, tc.expectedJSON)
			}
		})
	}
}

// ── TestBitsetLengthIsCeilDiv8 ────────────────────────────────────────────────

func TestBitsetLengthIsCeilDiv8(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name        string
		chunkCount  int
		haveIndices []int
	}{
		{"chunk_bitmap_sparse", 8, []int{0, 2, 5}},
		{"chunk_bitmap_empty", 8, []int{}},
		{"chunk_bitmap_full", 8, []int{0, 1, 2, 3, 4, 5, 6, 7}},
		{"chunk_bitmap_16chunks_partial", 16, []int{0, 8}},
	}

	for _, tc := range cases {
		tc := tc
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			bitset, err := BitsetEncode(tc.chunkCount, tc.haveIndices)
			if err != nil {
				t.Fatalf("BitsetEncode: %v", err)
			}
			want := (tc.chunkCount + 7) / 8
			if len(bitset) != want {
				t.Errorf("length: got %d, want %d (ceil(%d/8))", len(bitset), want, tc.chunkCount)
			}
		})
	}
}

// ── TestTrailingBitsAreZero ───────────────────────────────────────────────────

func TestTrailingBitsAreZero(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name        string
		chunkCount  int
		haveIndices []int
	}{
		{"chunk_bitmap_sparse", 8, []int{0, 2, 5}},
		{"chunk_bitmap_empty", 8, []int{}},
		{"chunk_bitmap_full", 8, []int{0, 1, 2, 3, 4, 5, 6, 7}},
		{"chunk_bitmap_16chunks_partial", 16, []int{0, 8}},
	}

	for _, tc := range cases {
		tc := tc
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()
			bitset, err := BitsetEncode(tc.chunkCount, tc.haveIndices)
			if err != nil {
				t.Fatalf("BitsetEncode: %v", err)
			}
			if len(bitset) == 0 {
				return // zero-chunk content is trivially compliant
			}
			trailingBits := tc.chunkCount % 8 // bits used in the last byte; 0 = full byte
			if trailingBits == 0 {
				return // last byte is fully used — no trailing bits to check
			}
			lastByte := bitset[len(bitset)-1]
			validMask := byte((1 << trailingBits) - 1)
			trailingSet := lastByte & ^validMask
			if trailingSet != 0 {
				t.Errorf("trailing bits non-zero in last byte 0x%02x (validMask 0x%02x, trailingSet 0x%02x)",
					lastByte, validMask, trailingSet)
			}
		})
	}
}
