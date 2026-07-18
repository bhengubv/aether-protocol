// SPDX-License-Identifier: MIT

package bittorrent

import (
	"bytes"
	"crypto/sha256"
	"testing"
)

func TestUtpPacket_ByteExactHeader(t *testing.T) {
	p := UtpPacket{
		Type:            UtpData,
		ConnectionID:    0x1234,
		TimestampMicros: 0x00ABCDEF,
		WindowSize:      0x00010000,
		SeqNr:           5,
		AckNr:           4,
		Payload:         []byte{0xDE, 0xAD},
	}
	wire := p.ToBytes()
	if len(wire) != 22 {
		t.Fatalf("len %d", len(wire))
	}
	if wire[0] != 0x01 || wire[1] != 0x00 {
		t.Fatalf("type/ext byte0=0x%02x byte1=0x%02x", wire[0], wire[1])
	}
	if !bytes.Equal(wire[2:4], []byte{0x12, 0x34}) {
		t.Fatalf("conn id % x", wire[2:4])
	}
	if !bytes.Equal(wire[4:8], []byte{0x00, 0xAB, 0xCD, 0xEF}) {
		t.Fatalf("timestamp % x", wire[4:8])
	}
	if !bytes.Equal(wire[16:18], []byte{0x00, 0x05}) || !bytes.Equal(wire[18:20], []byte{0x00, 0x04}) {
		t.Fatalf("seq/ack % x % x", wire[16:18], wire[18:20])
	}
	if !bytes.Equal(wire[20:22], []byte{0xDE, 0xAD}) {
		t.Fatalf("payload % x", wire[20:22])
	}
}

func TestUtpPacket_RoundtripAndExtensions(t *testing.T) {
	for _, ty := range []UtpPacketType{UtpSyn, UtpState, UtpFin, UtpReset, UtpData} {
		p := UtpPacket{Type: ty, ConnectionID: 42, SeqNr: 1, WindowSize: 1024}
		back, err := ParseUtpPacket(p.ToBytes())
		if err != nil || back.Type != ty || back.ConnectionID != 42 || back.SeqNr != 1 {
			t.Fatalf("roundtrip %d: %v", ty, err)
		}
	}
	// Extension chain [next=0][len=4][4 bytes] then payload → payload located after it.
	base := UtpPacket{Type: UtpData, ConnectionID: 1, SeqNr: 1}.ToBytes()
	withExt := make([]byte, 20+6+3)
	copy(withExt, base[:20])
	withExt[1] = 1 // first extension = selective ack
	withExt[20] = 0
	withExt[21] = 4
	withExt[26] = 0xAA
	withExt[27] = 0xBB
	withExt[28] = 0xCC
	parsed, err := ParseUtpPacket(withExt)
	if err != nil || !bytes.Equal(parsed.Payload, []byte{0xAA, 0xBB, 0xCC}) {
		t.Fatalf("extension payload: %v % x", err, parsed.Payload)
	}
}

func TestUtpPacket_Rejects(t *testing.T) {
	if _, err := ParseUtpPacket(make([]byte, 10)); err == nil {
		t.Fatal("short packet should reject")
	}
	bad := UtpPacket{Type: UtpSyn}.ToBytes()
	bad[0] = (4 << 4) | 2 // version 2
	if _, err := ParseUtpPacket(bad); err == nil {
		t.Fatal("wrong version should reject")
	}
}

func filled(n int) []byte {
	b := make([]byte, n)
	for i := range b {
		b[i] = byte(i*7 + 1)
	}
	return b
}

func TestMerkle_SingleBlockIsItsHash(t *testing.T) {
	data := filled(100)
	h := sha256.Sum256(data)
	if !bytes.Equal(MerkleRoot(data), h[:]) {
		t.Fatal("single-block root mismatch")
	}
}

func TestMerkle_TwoBlocks(t *testing.T) {
	data := filled(MerkleBlockSize + 50)
	h0 := sha256.Sum256(data[:MerkleBlockSize])
	h1 := sha256.Sum256(data[MerkleBlockSize:])
	want := sha256.Sum256(append(append([]byte{}, h0[:]...), h1[:]...))
	if !bytes.Equal(MerkleRoot(data), want[:]) {
		t.Fatal("two-block root mismatch")
	}
}

func TestMerkle_ThreeBlocksPadToFour(t *testing.T) {
	data := filled(2*MerkleBlockSize + 10)
	h0 := sha256.Sum256(data[:MerkleBlockSize])
	h1 := sha256.Sum256(data[MerkleBlockSize : 2*MerkleBlockSize])
	h2 := sha256.Sum256(data[2*MerkleBlockSize:])
	zero := make([]byte, 32)
	left := sha256.Sum256(append(append([]byte{}, h0[:]...), h1[:]...))
	right := sha256.Sum256(append(append([]byte{}, h2[:]...), zero...))
	want := sha256.Sum256(append(append([]byte{}, left[:]...), right[:]...))
	if !bytes.Equal(MerkleRoot(data), want[:]) {
		t.Fatal("three-block root mismatch")
	}
}

func TestMerkle_EmptyIsZeroRoot(t *testing.T) {
	if !bytes.Equal(MerkleRoot(nil), make([]byte, 32)) {
		t.Fatal("empty root should be 32 zero bytes")
	}
}

func TestV2InfoHash(t *testing.T) {
	info := NewBDict()
	_ = info.Add("meta version", BInt(2))
	_ = info.Add("name", BStr("v2.bin"))
	_ = info.Add("piece length", BInt(65536))
	b := Encode(info)
	h := sha256.Sum256(b)
	if !bytes.Equal(BitTorrentV2InfoHash(b), h[:]) {
		t.Fatal("v2 info-hash mismatch")
	}
	if len(BitTorrentV2InfoHashTruncated(b)) != 20 {
		t.Fatal("truncated should be 20 bytes")
	}
}
