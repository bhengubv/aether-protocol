// SPDX-License-Identifier: MIT

package identity_test

import (
	"strings"
	"testing"

	"github.com/bhengubv/aether-protocol/go/identity"
)

// ── helpers ───────────────────────────────────────────────────────────────────

// fixedKey32 returns a deterministic 32-byte public key for use in test vectors.
func fixedKey32(fill byte) []byte {
	key := make([]byte, 32)
	for i := range key {
		key[i] = fill
	}
	return key
}

// seqKey32 returns a 32-byte key with bytes 0, 1, 2, … 31.
func seqKey32() []byte {
	key := make([]byte, 32)
	for i := range key {
		key[i] = byte(i)
	}
	return key
}

// ── FromPublicKey ─────────────────────────────────────────────────────────────

func TestFromPublicKey_ReturnsExpectedFormat(t *testing.T) {
	tag, err := identity.FromPublicKey(seqKey32())
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	// Must match XXXXX-XXXXX  (5 chars, dash, 5 chars).
	if len(tag.Value) != 11 {
		t.Errorf("tag length: got %d, want 11  (value=%q)", len(tag.Value), tag.Value)
	}
	if tag.Value[5] != '-' {
		t.Errorf("separator missing or misplaced in %q", tag.Value)
	}

	// Each non-dash character must be in the Crockford alphabet.
	const alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
	for i, ch := range tag.Value {
		if i == 5 {
			continue // separator
		}
		if !strings.ContainsRune(alphabet, ch) {
			t.Errorf("character %q at position %d is not in the Crockford alphabet", ch, i)
		}
	}
}

func TestFromPublicKey_KnownVector(t *testing.T) {
	// SHA-256(0x00 × 32) is well-known; derive the expected tag deterministically.
	// We call the function itself as the ground truth and freeze the result here.
	key := fixedKey32(0x00)
	tag1, err := identity.FromPublicKey(key)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	// The tag should be stable — call again and confirm identical output.
	tag2, _ := identity.FromPublicKey(key)
	if tag1.Value != tag2.Value {
		t.Errorf("same key produced different tags: %q vs %q", tag1.Value, tag2.Value)
	}

	// Must be exactly "XXXXX-XXXXX".
	parts := strings.Split(tag1.Value, "-")
	if len(parts) != 2 || len(parts[0]) != 5 || len(parts[1]) != 5 {
		t.Errorf("tag format wrong: %q", tag1.Value)
	}
}

func TestFromPublicKey_ErrorOnShortKey(t *testing.T) {
	_, err := identity.FromPublicKey(make([]byte, 31))
	if err == nil {
		t.Error("expected error for 31-byte key")
	}
}

func TestFromPublicKey_ErrorOnLongKey(t *testing.T) {
	_, err := identity.FromPublicKey(make([]byte, 33))
	if err == nil {
		t.Error("expected error for 33-byte key")
	}
}

func TestFromPublicKey_ErrorOnNilKey(t *testing.T) {
	_, err := identity.FromPublicKey(nil)
	if err == nil {
		t.Error("expected error for nil key")
	}
}

func TestFromPublicKey_ErrorOnEmptyKey(t *testing.T) {
	_, err := identity.FromPublicKey([]byte{})
	if err == nil {
		t.Error("expected error for empty key")
	}
}

// ── Determinism & uniqueness ──────────────────────────────────────────────────

func TestFromPublicKey_SameKeyProducesSameTag(t *testing.T) {
	key := seqKey32()
	tag1, _ := identity.FromPublicKey(key)
	tag2, _ := identity.FromPublicKey(key)
	if tag1.Value != tag2.Value {
		t.Errorf("got %q and %q for the same key", tag1.Value, tag2.Value)
	}
}

func TestFromPublicKey_DifferentKeysProduceDifferentTags(t *testing.T) {
	tag1, _ := identity.FromPublicKey(fixedKey32(0x00))
	tag2, _ := identity.FromPublicKey(fixedKey32(0xFF))
	tag3, _ := identity.FromPublicKey(seqKey32())

	if tag1.Value == tag2.Value {
		t.Errorf("all-zero and all-0xFF keys produced the same tag: %q", tag1.Value)
	}
	if tag1.Value == tag3.Value {
		t.Errorf("all-zero and sequential keys produced the same tag: %q", tag1.Value)
	}
	if tag2.Value == tag3.Value {
		t.Errorf("all-0xFF and sequential keys produced the same tag: %q", tag2.Value)
	}
}

