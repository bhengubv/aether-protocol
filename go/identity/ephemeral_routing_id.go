// SPDX-License-Identifier: MIT

// Ephemeral Routing Id (ERID) — a rotating, key-derived wire address designed to
// replace the stable, phone-derived UHID on the public wire.
//
// # The problem it solves
//
// A node's UHID is SHA-256(phone : deviceId : publicKey) — stable for the life of
// the install and carried in cleartext on every packet. A passive observer who
// never breaks any encryption can therefore (a) follow any node indefinitely across
// time and place, and (b) — because the value is phone-derived — attempt to confirm
// a suspected phone number by recomputing the hash. That is a surveillance and
// targeting primitive, independent of the fact that message contents are E2E-encrypted.
//
// # The design
//
//		ERID(epoch) = base32( HMAC-SHA256(routingKey, epoch) )[0..length]
//
//	  - routingKey is SECRET — derived from the node's identity secret via
//	    DeriveRoutingKey. It is NEVER derived from the public key: if it were, anyone
//	    could recompute the whole schedule and unlinkability would be lost.
//	  - epoch = floor(unixSeconds / epochSeconds) — a 15-minute window by default.
//	  - Two ERIDs from the same node in different epochs are cryptographically
//	    uncorrelated to an outside observer — no cross-time linkage, no phone recovery.
//	  - A peer that needs to address the node learns its routingKey (or a window of
//	    upcoming ERIDs) inside the established Signal session.
//
// The epoch is encoded big-endian (8-byte signed int64) so every language port
// produces byte-identical input to the HMAC.
package identity

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/binary"
	"errors"
	"io"

	"golang.org/x/crypto/hkdf"
)

// DefaultEpochSeconds is the default rotation window: 15 minutes, in seconds.
const DefaultEpochSeconds = 900

// DefaultEridLength is the default ERID length in Crockford base-32 characters
// (16 chars × 5 bits = 80 bits of entropy).
const DefaultEridLength = 16

// eridRoutingKeyInfo is the HKDF domain-separation label so a routing key can never
// collide with another key derived from the same identity secret for a different
// purpose. Must match the C# reference (and every other port) exactly.
var eridRoutingKeyInfo = []byte("aether-erid-routing-key-v1")

// DeriveRoutingKey derives the 32-byte SECRET routing key from a node's identity
// secret (e.g. its Ed25519 private-key bytes). Domain-separated via HKDF-SHA256
// (RFC 5869, no salt). MUST be fed a secret — never a public value, or the rotation
// schedule becomes computable by anyone.
//
// Returns an error if identitySecret is empty.
func DeriveRoutingKey(identitySecret []byte) ([]byte, error) {
	if len(identitySecret) == 0 {
		return nil, errors.New("erid: identitySecret cannot be empty")
	}
	r := hkdf.New(sha256.New, identitySecret, nil, eridRoutingKeyInfo)
	key := make([]byte, 32)
	if _, err := io.ReadFull(r, key); err != nil {
		return nil, err
	}
	return key, nil
}

// EpochFor returns the epoch (rotation-window index) that contains the given Unix
// time. Negative unixSeconds clamp to 0. Panics if epochSeconds is not positive.
func EpochFor(unixSeconds int64, epochSeconds int) int64 {
	if epochSeconds <= 0 {
		panic("erid: epochSeconds must be positive")
	}
	if unixSeconds < 0 {
		unixSeconds = 0
	}
	return unixSeconds / int64(epochSeconds)
}

// DeriveERID derives the ERID for the epoch that contains unixSeconds.
func DeriveERID(routingKey []byte, unixSeconds int64, epochSeconds, length int) (string, error) {
	return DeriveERIDForEpoch(routingKey, EpochFor(unixSeconds, epochSeconds), length)
}

// DeriveERIDForEpoch derives the ERID for an explicit epoch number. The epoch is
// encoded big-endian so every language port produces byte-identical HMAC input.
//
// Returns an error if routingKey is empty or length is outside 1..51.
func DeriveERIDForEpoch(routingKey []byte, epoch int64, length int) (string, error) {
	if len(routingKey) == 0 {
		return "", errors.New("erid: routingKey cannot be empty")
	}
	if length < 1 || length > 51 {
		return "", errors.New("erid: length must be 1..51 (SHA-256 is 256 bits = 51 base-32 chars)")
	}

	var epochBytes [8]byte
	binary.BigEndian.PutUint64(epochBytes[:], uint64(epoch))

	mac := hmac.New(sha256.New, routingKey)
	mac.Write(epochBytes[:])
	sum := mac.Sum(nil)

	return eridBase32(sum, length), nil
}

// crockfordAlphabet is reused from aethernet_tag.go (same package).

// eridBase32 encodes the first (length × 5) bits of data as Crockford base-32,
// most-significant bit first.
func eridBase32(data []byte, length int) string {
	out := make([]byte, length)
	bitPos := 0
	for i := 0; i < length; i++ {
		byteIndex := bitPos >> 3
		bitOffset := bitPos & 7
		hi := int(data[byteIndex])
		lo := 0
		if byteIndex+1 < len(data) {
			lo = int(data[byteIndex+1])
		}
		window := (hi << 8) | lo
		val := (window >> (11 - bitOffset)) & 0x1F
		out[i] = crockfordAlphabet[val]
		bitPos += 5
	}
	return string(out)
}
