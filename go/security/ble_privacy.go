// SPDX-License-Identifier: MIT

package security

import (
	"crypto/aes"
	"crypto/hmac"
	"crypto/sha256"
	"encoding/binary"
	"fmt"
)

// Bluetooth-LE tracking protection: a rotating Service UUID and IRK-based
// Resolvable Private Addresses (RPA), so a mesh node is discoverable by its
// peers without exposing a stable, trackable Bluetooth fingerprint on the air.
//
//   - The Service UUID rotates every 15 minutes, HMAC-SHA256-derived from a
//     shared rotation key and the current time window. Every node in the same
//     window derives the same UUID, so peers still find each other — but a
//     passive scanner sees an identifier that changes and cannot be linked over
//     time.
//   - The node's stable id is removed from the advertisement; a peer that holds
//     the node's 128-bit Identity Resolving Key (IRK) resolves its rotating
//     6-byte RPA instead (the BLE "ah" function).
//
// The window-based operations are deterministic and byte-identical across every
// AetherNet SDK (verified against fixtures/bleprivacy/vectors.json). The time
// window is encoded as a little-endian int64.

// RotationSeconds is the rotation period in seconds (15 minutes).
const RotationSeconds = 900

// WindowFor returns the rotation window index for a Unix-seconds timestamp.
func WindowFor(unixSeconds int64) int64 {
	return unixSeconds / RotationSeconds
}

// ServiceUUID returns the rotating BLE Service UUID for a rotation key and time
// window. Every node sharing the rotation key derives the same UUID within the
// window, enabling mutual discovery with no static identifier on the air.
//
// The result is a lowercase canonical UUID string
// "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx".
func ServiceUUID(rotationKey []byte, window int64) string {
	mac := hmacSHA256(rotationKey, windowBytes(window))
	return formatUUID(mac[:16])
}

// ResolvableAddress returns a 6-byte Resolvable Private Address for a 16-byte
// IRK and time window: hash(3) || prand(3), where prand is HMAC-derived (with
// the RPA address-type bits set) and hash = AES-128(IRK, prand-block). Rotates
// every window; only a peer holding the IRK can link successive addresses.
//
// The IRK must be exactly 16 bytes.
func ResolvableAddress(irk []byte, window int64) ([]byte, error) {
	if len(irk) != 16 {
		return nil, fmt.Errorf("IRK must be 16 bytes, got %d", len(irk))
	}

	mac := hmacSHA256(irk, windowBytes(window))
	prand := make([]byte, 3)
	copy(prand, mac[:3])
	prand[0] = (prand[0] & 0x3F) | 0x40 // RPA address-type bits (0b01)

	hash, err := ah(irk, prand)
	if err != nil {
		return nil, err
	}

	rpa := make([]byte, 6)
	copy(rpa[0:3], hash[0:3])
	copy(rpa[3:6], prand[0:3])
	return rpa, nil
}

// ResolveAddress reports whether rpa was generated from irk — i.e. this node
// recognises the peer behind the rotating address. Returns false for a
// wrong-length IRK (must be 16) or RPA (must be 6), or on any failure.
func ResolveAddress(irk, rpa []byte) bool {
	if len(irk) != 16 || len(rpa) != 6 {
		return false
	}

	prand := make([]byte, 3)
	copy(prand, rpa[3:6])

	hash, err := ah(irk, prand)
	if err != nil {
		return false
	}
	return hmac.Equal(hash[0:3], rpa[0:3])
}

// ah is the BLE "ah" hash: AES-128-ECB(irk, 0^13 || prand), keep the first 3
// bytes. It encrypts a single 16-byte block, no padding.
func ah(irk, prand []byte) ([]byte, error) {
	var block [16]byte
	copy(block[13:16], prand[0:3])

	cipher, err := aes.NewCipher(irk)
	if err != nil {
		return nil, fmt.Errorf("aes.NewCipher: %w", err)
	}

	var ct [16]byte
	cipher.Encrypt(ct[:], block[:])
	out := make([]byte, 3)
	copy(out, ct[0:3])
	return out, nil
}

func windowBytes(window int64) []byte {
	b := make([]byte, 8)
	binary.LittleEndian.PutUint64(b, uint64(window))
	return b
}

func hmacSHA256(key, data []byte) []byte {
	h := hmac.New(sha256.New, key)
	h.Write(data)
	return h.Sum(nil)
}

func formatUUID(b []byte) string {
	return fmt.Sprintf(
		"%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
		b[0], b[1], b[2], b[3],
		b[4], b[5],
		b[6], b[7],
		b[8], b[9],
		b[10], b[11], b[12], b[13], b[14], b[15],
	)
}
