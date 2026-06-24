// SPDX-License-Identifier: MIT

package security

import (
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// TestVerifyWithFallback_P256Fixture drives Ed25519Service.VerifyWithFallback through
// the shared cross-language corpus at tests/cross-language/p256-fixtures.json — DER
// SubjectPublicKeyInfo public key + ASN.1 DER ECDSA signature + SHA-256, per
// PROTOCOL_SPEC.md §7.5. Every AetherNet SDK drives the SAME vectors and MUST accept
// valid:true and reject valid:false.
func TestVerifyWithFallback_P256Fixture(t *testing.T) {
	raw, err := os.ReadFile(findP256Fixture(t))
	if err != nil {
		t.Fatal(err)
	}
	var doc struct {
		Vectors []struct {
			Name         string `json:"name"`
			PublicKeyDer string `json:"public_key_der"`
			Message      string `json:"message"`
			SignatureDer string `json:"signature_der"`
			Valid        bool   `json:"valid"`
		} `json:"vectors"`
	}
	if err := json.Unmarshal(raw, &doc); err != nil {
		t.Fatal(err)
	}
	if len(doc.Vectors) == 0 {
		t.Fatal("no vectors in p256-fixtures.json")
	}

	es := NewEd25519Service()
	for _, v := range doc.Vectors {
		pub, _ := hex.DecodeString(v.PublicKeyDer)
		msg, _ := hex.DecodeString(v.Message)
		sig, _ := hex.DecodeString(v.SignatureDer)
		// A >32-byte key forces the P-256 branch; a regression to Ed25519-only would
		// reject the valid vector and fail here.
		if len(pub) <= 32 {
			t.Fatalf("%s: P-256 SPKI key must be > 32 bytes", v.Name)
		}
		if got := es.VerifyWithFallback(pub, msg, sig); got != v.Valid {
			t.Errorf("%s: VerifyWithFallback = %v, want %v", v.Name, got, v.Valid)
		}
	}
}

func findP256Fixture(t *testing.T) string {
	dir, err := os.Getwd()
	if err != nil {
		t.Fatal(err)
	}
	for {
		cand := filepath.Join(dir, "tests", "cross-language", "p256-fixtures.json")
		if _, statErr := os.Stat(cand); statErr == nil {
			return cand
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			t.Fatalf("p256-fixtures.json not found walking up from working dir")
		}
		dir = parent
	}
}
