// SPDX-License-Identifier: MIT

package storage

import (
	"bytes"
	"testing"
)

// TestDerivedKeyProvider_Deterministic verifies same passphrase + salt +
// iterations -> same derived key.
func TestDerivedKeyProvider_Deterministic(t *testing.T) {
	salt := []byte("salt-must-be-at-least-16-bytes")
	p1, err := NewDerivedDataAtRestKeyProvider("passphrase-1", salt, 1000)
	if err != nil {
		t.Fatalf("NewDerivedDataAtRestKeyProvider: %v", err)
	}
	p2, err := NewDerivedDataAtRestKeyProvider("passphrase-1", salt, 1000)
	if err != nil {
		t.Fatalf("NewDerivedDataAtRestKeyProvider: %v", err)
	}
	k1 := p1.GetKey(1)
	k2 := p2.GetKey(1)
	if !bytes.Equal(k1, k2) {
		t.Errorf("same passphrase+salt+iters -> different keys (k1=%x k2=%x)", k1, k2)
	}
	if len(k1) != 32 {
		t.Errorf("derived key length=%d, want 32 (AES-256)", len(k1))
	}
}

// TestDerivedKeyProvider_DifferentSalt verifies different salt -> different key.
func TestDerivedKeyProvider_DifferentSalt(t *testing.T) {
	pass := "same-passphrase"
	saltA := []byte("salt-a-with-min-length-16")
	saltB := []byte("salt-b-with-min-length-16")
	pA, err := NewDerivedDataAtRestKeyProvider(pass, saltA, 1000)
	if err != nil {
		t.Fatalf("derive A: %v", err)
	}
	pB, err := NewDerivedDataAtRestKeyProvider(pass, saltB, 1000)
	if err != nil {
		t.Fatalf("derive B: %v", err)
	}
	if bytes.Equal(pA.GetKey(1), pB.GetKey(1)) {
		t.Errorf("different salts -> identical keys (PBKDF2 broken or salt unused)")
	}
}

// TestDerivedKeyProvider_DifferentPassphrase verifies different pass -> different key.
func TestDerivedKeyProvider_DifferentPassphrase(t *testing.T) {
	salt := []byte("salt-with-min-length-16-bytes")
	pA, _ := NewDerivedDataAtRestKeyProvider("alpha", salt, 1000)
	pB, _ := NewDerivedDataAtRestKeyProvider("beta", salt, 1000)
	if bytes.Equal(pA.GetKey(1), pB.GetKey(1)) {
		t.Errorf("different passphrases -> identical keys")
	}
}

// TestDerivedKeyProvider_InvalidInputs verifies the validation errors.
func TestDerivedKeyProvider_InvalidInputs(t *testing.T) {
	cases := []struct {
		name       string
		pass       string
		salt       []byte
		iterations int
	}{
		{"empty passphrase", "", []byte("0123456789abcdef"), 1000},
		{"nil salt", "p", nil, 1000},
		{"short salt", "p", []byte("short"), 1000},
		{"zero iterations", "p", []byte("0123456789abcdef"), 0},
		{"negative iterations", "p", []byte("0123456789abcdef"), -1},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			_, err := NewDerivedDataAtRestKeyProvider(c.pass, c.salt, c.iterations)
			if err == nil {
				t.Errorf("%s: expected error, got nil", c.name)
			}
		})
	}
}

// TestDerivedKeyProvider_IterationsAffectKey verifies that the iteration
// count actually changes the output (i.e. PBKDF2 is using it).
func TestDerivedKeyProvider_IterationsAffectKey(t *testing.T) {
	pass := "passphrase"
	salt := []byte("0123456789abcdef0123")
	p100, _ := NewDerivedDataAtRestKeyProvider(pass, salt, 100)
	p1000, _ := NewDerivedDataAtRestKeyProvider(pass, salt, 1000)
	if bytes.Equal(p100.GetKey(1), p1000.GetKey(1)) {
		t.Errorf("different iteration counts produced identical keys (iter param ignored?)")
	}
}

// TestDerivedKeyProvider_CurrentVersionAndIterations verifies the
// metadata accessors.
func TestDerivedKeyProvider_CurrentVersionAndIterations(t *testing.T) {
	salt := []byte("0123456789abcdef")
	p, err := NewDerivedDataAtRestKeyProvider("p", salt, 2500)
	if err != nil {
		t.Fatalf("derive: %v", err)
	}
	if p.CurrentVersion() != 1 {
		t.Errorf("CurrentVersion: got %d want 1", p.CurrentVersion())
	}
	if p.Iterations() != 2500 {
		t.Errorf("Iterations: got %d want 2500", p.Iterations())
	}
}

// TestDerivedKeyProvider_WithRotation verifies the rotation flow.
func TestDerivedKeyProvider_WithRotation(t *testing.T) {
	saltOld := []byte("0123456789abcdef")
	saltNew := []byte("fedcba9876543210")
	pOld, err := NewDerivedDataAtRestKeyProvider("old", saltOld, 1000)
	if err != nil {
		t.Fatalf("derive old: %v", err)
	}
	pNew, err := pOld.WithRotation(2, "new", saltNew, 1000)
	if err != nil {
		t.Fatalf("WithRotation: %v", err)
	}
	if pNew.CurrentVersion() != 2 {
		t.Errorf("rotated CurrentVersion: got %d want 2", pNew.CurrentVersion())
	}
	if pNew.GetKey(1) == nil {
		t.Errorf("rotated provider lost v1 — historic blobs would be unreadable")
	}
	if pNew.GetKey(2) == nil {
		t.Errorf("rotated provider missing v2")
	}
	if bytes.Equal(pNew.GetKey(1), pNew.GetKey(2)) {
		t.Errorf("rotated v1 and v2 keys are identical — derivation broken")
	}

	// Rotating to an existing version should error.
	if _, err := pOld.WithRotation(1, "another", saltNew, 1000); err == nil {
		t.Errorf("WithRotation to existing version: expected error")
	}
}
