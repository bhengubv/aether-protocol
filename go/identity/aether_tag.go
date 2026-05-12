// SPDX-License-Identifier: MIT

// Package identity provides the AetherTag identity primitive — a human-readable,
// shareable address derived from a node's 32-byte Ed25519 public key.
//
// Algorithm
//
//	SHA-256(publicKey) → extract first 50 bits → encode as 10 Crockford base-32
//	chars → format as "XXXXX-XXXXX"
//
// The Crockford base-32 alphabet omits I, L, O and U to avoid transcription errors:
//
//	0123456789ABCDEFGHJKMNPQRSTVWXYZ
package identity

import (
	"crypto/sha256"
	"errors"
	"strings"
)

// crockfordAlphabet is the 32-character Crockford base-32 alphabet.
// It omits I, L, O and U to reduce visual ambiguity.
const crockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

// tagLength is the total number of Crockford characters in a tag (excluding the separator).
const tagLength = 10

// AetherTag is a human-readable, shareable identity address.
// The canonical form is "XXXXX-XXXXX" (10 Crockford base-32 chars with a central dash).
type AetherTag struct {
	// Value holds the canonical "XXXXX-XXXXX" representation.
	Value string
}

// FromPublicKey derives an AetherTag from a 32-byte Ed25519 public key.
// It returns an error if publicKey is not exactly 32 bytes.
func FromPublicKey(publicKey []byte) (AetherTag, error) {
	if len(publicKey) != 32 {
		return AetherTag{}, errors.New("aethertag: public key must be exactly 32 bytes")
	}

	hash := sha256.Sum256(publicKey)

	// Pack the first 50 bits from the hash digest.
	// bits = (hash[0]<<42) | (hash[1]<<34) | (hash[2]<<26) | (hash[3]<<18) |
	//        (hash[4]<<10) | (hash[5]<<2)  | (hash[6]>>6 & 0x3)
	bits := (uint64(hash[0]) << 42) |
		(uint64(hash[1]) << 34) |
		(uint64(hash[2]) << 26) |
		(uint64(hash[3]) << 18) |
		(uint64(hash[4]) << 10) |
		(uint64(hash[5]) << 2) |
		uint64(hash[6]>>6&0x03)

	// Decode 10 × 5-bit groups from MSB to LSB.
	var chars [tagLength]byte
	for i := 9; i >= 0; i-- {
		chars[i] = crockfordAlphabet[bits&0x1F]
		bits >>= 5
	}

	tag := string(chars[:5]) + "-" + string(chars[5:])
	return AetherTag{Value: tag}, nil
}

// Parse parses a tag string into an AetherTag.  It accepts:
//   - canonical form:  "KXJB7-MN2P4"
//   - no separator:    "KXJB7MN2P4"
//   - any mix of case: "kxjb7mn2p4", "kxjb7-mn2p4"
//
// It returns an error if the input has the wrong length, contains invalid
// Crockford characters (including the excluded letters I, L, O, U), or is empty.
func Parse(tag string) (AetherTag, error) {
	if tag == "" {
		return AetherTag{}, errors.New("aethertag: tag string is empty")
	}

	upper := strings.ToUpper(tag)

	// Strip an optional central separator.
	stripped := strings.ReplaceAll(upper, "-", "")

	if len(stripped) != tagLength {
		return AetherTag{}, errors.New("aethertag: tag must be exactly 10 base-32 characters (excluding separator)")
	}

	// Validate every character against the Crockford alphabet.
	for _, ch := range stripped {
		if !strings.ContainsRune(crockfordAlphabet, ch) {
			return AetherTag{}, errors.New("aethertag: invalid character in tag: " + string(ch))
		}
	}

	canonical := stripped[:5] + "-" + stripped[5:]
	return AetherTag{Value: canonical}, nil
}

// TryParse is the non-error variant of Parse.
// It returns (AetherTag, true) on success and (AetherTag{}, false) on failure.
func TryParse(tag string) (AetherTag, bool) {
	t, err := Parse(tag)
	if err != nil {
		return AetherTag{}, false
	}
	return t, true
}

// Verify returns true if the supplied tag matches the tag derived from publicKey.
func Verify(tag string, publicKey []byte) bool {
	parsed, err := Parse(tag)
	if err != nil {
		return false
	}
	derived, err := FromPublicKey(publicKey)
	if err != nil {
		return false
	}
	return parsed.Value == derived.Value
}

// IsValid returns true when the AetherTag has a non-empty Value.
func (t AetherTag) IsValid() bool {
	return t.Value != ""
}

// String returns the canonical "XXXXX-XXXXX" representation.
func (t AetherTag) String() string {
	return t.Value
}
