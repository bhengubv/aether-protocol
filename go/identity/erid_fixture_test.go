// SPDX-License-Identifier: MIT

package identity

import (
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// eridVectors mirrors fixtures/erid/vectors.json — the canonical cross-language parity
// source generated from the C# reference. Every language port MUST reproduce these
// byte-for-byte.
type eridVectors struct {
	SecretASCII   string `json:"secret_ascii"`
	RoutingKeyHex string `json:"routing_key_hex"`
	EpochSeconds  int    `json:"epoch_seconds"`
	EridLength    int    `json:"erid_length"`
	EridsByEpoch  []struct {
		Epoch int64  `json:"epoch"`
		Erid  string `json:"erid"`
	} `json:"erids_by_epoch"`
	DeriveByUnixSeconds []struct {
		Unix int64  `json:"unix"`
		Erid string `json:"erid"`
	} `json:"derive_by_unixseconds"`
	AnnouncementEncodeHex string `json:"announcement_encode_hex"`
}

func loadEridVectors(t *testing.T) eridVectors {
	t.Helper()
	path := filepath.Join("..", "..", "fixtures", "erid", "vectors.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read fixtures: %v", err)
	}
	var v eridVectors
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("parse fixtures: %v", err)
	}
	return v
}

// TestEridByteParityWithCSharpFixture asserts the Go port reproduces the C# reference
// vectors exactly: routing key, per-epoch ERIDs, derive-by-unixseconds, and the AERD
// announcement frame.
func TestEridByteParityWithCSharpFixture(t *testing.T) {
	v := loadEridVectors(t)

	rk, err := DeriveRoutingKey([]byte(v.SecretASCII))
	if err != nil {
		t.Fatal(err)
	}
	if got := hex.EncodeToString(rk); got != v.RoutingKeyHex {
		t.Fatalf("routingKey: got %s want %s", got, v.RoutingKeyHex)
	}

	for _, e := range v.EridsByEpoch {
		got, err := DeriveERIDForEpoch(rk, e.Epoch, v.EridLength)
		if err != nil {
			t.Fatal(err)
		}
		if got != e.Erid {
			t.Fatalf("epoch %d: got %s want %s", e.Epoch, got, e.Erid)
		}
	}

	for _, e := range v.DeriveByUnixSeconds {
		got, err := DeriveERID(rk, e.Unix, v.EpochSeconds, v.EridLength)
		if err != nil {
			t.Fatal(err)
		}
		if got != e.Erid {
			t.Fatalf("unix %d: got %s want %s", e.Unix, got, e.Erid)
		}
	}

	enc, err := EncodeEridAnnouncement(rk, v.EpochSeconds, v.EridLength)
	if err != nil {
		t.Fatal(err)
	}
	if got := hex.EncodeToString(enc); got != v.AnnouncementEncodeHex {
		t.Fatalf("announcement frame: got %s want %s", got, v.AnnouncementEncodeHex)
	}

	// Round-trip the frame back through the decoder.
	dec, ok := TryDecodeEridAnnouncement(enc)
	if !ok {
		t.Fatal("TryDecodeEridAnnouncement rejected a frame it encoded")
	}
	if hex.EncodeToString(dec.RoutingKey) != v.RoutingKeyHex ||
		dec.EpochSeconds != v.EpochSeconds || dec.EridLength != v.EridLength {
		t.Fatalf("decode mismatch: %+v", dec)
	}
}

// TestEridDirectoryResolveAndOutsider proves an established peer resolves a rotating
// ERID both ways, while an outsider holding no routing key cannot.
func TestEridDirectoryResolveAndOutsider(t *testing.T) {
	aKey, _ := DeriveRoutingKey([]byte("identity-A"))
	bKey, _ := DeriveRoutingKey([]byte("identity-B"))
	alice, err := NewEridDirectory(aKey, 0, 0)
	if err != nil {
		t.Fatal(err)
	}
	bob, err := NewEridDirectory(bKey, 0, 0)
	if err != nil {
		t.Fatal(err)
	}
	if err := alice.RememberPeer("bob", bKey); err != nil {
		t.Fatal(err)
	}
	if err := bob.RememberPeer("alice", aKey); err != nil {
		t.Fatal(err)
	}
	var ts int64 = 1_700_000_000

	aliceForBob, ok, _ := alice.EridForPeer("bob", ts)
	if !ok {
		t.Fatal("alice should hold a key for bob")
	}
	bobSelf, _ := bob.MyErid(ts)
	if aliceForBob != bobSelf {
		t.Fatalf("established peer should resolve rotating address: %s != %s", aliceForBob, bobSelf)
	}

	aliceSelf, _ := alice.MyErid(ts)
	who, ok, _ := bob.ResolvePeer(aliceSelf, ts)
	if !ok || who != "alice" {
		t.Fatalf("reverse resolve: got %q ok=%v want alice", who, ok)
	}

	xKey, _ := DeriveRoutingKey([]byte("identity-X"))
	outsider, _ := NewEridDirectory(xKey, 0, 0)
	if _, ok, _ := outsider.ResolvePeer(aliceSelf, ts); ok {
		t.Fatal("an outsider with no routing key must not resolve the ERID")
	}
}
