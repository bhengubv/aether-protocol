// SPDX-License-Identifier: MIT

package bittorrent

import (
	"bytes"
	"crypto/sha1"
)

// PieceStore holds verified pieces of a torrent in memory, verifying each against its
// SHA-1 before accepting it, and can serve blocks or assemble the whole content.
type PieceStore struct {
	pieceLength int
	totalLength int64
	pieceHashes [][]byte
	pieces      map[int][]byte
}

// NewPieceStore creates an empty store for the given layout.
func NewPieceStore(pieceLength int, totalLength int64, pieceHashes [][]byte) *PieceStore {
	return &PieceStore{
		pieceLength: pieceLength,
		totalLength: totalLength,
		pieceHashes: pieceHashes,
		pieces:      map[int][]byte{},
	}
}

// PieceCount is the number of pieces.
func (s *PieceStore) PieceCount() int { return len(s.pieceHashes) }

// LengthOfPiece returns the byte length of a piece (the last may be short).
func (s *PieceStore) LengthOfPiece(i int) int {
	if i < 0 || i >= len(s.pieceHashes) {
		return 0
	}
	if i == len(s.pieceHashes)-1 {
		return int(s.totalLength - int64(i)*int64(s.pieceLength))
	}
	return s.pieceLength
}

// Has reports whether a verified piece is present.
func (s *PieceStore) Has(i int) bool { _, ok := s.pieces[i]; return ok }

// TryComplete verifies data against the piece's SHA-1 and stores it on success.
func (s *PieceStore) TryComplete(i int, data []byte) bool {
	if i < 0 || i >= len(s.pieceHashes) {
		return false
	}
	if len(data) != s.LengthOfPiece(i) {
		return false
	}
	h := sha1.Sum(data)
	if !bytes.Equal(h[:], s.pieceHashes[i]) {
		return false
	}
	cp := make([]byte, len(data))
	copy(cp, data)
	s.pieces[i] = cp
	return true
}

// ReadBlock returns a block from a stored piece.
func (s *PieceStore) ReadBlock(i, begin, length int) ([]byte, bool) {
	p, ok := s.pieces[i]
	if !ok || begin < 0 || length < 0 || begin+length > len(p) {
		return nil, false
	}
	out := make([]byte, length)
	copy(out, p[begin:begin+length])
	return out, true
}

// BuildBitfield returns a bitfield of currently-held pieces.
func (s *PieceStore) BuildBitfield() *Bitfield {
	bf := NewBitfield(len(s.pieceHashes))
	for i := range s.pieceHashes {
		if s.Has(i) {
			bf.Set(i)
		}
	}
	return bf
}

// IsComplete reports whether every piece is present.
func (s *PieceStore) IsComplete() bool { return len(s.pieces) == len(s.pieceHashes) }

// Assemble returns the full content if complete.
func (s *PieceStore) Assemble() ([]byte, bool) {
	if !s.IsComplete() {
		return nil, false
	}
	out := make([]byte, s.totalLength)
	off := 0
	for i := range s.pieceHashes {
		copy(out[off:], s.pieces[i])
		off += len(s.pieces[i])
	}
	return out, true
}

// PieceStoreFromContent builds a complete store from raw content (a seeder's side).
func PieceStoreFromContent(data []byte, pieceLength int) *PieceStore {
	pieceCount := (len(data) + pieceLength - 1) / pieceLength
	hashes := make([][]byte, pieceCount)
	s := NewPieceStore(pieceLength, int64(len(data)), nil)
	for i := 0; i < pieceCount; i++ {
		start := i * pieceLength
		end := start + pieceLength
		if end > len(data) {
			end = len(data)
		}
		h := sha1.Sum(data[start:end])
		hh := make([]byte, 20)
		copy(hh, h[:])
		hashes[i] = hh
		cp := make([]byte, end-start)
		copy(cp, data[start:end])
		s.pieces[i] = cp
	}
	s.pieceHashes = hashes
	return s
}
