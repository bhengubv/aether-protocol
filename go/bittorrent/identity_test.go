// SPDX-License-Identifier: MIT

package bittorrent

import (
	"bytes"
	"crypto/sha1"
	"encoding/base32"
	"encoding/hex"
	"testing"
)

func TestMetainfo_BuildParseRoundtrip(t *testing.T) {
	data := make([]byte, 70000)
	for i := range data {
		data[i] = byte(i*31 + 7)
	}
	const pieceLength = 32768

	tb, err := BuildSingleFileTorrent("payload.bin", data, pieceLength, "http://tracker.example/announce")
	if err != nil {
		t.Fatal(err)
	}
	m, err := ParseTorrent(tb)
	if err != nil {
		t.Fatal(err)
	}

	if m.Name != "payload.bin" {
		t.Errorf("name %q", m.Name)
	}
	if m.TotalLength != int64(len(data)) {
		t.Errorf("total %d", m.TotalLength)
	}
	if m.PieceLength != pieceLength {
		t.Errorf("piece length %d", m.PieceLength)
	}
	if !m.IsSingleFile {
		t.Errorf("expected single file")
	}
	if len(m.AnnounceURLs) != 1 || m.AnnounceURLs[0] != "http://tracker.example/announce" {
		t.Errorf("announce %v", m.AnnounceURLs)
	}

	want := (len(data) + pieceLength - 1) / pieceLength
	if len(m.PieceHashes) != want {
		t.Fatalf("pieces %d want %d", len(m.PieceHashes), want)
	}
	for i := 0; i < want; i++ {
		start := i * pieceLength
		end := start + pieceLength
		if end > len(data) {
			end = len(data)
		}
		h := sha1.Sum(data[start:end])
		if !bytes.Equal(m.PieceHashes[i], h[:]) {
			t.Fatalf("piece %d hash mismatch", i)
		}
	}
}

func TestMetainfo_InfoHashDeterministic(t *testing.T) {
	data := []byte("the quick brown fox jumps over the lazy dog")
	a, _ := BuildSingleFileTorrent("f", data, 16384, "")
	b, _ := BuildSingleFileTorrent("f", data, 16384, "")
	ma, _ := ParseTorrent(a)
	mb, _ := ParseTorrent(b)
	if ma.InfoHashV1Hex() != mb.InfoHashV1Hex() {
		t.Fatalf("info-hash not deterministic: %s vs %s", ma.InfoHashV1Hex(), mb.InfoHashV1Hex())
	}
	if len(ma.InfoHashV1Hex()) != 40 {
		t.Fatalf("info-hash hex length %d", len(ma.InfoHashV1Hex()))
	}
}

func TestMagnet_HexAndBase32ResolveToSameHash(t *testing.T) {
	hexHash := "0123456789abcdef0123456789abcdef01234567"
	m1, err := ParseMagnet("magnet:?xt=urn:btih:" + hexHash + "&dn=test&tr=http%3A%2F%2Ftracker%2Fannounce")
	if err != nil {
		t.Fatal(err)
	}
	if m1.InfoHashHex() != hexHash {
		t.Fatalf("hex magnet hash %s", m1.InfoHashHex())
	}
	if m1.DisplayName != "test" {
		t.Errorf("dn %q", m1.DisplayName)
	}
	if len(m1.Trackers) != 1 || m1.Trackers[0] != "http://tracker/announce" {
		t.Errorf("tr %v", m1.Trackers)
	}

	raw, _ := hex.DecodeString(hexHash)
	b32 := base32.StdEncoding.WithPadding(base32.NoPadding).EncodeToString(raw)
	m2, err := ParseMagnet("magnet:?xt=urn:btih:" + b32)
	if err != nil {
		t.Fatal(err)
	}
	if m2.InfoHash != m1.InfoHash {
		t.Fatalf("base32 hash != hex hash")
	}
}

func TestMagnet_Rejects(t *testing.T) {
	for _, bad := range []string{
		"http://not-a-magnet",
		"magnet:?dn=noxt",
		"magnet:?xt=urn:btih:tooshort",
	} {
		if _, err := ParseMagnet(bad); err == nil {
			t.Fatalf("expected reject for %q", bad)
		}
	}
}
