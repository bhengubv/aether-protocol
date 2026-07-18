// SPDX-License-Identifier: MIT

package bittorrent

import (
	"bytes"
	"testing"
)

func TestRarestFirstPicker_PicksRarest(t *testing.T) {
	p := NewPicker(4)
	// availability: piece0=1 (A), piece1=2 (A,B), piece2=2 (A,B), piece3=1 (B)
	for _, i := range []int{0, 1, 2} {
		p.PeerHas("A", i)
	}
	for _, i := range []int{1, 2, 3} {
		p.PeerHas("B", i)
	}

	// A has {0,1,2}; rarest is piece 0 (availability 1).
	if got := p.PickFor("A"); got != 0 {
		t.Fatalf("first pick got %d want 0", got)
	}
	p.SetHave(0)
	// Now A's remaining {1,2} both availability 2 → first index 1.
	if got := p.PickFor("A"); got != 1 {
		t.Fatalf("second pick got %d want 1", got)
	}
	// Piece 1 is now in-flight; next pick for A is piece 2.
	if got := p.PickFor("A"); got != 2 {
		t.Fatalf("third pick got %d want 2", got)
	}
	// Release piece 1 → it becomes pickable again for B (rarest of B's {1,3}? both avail... 1=2,3=1 → 3).
	p.Release(1)
	if got := p.PickFor("B"); got != 3 {
		t.Fatalf("B pick got %d want 3 (piece3 availability 1)", got)
	}
}

func TestPieceStore_FromContentAssembles(t *testing.T) {
	data := make([]byte, 5000)
	for i := range data {
		data[i] = byte(i * 7)
	}
	s := PieceStoreFromContent(data, 1024) // 5 pieces (4x1024 + 904)
	if s.PieceCount() != 5 {
		t.Fatalf("piece count %d", s.PieceCount())
	}
	if !s.IsComplete() || !s.BuildBitfield().HasAll() {
		t.Fatalf("store should be complete")
	}
	if s.LengthOfPiece(4) != 5000-4*1024 {
		t.Fatalf("last piece length %d", s.LengthOfPiece(4))
	}
	got, ok := s.Assemble()
	if !ok || !bytes.Equal(got, data) {
		t.Fatalf("assemble mismatch")
	}
}

func TestPieceStore_VerifiesOnComplete(t *testing.T) {
	data := make([]byte, 2048)
	for i := range data {
		data[i] = byte(i)
	}
	src := PieceStoreFromContent(data, 1024)

	// A fresh empty store with the same hashes: accept correct piece bytes, reject tampered.
	dst := NewPieceStore(1024, int64(len(data)), src.pieceHashes)
	good, _ := src.ReadBlock(0, 0, 1024)
	if !dst.TryComplete(0, good) {
		t.Fatalf("correct piece should verify")
	}
	bad := make([]byte, 1024)
	if dst.TryComplete(1, bad) {
		t.Fatalf("tampered piece should be rejected")
	}
	if dst.IsComplete() {
		t.Fatalf("store should not be complete with 1/2 pieces")
	}
}