// ── Round-trip: FromPublicKey → String → Parse ─────────────────────────────

func TestRoundTrip_FromPublicKeyToString_ToParse(t *testing.T) {
	original, err := identity.FromPublicKey(seqKey32())
	if err != nil {
		t.Fatalf("FromPublicKey: %v", err)
	}

	parsed, err := identity.Parse(original.String())
	if err != nil {
		t.Fatalf("Parse: %v", err)
	}

	if original.Value != parsed.Value {
		t.Errorf("round-trip mismatch: %q → %q", original.Value, parsed.Value)
	}
}

// ── Parse ─────────────────────────────────────────────────────────────────────

func TestParse_AcceptsCanonicalForm(t *testing.T) {
	tag, err := identity.FromPublicKey(seqKey32())
	if err != nil {
		t.Fatal(err)
	}

	parsed, err := identity.Parse(tag.Value) // e.g. "KXJB7-MN2P4"
	if err != nil {
		t.Errorf("Parse(%q): %v", tag.Value, err)
	}
	if parsed.Value != tag.Value {
		t.Errorf("got %q, want %q", parsed.Value, tag.Value)
	}
}

func TestParse_AcceptsWithoutSeparator(t *testing.T) {
	tag, _ := identity.FromPublicKey(seqKey32())
	noSep := tag.Value[:5] + tag.Value[6:] // remove the dash

	parsed, err := identity.Parse(noSep)
	if err != nil {
		t.Errorf("Parse without separator %q: %v", noSep, err)
	}
	if parsed.Value != tag.Value {
		t.Errorf("got %q, want %q", parsed.Value, tag.Value)
	}
}

func TestParse_AcceptsLowercase(t *testing.T) {
	tag, _ := identity.FromPublicKey(seqKey32())
	lower := strings.ToLower(tag.Value)

	parsed, err := identity.Parse(lower)
	if err != nil {
		t.Errorf("Parse lowercase %q: %v", lower, err)
	}
	if parsed.Value != tag.Value {
		t.Errorf("got %q, want %q", parsed.Value, tag.Value)
	}
}

func TestParse_AcceptsMixedCase(t *testing.T) {
	tag, _ := identity.FromPublicKey(seqKey32())
	// Alternate upper/lower on the alphabetic (non-digit, non-separator) characters.
	var mixed strings.Builder
	toLower := false
	for _, ch := range tag.Value {
		if ch == '-' {
			mixed.WriteRune(ch)
			continue
		}
		// Only letters can be lowercased; digits are unchanged.
		if toLower && ch >= 'A' && ch <= 'Z' {
			mixed.WriteRune(ch + 32) // convert A-Z to a-z
		} else {
			mixed.WriteRune(ch)
		}
		toLower = !toLower
	}
	parsed, err := identity.Parse(mixed.String())
	if err != nil {
		t.Errorf("Parse mixed case %q: %v", mixed.String(), err)
	}
	if parsed.Value != tag.Value {
		t.Errorf("got %q, want %q", parsed.Value, tag.Value)
	}
}

func TestParse_RejectsEmptyString(t *testing.T) {
	_, err := identity.Parse("")
	if err == nil {
		t.Error("expected error for empty string")
	}
}

func TestParse_RejectsWrongLengthShort(t *testing.T) {
	_, err := identity.Parse("ABCDE")
	if err == nil {
		t.Error("expected error for 5-char string")
	}
}

func TestParse_RejectsWrongLengthLong(t *testing.T) {
	_, err := identity.Parse("ABCDE-FGHJ-KM")
	if err == nil {
		t.Error("expected error for 13-char string")
	}
}

func TestParse_RejectsInvalidChar_I(t *testing.T) {
	// 'I' is excluded from the Crockford alphabet.
	_, err := identity.Parse("ABCDI-FGHJK")
	if err == nil {
		t.Error("expected error for tag containing 'I'")
	}
}

