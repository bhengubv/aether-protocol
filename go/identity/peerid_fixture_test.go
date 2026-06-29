// SPDX-License-Identifier: MIT

package identity

import (
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

type peerIDInput struct {
	Name      string `json:"name"`
	PubkeyHex string `json:"pubkey_hex"`
}

// TestPeerIDByteParityWithLibp2pFixture asserts the Go port reproduces the cross-language
// fixtures/peerid corpus exactly — and those expected values are real js-libp2p output, so
// passing proves both cross-language byte-identity AND interoperability with the real network.
func TestPeerIDByteParityWithLibp2pFixture(t *testing.T) {
	dir := filepath.Join("..", "..", "fixtures", "peerid")
	raw, err := os.ReadFile(filepath.Join(dir, "inputs.json"))
	if err != nil {
		t.Fatalf("read inputs: %v", err)
	}
	var inputs []peerIDInput
	if err := json.Unmarshal(raw, &inputs); err != nil {
		t.Fatalf("parse inputs: %v", err)
	}
	if len(inputs) == 0 {
		t.Fatal("no inputs")
	}
	for _, in := range inputs {
		pub, err := hex.DecodeString(in.PubkeyHex)
		if err != nil {
			t.Fatalf("%s: hex: %v", in.Name, err)
		}
		wantRaw, err := os.ReadFile(filepath.Join(dir, "expected", in.Name+".txt"))
		if err != nil {
			t.Fatalf("%s: read expected: %v", in.Name, err)
		}
		want := strings.TrimSpace(string(wantRaw))
		got, err := PeerIDFromEd25519PublicKey(pub)
		if err != nil {
			t.Fatalf("%s: derive: %v", in.Name, err)
		}
		if got != want {
			t.Fatalf("%s: got %s want %s", in.Name, got, want)
		}
		if !strings.HasPrefix(got, "12D3Koo") {
			t.Fatalf("%s: expected 12D3Koo prefix, got %s", in.Name, got)
		}
	}
}

func TestPeerIDRejectsWrongLength(t *testing.T) {
	if _, err := PeerIDFromEd25519PublicKey(make([]byte, 31)); err == nil {
		t.Fatal("expected error for 31-byte key")
	}
	if _, err := PeerIDFromEd25519PublicKey(make([]byte, 33)); err == nil {
		t.Fatal("expected error for 33-byte key")
	}
}
