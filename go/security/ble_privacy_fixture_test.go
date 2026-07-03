// SPDX-License-Identifier: MIT

package security

import (
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

// Cross-language BLE tracking-protection fixture verifier. Reproduces the
// rotating Service UUID and the IRK-based Resolvable Private Address (RPA)
// against the canonical vectors in fixtures/bleprivacy/vectors.json and asserts
// they match the C# reference (and every other SDK) byte-for-byte.
//
// Any drift between this Go implementation and BlePrivacy.cs shows up here as a
// UUID string or hex mismatch.

type blePrivacyVectors struct {
	RotationSeconds int    `json:"rotation_seconds"`
	RotationKey     string `json:"rotation_key"`
	IRK             string `json:"irk"`
	WrongIRK        string `json:"wrong_irk"`
	UUIDVectors     []struct {
		Window int64  `json:"window"`
		UUID   string `json:"uuid"`
	} `json:"uuid_vectors"`
	RPAVectors []struct {
		Window int64  `json:"window"`
		RPA    string `json:"rpa"`
	} `json:"rpa_vectors"`
}

func loadBlePrivacyVectors(t *testing.T) blePrivacyVectors {
	t.Helper()
	root := repoRoot(t)
	path := filepath.Join(root, "fixtures", "bleprivacy", "vectors.json")

	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read vectors: %v", err)
	}
	var v blePrivacyVectors
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("unmarshal vectors: %v", err)
	}
	return v
}

func TestBlePrivacyFixture_ServiceUUID(t *testing.T) {
	v := loadBlePrivacyVectors(t)
	rotationKey := mustHex(t, v.RotationKey)

	if len(v.UUIDVectors) == 0 {
		t.Fatal("no uuid_vectors in fixture")
	}
	for _, uv := range v.UUIDVectors {
		got := ServiceUUID(rotationKey, uv.Window)
		if got != uv.UUID {
			t.Errorf("ServiceUUID(window=%d) mismatch:\n  expected: %s\n  actual:   %s",
				uv.Window, uv.UUID, got)
		}
	}
}

func TestBlePrivacyFixture_ResolvableAddress(t *testing.T) {
	v := loadBlePrivacyVectors(t)
	irk := mustHex(t, v.IRK)
	wrongIRK := mustHex(t, v.WrongIRK)

	if len(v.RPAVectors) == 0 {
		t.Fatal("no rpa_vectors in fixture")
	}
	for _, rv := range v.RPAVectors {
		rpa, err := ResolvableAddress(irk, rv.Window)
		if err != nil {
			t.Fatalf("ResolvableAddress(window=%d): %v", rv.Window, err)
		}
		if got := hex.EncodeToString(rpa); got != rv.RPA {
			t.Errorf("ResolvableAddress(window=%d) mismatch:\n  expected: %s\n  actual:   %s",
				rv.Window, rv.RPA, got)
		}

		// The generating IRK resolves its own RPA; a different IRK must not.
		if !ResolveAddress(irk, rpa) {
			t.Errorf("ResolveAddress(irk, rpa) = false for window=%d, want true", rv.Window)
		}
		if ResolveAddress(wrongIRK, rpa) {
			t.Errorf("ResolveAddress(wrongIRK, rpa) = true for window=%d, want false", rv.Window)
		}
	}
}

func TestBlePrivacyFixture_RotationSecondsAndWindowFor(t *testing.T) {
	v := loadBlePrivacyVectors(t)

	if RotationSeconds != v.RotationSeconds {
		t.Errorf("RotationSeconds = %d, want %d", RotationSeconds, v.RotationSeconds)
	}
	if w := WindowFor(899); w != 0 {
		t.Errorf("WindowFor(899) = %d, want 0", w)
	}
	if w := WindowFor(900); w != 1 {
		t.Errorf("WindowFor(900) = %d, want 1", w)
	}
}

func TestBlePrivacyFixture_ShortIRKRejected(t *testing.T) {
	shortIRK := make([]byte, 15)

	if _, err := ResolvableAddress(shortIRK, 0); err == nil {
		t.Error("ResolvableAddress(15-byte IRK) = nil error, want error")
	}
	if ResolveAddress(shortIRK, make([]byte, 6)) {
		t.Error("ResolveAddress(15-byte IRK, ...) = true, want false")
	}
}
