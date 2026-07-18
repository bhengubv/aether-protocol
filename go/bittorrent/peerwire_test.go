// SPDX-License-Identifier: MIT

package bittorrent

import (
	"bytes"
	"testing"
)

func TestHandshake_Roundtrip(t *testing.T) {
	var h Handshake
	h.Reserved = DefaultReserved()
	for i := range h.InfoHash {
		h.InfoHash[i] = byte(i)
	}
	for i := range h.PeerID {
		h.PeerID[i] = byte(200 - i)
	}
	wire := h.ToBytes()
	if len(wire) != 68 {
		t.Fatalf("handshake length %d", len(wire))
	}
	if wire[0] != 19 || string(wire[1:20]) != protocolString {
		t.Fatalf("bad handshake prefix")
	}
	back, err := ParseHandshake(wire)
	if err != nil {
		t.Fatal(err)
	}
	if back.InfoHash != h.InfoHash || back.PeerID != h.PeerID || back.Reserved != h.Reserved {
		t.Fatalf("handshake roundtrip mismatch")
	}
	if !back.SupportsExtended() || !back.SupportsDht() {
		t.Fatalf("default reserved should advertise extended + DHT")
	}
}

func TestPeerMessage_ByteExactFraming(t *testing.T) {
	cases := []struct {
		msg  PeerMessage
		want []byte
	}{
		{KeepAlive(), []byte{0, 0, 0, 0}},
		{Choke(), []byte{0, 0, 0, 1, 0}},
		{Unchoke(), []byte{0, 0, 0, 1, 1}},
		{Interested(), []byte{0, 0, 0, 1, 2}},
		{Have(5), []byte{0, 0, 0, 5, 4, 0, 0, 0, 5}},
		{Request(1, 0, 16384), []byte{0, 0, 0, 13, 6, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0x40, 0}},
		{Port(6881), []byte{0, 0, 0, 3, 9, 0x1a, 0xe1}},
	}
	for _, c := range cases {
		got := c.msg.ToBytes()
		if !bytes.Equal(got, c.want) {
			t.Fatalf("framing: got % x want % x", got, c.want)
		}
		// Round-trip through ParseFrame.
		back, n, err := ParseFrame(got)
		if err != nil || n != len(got) {
			t.Fatalf("parse frame: %v consumed %d", err, n)
		}
		if back.HasID != c.msg.HasID || back.ID != c.msg.ID || !bytes.Equal(back.Payload, c.msg.Payload) {
			t.Fatalf("frame roundtrip mismatch for % x", c.want)
		}
	}
}

func TestPeerMessage_Decoders(t *testing.T) {
	idx, err := Have(9).HavePieceIndex()
	if err != nil || idx != 9 {
		t.Fatalf("have decode %d %v", idx, err)
	}
	i, b, l, err := Request(2, 16384, 16384).BlockRef()
	if err != nil || i != 2 || b != 16384 || l != 16384 {
		t.Fatalf("request decode %d %d %d %v", i, b, l, err)
	}
	pi, pb, block, err := Piece(3, 0, []byte{1, 2, 3}).PieceBlock()
	if err != nil || pi != 3 || pb != 0 || !bytes.Equal(block, []byte{1, 2, 3}) {
		t.Fatalf("piece decode %v", err)
	}
}

func TestBitfield_MsbFirst(t *testing.T) {
	b := NewBitfield(10)
	b.Set(0)
	b.Set(9)
	if b.ToBytes()[0] != 0x80 {
		t.Fatalf("piece 0 should be 0x80, got 0x%02x", b.ToBytes()[0])
	}
	if !b.Get(0) || !b.Get(9) || b.Get(1) {
		t.Fatalf("bit get wrong")
	}
	if b.PopCount() != 2 {
		t.Fatalf("popcount %d", b.PopCount())
	}
	if b.HasAll() {
		t.Fatalf("should not have all")
	}
	// second byte holds pieces 8 and 9 in its two high bits.
	if b.ToBytes()[1]&0x40 == 0 {
		t.Fatalf("piece 9 should set 0x40 of byte 1, got 0x%02x", b.ToBytes()[1])
	}
}
