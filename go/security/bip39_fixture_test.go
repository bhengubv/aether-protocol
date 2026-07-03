// SPDX-License-Identifier: MIT

package security

import (
	"bytes"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// Cross-language BIP-39 fixture verifier. Drives the recovery-phrase codec
// against the official Trezor test vectors in fixtures/bip39/vectors.json and
// asserts entropy->mnemonic->seed byte-for-byte, then exercises the AetherNet
// identity backup/restore round-trip and the reject paths.
//
// Any drift between this Go implementation and the C# reference (or any other
// language) shows up here as a mismatch.

type bip39Vectors struct {
	Passphrase string `json:"passphrase"`
	Vectors    []struct {
		Entropy  string `json:"entropy"`
		Mnemonic string `json:"mnemonic"`
		Seed     string `json:"seed"`
	} `json:"vectors"`
}

func TestBip39Fixture_TrezorVectors(t *testing.T) {
	raw, err := os.ReadFile(findBip39Fixture(t))
	if err != nil {
		t.Fatal(err)
	}
	var doc bip39Vectors
	if err := json.Unmarshal(raw, &doc); err != nil {
		t.Fatal(err)
	}
	if len(doc.Vectors) != 24 {
		t.Fatalf("expected 24 vectors in vectors.json, got %d", len(doc.Vectors))
	}
	if doc.Passphrase != "TREZOR" {
		t.Fatalf("expected passphrase TREZOR, got %q", doc.Passphrase)
	}

	for i, v := range doc.Vectors {
		entropy := mustHexBip39(t, v.Entropy)

		// entropy -> mnemonic
		gotMnemonic, err := EntropyToMnemonic(entropy)
		if err != nil {
			t.Fatalf("vector %d: EntropyToMnemonic error: %v", i, err)
		}
		if gotMnemonic != v.Mnemonic {
			t.Errorf("vector %d: mnemonic mismatch:\n  expected: %s\n  actual:   %s", i, v.Mnemonic, gotMnemonic)
		}

		// mnemonic -> entropy
		gotEntropy, err := MnemonicToEntropy(v.Mnemonic)
		if err != nil {
			t.Fatalf("vector %d: MnemonicToEntropy error: %v", i, err)
		}
		if got := hex.EncodeToString(gotEntropy); got != v.Entropy {
			t.Errorf("vector %d: entropy mismatch:\n  expected: %s\n  actual:   %s", i, v.Entropy, got)
		}

		// mnemonic -> seed (PBKDF2-HMAC-SHA512, passphrase TREZOR)
		gotSeed := MnemonicToSeed(v.Mnemonic, doc.Passphrase)
		if got := hex.EncodeToString(gotSeed); got != v.Seed {
			t.Errorf("vector %d: seed mismatch:\n  expected: %s\n  actual:   %s", i, v.Seed, got)
		}
	}
}

// TestBip39Identity_KnownVector pins the AetherNet identity mapping to a fixed
// 32-byte seed (last Trezor vector): the seed must encode to the exact 24-word
// phrase, and restoring that phrase must recover the same private seed.
func TestBip39Identity_KnownVector(t *testing.T) {
	const (
		entropyHex   = "f585c11aec520db57dd353c69554b21a89b20fb0650966fa0a9d6f74fd989d8f"
		wantMnemonic = "void come effort suffer camp survey warrior heavy shoot primary clutch crush open amazing screen patrol group space point ten exist slush involve unfold"
	)
	seed := mustHexBip39(t, entropyHex)

	phrase, err := ToRecoveryPhrase(seed)
	if err != nil {
		t.Fatalf("ToRecoveryPhrase error: %v", err)
	}
	if phrase != wantMnemonic {
		t.Errorf("recovery phrase mismatch:\n  expected: %s\n  actual:   %s", wantMnemonic, phrase)
	}

	_, priv, err := FromRecoveryPhrase(wantMnemonic)
	if err != nil {
		t.Fatalf("FromRecoveryPhrase error: %v", err)
	}
	if !bytes.Equal(priv, seed) {
		t.Errorf("restored private key mismatch:\n  expected: %s\n  actual:   %s", entropyHex, hex.EncodeToString(priv))
	}
}

// TestBip39Identity_RandomRoundTrip generates a fresh identity, backs it up to a
// phrase, restores it, and confirms the restored key pair is identical and still
// signs+verifies — i.e. the phrase truly is the identity.
func TestBip39Identity_RandomRoundTrip(t *testing.T) {
	seed := make([]byte, 32)
	if _, err := rand.Read(seed); err != nil {
		t.Fatalf("rand: %v", err)
	}

	phrase, err := ToRecoveryPhrase(seed)
	if err != nil {
		t.Fatalf("ToRecoveryPhrase error: %v", err)
	}

	pub, priv, err := FromRecoveryPhrase(phrase)
	if err != nil {
		t.Fatalf("FromRecoveryPhrase error: %v", err)
	}
	if !bytes.Equal(priv, seed) {
		t.Fatalf("restored private key != original seed")
	}

	// The restored public key must be the canonical Ed25519 public key for this
	// seed (trailing 32 bytes of the expanded private key), i.e. what any Ed25519
	// implementation — including Ed25519Service — derives.
	wantPub := ed25519.NewKeyFromSeed(seed)[32:]
	if !bytes.Equal(pub, wantPub) {
		t.Fatalf("restored public key mismatch:\n  expected: %s\n  actual:   %s",
			hex.EncodeToString(wantPub), hex.EncodeToString(pub))
	}

	// And the restored key actually signs and verifies through Ed25519Service.
	es := NewEd25519Service()
	msg := []byte("the mesh is alive")
	sig, err := es.Sign(priv, msg)
	if err != nil {
		t.Fatalf("Sign: %v", err)
	}
	if !es.Verify(pub, msg, sig) {
		t.Fatalf("restored key failed to sign+verify")
	}
}

// TestBip39_RejectPaths confirms a mistyped or malformed phrase is rejected
// rather than silently yielding a wrong secret.
func TestBip39_RejectPaths(t *testing.T) {
	cases := []struct {
		name     string
		mnemonic string
	}{
		{
			name:     "bad checksum (24x abandon)",
			mnemonic: "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon",
		},
		{
			name:     "unknown word",
			mnemonic: "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon notaword",
		},
		{
			name:     "wrong word count (3 words)",
			mnemonic: "abandon abandon abandon",
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if _, err := MnemonicToEntropy(tc.mnemonic); err == nil {
				t.Errorf("MnemonicToEntropy(%q): expected error, got nil", tc.name)
			}
			if _, _, err := FromRecoveryPhrase(tc.mnemonic); err == nil {
				t.Errorf("FromRecoveryPhrase(%q): expected error, got nil", tc.name)
			}
			if IsValidMnemonic(tc.mnemonic) {
				t.Errorf("IsValidMnemonic(%q): expected false", tc.name)
			}
		})
	}
}

// ─── Helpers ─────────────────────────────────────────────────────────────

// findBip39Fixture walks up from the working directory to the repo root (marked
// by AetherNetProtocol.slnx) and returns fixtures/bip39/vectors.json — the same
// walk-up idiom the other Go fixture tests use.
func findBip39Fixture(t *testing.T) string {
	t.Helper()
	dir, err := os.Getwd()
	if err != nil {
		t.Fatal(err)
	}
	for {
		if _, statErr := os.Stat(filepath.Join(dir, "AetherNetProtocol.slnx")); statErr == nil {
			return filepath.Join(dir, "fixtures", "bip39", "vectors.json")
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			t.Fatalf("repo root (AetherNetProtocol.slnx) not found walking up from working dir")
		}
		dir = parent
	}
}

func mustHexBip39(t *testing.T, s string) []byte {
	t.Helper()
	b, err := hex.DecodeString(s)
	if err != nil {
		t.Fatalf("hex.DecodeString(%q): %v", s, err)
	}
	return b
}
