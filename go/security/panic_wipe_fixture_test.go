// SPDX-License-Identifier: MIT

package security

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// Cross-language panic-wipe parity verifier. Reproduces the deterministic
// duress-defence primitives against the canonical vectors in
// fixtures/panicwipe/vectors.json and asserts they match the C# reference
// (PanicWipe.cs) — and every other SDK — byte-for-byte.
//
// duress_pin_hashes are SHA-256(UTF8(pin)) in hex; identity_key_names and the
// pre-key name patterns are the canonical set a wipe destroys. SecureErase
// (overwrite random + zero) is behavioural and tested here per-language.

type panicWipeVectors struct {
	MaxPreKeys       int      `json:"max_prekeys"`
	IdentityKeyNames []string `json:"identity_key_names"`
	PreKeyName       struct {
		Index    int    `json:"index"`
		Expected string `json:"expected"`
	} `json:"prekey_name"`
	SignedPreKeyName struct {
		Index    int    `json:"index"`
		Expected string `json:"expected"`
	} `json:"signed_prekey_name"`
	DuressPinHashes []struct {
		Pin    string `json:"pin"`
		SHA256 string `json:"sha256"`
	} `json:"duress_pin_hashes"`
}

func loadPanicWipeVectors(t *testing.T) panicWipeVectors {
	t.Helper()
	root := repoRoot(t)
	path := filepath.Join(root, "fixtures", "panicwipe", "vectors.json")

	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read vectors: %v", err)
	}
	var v panicWipeVectors
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("unmarshal vectors: %v", err)
	}
	return v
}

func TestPanicWipeFixture_DuressPin(t *testing.T) {
	v := loadPanicWipeVectors(t)

	if len(v.DuressPinHashes) == 0 {
		t.Fatal("no duress_pin_hashes in fixture")
	}
	for _, d := range v.DuressPinHashes {
		hash := DuressPinHash(d.Pin)
		if got := hex.EncodeToString(hash); got != d.SHA256 {
			t.Errorf("DuressPinHash(%q) mismatch:\n  expected: %s\n  actual:   %s",
				d.Pin, d.SHA256, got)
		}
		if !VerifyDuressPin(d.Pin, hash) {
			t.Errorf("VerifyDuressPin(%q, hash) = false, want true", d.Pin)
		}
		// A different PIN must not verify against the same stored hash.
		if VerifyDuressPin(d.Pin+"x", hash) {
			t.Errorf("VerifyDuressPin(%q, hash) = true, want false", d.Pin+"x")
		}
	}
}

func TestPanicWipeFixture_IdentityKeyNames(t *testing.T) {
	v := loadPanicWipeVectors(t)

	got := IdentityKeyNames()
	if len(got) != len(v.IdentityKeyNames) {
		t.Fatalf("IdentityKeyNames length = %d, want %d\n  actual:   %v\n  expected: %v",
			len(got), len(v.IdentityKeyNames), got, v.IdentityKeyNames)
	}
	for i := range v.IdentityKeyNames {
		if got[i] != v.IdentityKeyNames[i] {
			t.Errorf("IdentityKeyNames[%d] = %q, want %q", i, got[i], v.IdentityKeyNames[i])
		}
	}
}

func TestPanicWipeFixture_KeyNamePatterns(t *testing.T) {
	v := loadPanicWipeVectors(t)

	if MaxPreKeys != v.MaxPreKeys {
		t.Errorf("MaxPreKeys = %d, want %d", MaxPreKeys, v.MaxPreKeys)
	}
	if got := PreKeyName(v.PreKeyName.Index); got != v.PreKeyName.Expected {
		t.Errorf("PreKeyName(%d) = %q, want %q", v.PreKeyName.Index, got, v.PreKeyName.Expected)
	}
	if got := SignedPreKeyName(v.SignedPreKeyName.Index); got != v.SignedPreKeyName.Expected {
		t.Errorf("SignedPreKeyName(%d) = %q, want %q",
			v.SignedPreKeyName.Index, got, v.SignedPreKeyName.Expected)
	}
}

func TestPanicWipeFixture_SecureErase(t *testing.T) {
	secret := []byte("super-secret-key-material-0123456789")
	buf := make([]byte, len(secret))
	copy(buf, secret)

	SecureErase(buf)

	zero := make([]byte, len(secret))
	if !bytes.Equal(buf, zero) {
		t.Errorf("SecureErase left non-zero bytes: %x", buf)
	}

	// Nil and empty buffers must be no-ops (no panic).
	SecureErase(nil)
	SecureErase([]byte{})
}

func TestPanicWipeFixture_WrongLengthHashRejected(t *testing.T) {
	// A stored hash that is not exactly 32 bytes must never verify.
	shortHash := make([]byte, 16)
	if VerifyDuressPin("0000", shortHash) {
		t.Error("VerifyDuressPin with 16-byte hash = true, want false")
	}
}
