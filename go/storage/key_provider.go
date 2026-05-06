// SPDX-License-Identifier: MIT

package storage

// IDataAtRestKeyProvider supplies the AES-256 master key(s) used by
// EncryptedKeyValueStore to encrypt and decrypt persisted values at rest.
//
// Two responsibilities, mirroring the C# IDataAtRestKeyProvider:
//
//   - CurrentVersion tells the wrapper which key version to stamp onto every
//     newly written blob. Hosts increment this to roll the key.
//   - GetKey hands back the 32-byte AES-256 key for a given version on read.
//     During a key-rotation window, the provider keeps both the old and new
//     key so previously written blobs continue to decrypt.
//
// Hosts derive these bytes however they like — from a passphrase via
// PBKDF2 (DerivedDataAtRestKeyProvider), from the OS keychain, from a
// hardware enclave, or from a remote KMS. The wrapper never sees the source.
//
// All keys returned by GetKey MUST be exactly 32 bytes (AES-256).
type IDataAtRestKeyProvider interface {
	// CurrentVersion is stamped onto every blob written via this provider.
	// Must be in the range [1, 255] so it fits in the single-byte version
	// header of the encrypted blob format.
	CurrentVersion() int

	// GetKey returns the 32-byte AES-256 key for the given version, or nil
	// if the provider has no key for that version (the blob was written
	// under a key that has since been retired). The wrapper treats a nil
	// result as "cannot decrypt — return nil to caller".
	GetKey(version int) []byte
}
