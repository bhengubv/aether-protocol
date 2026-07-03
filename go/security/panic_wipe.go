// SPDX-License-Identifier: MIT

package security

import (
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"fmt"
)

// Panic-wipe: the identity-erasure core of an AetherNet node's duress defence.
// A duress PIN (or panic button) irreversibly destroys the node's key material,
// so a seized device reveals nothing and looks like a fresh install.
//
// This is the protocol-level core — deterministic and portable across every
// AetherNet SDK:
//
//   - DuressPinHash / VerifyDuressPin — recognise the duress PIN (SHA-256,
//     constant-time compare); the PIN itself is never stored.
//   - SecureErase — best-effort in-memory erase of key material (overwrite with
//     random, then zero).
//   - IdentityKeyNames + PreKeyName / SignedPreKeyName — the canonical set of
//     key-store entries a wipe must destroy.
//
// Destroying the hosting app's local database, platform keychain entries and any
// decoy store is the app's job — it owns that storage. This gives the app the
// crypto trigger, the secure-erase primitive, and the manifest of what to remove,
// so every app wipes the same identity material the same way.

// MaxPreKeys is the number of one-time / signed pre-key slots a wipe sweeps
// (0..N-1).
const MaxPreKeys = 200

// IdentityKeyNames returns the key-store entry names that together constitute an
// AetherNet identity — everything a panic-wipe must destroy, besides the numbered
// pre-keys. The order is canonical and shared across every SDK.
func IdentityKeyNames() []string {
	return []string{
		"aether_identity_pub",
		"aether_identity_priv",
		"aether_identity_generated",
		"aether_device_salt",
		"aether_drk",
		"aether_ble_rotation_key",
		"aether_ble_irk",
	}
}

// PreKeyName returns the key-store name of the i-th one-time pre-key.
func PreKeyName(index int) string {
	return fmt.Sprintf("prekey_%d", index)
}

// SignedPreKeyName returns the key-store name of the i-th signed pre-key.
func SignedPreKeyName(index int) string {
	return fmt.Sprintf("signed_prekey_%d", index)
}

// DuressPinHash returns the duress-PIN hash: SHA-256 of the UTF-8 PIN. Stored at
// setup and compared on unlock — the PIN is only ever kept as this hash.
func DuressPinHash(pin string) []byte {
	sum := sha256.Sum256([]byte(pin))
	return sum[:]
}

// VerifyDuressPin reports, in constant time, whether pin matches a stored
// DuressPinHash — i.e. whether unlocking should trigger a wipe. It returns false
// for a stored hash that is not exactly 32 bytes.
func VerifyDuressPin(pin string, storedHash []byte) bool {
	if len(storedHash) != 32 {
		return false
	}
	return subtle.ConstantTimeCompare(DuressPinHash(pin), storedHash) == 1
}

// SecureErase does a best-effort secure erase of in-memory key material:
// overwrite with random bytes, then zero. Call on every buffer holding a secret
// before releasing it. Defence in depth — the runtime or OS may still hold
// copies, but this removes the obvious one and leaves no plaintext secret in the
// buffer. A nil or empty buffer is a no-op.
func SecureErase(buffer []byte) {
	if len(buffer) == 0 {
		return
	}
	// Overwrite with random bytes first. rand.Read on crypto/rand never returns a
	// short read without an error; if it did error we still zero below, so the
	// buffer never keeps its secret either way.
	_, _ = rand.Read(buffer)
	for i := range buffer {
		buffer[i] = 0
	}
}