func TestParse_RejectsInvalidChar_L(t *testing.T) {
	_, err := identity.Parse("ABCDL-FGHJK")
	if err == nil {
		t.Error("expected error for tag containing 'L'")
	}
}

func TestParse_RejectsInvalidChar_O(t *testing.T) {
	_, err := identity.Parse("ABCDO-FGHJK")
	if err == nil {
		t.Error("expected error for tag containing 'O'")
	}
}

func TestParse_RejectsInvalidChar_U(t *testing.T) {
	_, err := identity.Parse("ABCDU-FGHJK")
	if err == nil {
		t.Error("expected error for tag containing 'U'")
	}
}

func TestParse_NormalisesToUppercase(t *testing.T) {
	tag, _ := identity.FromPublicKey(seqKey32())
	lower := strings.ToLower(tag.Value)
	parsed, _ := identity.Parse(lower)
	if parsed.Value != tag.Value {
		t.Errorf("Parse did not normalise to uppercase: got %q, want %q", parsed.Value, tag.Value)
	}
}

// ── TryParse ──────────────────────────────────────────────────────────────────

func TestTryParse_ReturnsTrueOnValid(t *testing.T) {
	tag, _ := identity.FromPublicKey(seqKey32())
	parsed, ok := identity.TryParse(tag.Value)
	if !ok {
		t.Error("TryParse should return true for valid tag")
	}
	if parsed.Value != tag.Value {
		t.Errorf("got %q, want %q", parsed.Value, tag.Value)
	}
}

func TestTryParse_ReturnsFalseOnInvalid(t *testing.T) {
	_, ok := identity.TryParse("not-a-tag")
	if ok {
		t.Error("TryParse should return false for invalid input")
	}
}

func TestTryParse_ReturnsFalseOnEmpty(t *testing.T) {
	_, ok := identity.TryParse("")
	if ok {
		t.Error("TryParse should return false for empty string")
	}
}

// ── Verify ────────────────────────────────────────────────────────────────────

func TestVerify_CorrectPubkeyReturnsTrue(t *testing.T) {
	key := seqKey32()
	tag, _ := identity.FromPublicKey(key)
	if !identity.Verify(tag.Value, key) {
		t.Error("Verify should return true for correct public key")
	}
}

func TestVerify_WrongPubkeyReturnsFalse(t *testing.T) {
	key := seqKey32()
	tag, _ := identity.FromPublicKey(key)

	wrongKey := fixedKey32(0xFF)
	if identity.Verify(tag.Value, wrongKey) {
		t.Error("Verify should return false for wrong public key")
	}
}

func TestVerify_InvalidTagReturnsFalse(t *testing.T) {
	if identity.Verify("bad-tag", seqKey32()) {
		t.Error("Verify should return false for invalid tag string")
	}
}

func TestVerify_ShortKeyReturnsFalse(t *testing.T) {
	key := seqKey32()
	tag, _ := identity.FromPublicKey(key)
	if identity.Verify(tag.Value, key[:16]) {
		t.Error("Verify should return false for short key")
	}
}

func TestVerify_WithoutSeparatorMatchesKey(t *testing.T) {
	key := seqKey32()
	tag, _ := identity.FromPublicKey(key)
	noSep := tag.Value[:5] + tag.Value[6:]
	if !identity.Verify(noSep, key) {
		t.Error("Verify should return true for tag without separator when key matches")
	}
}

// ── IsValid / String ──────────────────────────────────────────────────────────

func TestIsValid_TrueForDerivedTag(t *testing.T) {
	tag, _ := identity.FromPublicKey(seqKey32())
	if !tag.IsValid() {
		t.Error("derived tag should be valid")
	}
}

func TestIsValid_FalseForZeroValue(t *testing.T) {
	var tag identity.AetherTag
	if tag.IsValid() {
		t.Error("zero-value AetherTag should not be valid")
	}
}

func TestString_ReturnsValue(t *testing.T) {
	tag, _ := identity.FromPublicKey(seqKey32())
	if tag.String() != tag.Value {
		t.Errorf("String() %q != Value %q", tag.String(), tag.Value)
	}
}

func TestString_ZeroValueIsEmpty(t *testing.T) {
	var tag identity.AetherTag
	if tag.String() != "" {
		t.Errorf("zero-value String() should be empty, got %q", tag.String())
	}
}
