// SPDX-License-Identifier: MIT

package security

import "time"

// StoredIdentityKeys carries the long-term identity-key material that
// survives across process restarts. The Ed25519 keypair signs pre-key
// bundles; the X25519 keypair participates in X3DH agreement. Both private
// halves stay on the node and are never transmitted.
//
// LocalUhid is persisted alongside the keys so that Encrypt still works
// after a restart without the host having to call SetLocalUhid again.
//
// Mirrors the C# StoredIdentityKeys record.
type StoredIdentityKeys struct {
	Ed25519PrivateKey []byte
	Ed25519PublicKey  []byte
	X25519PrivateKey  []byte
	X25519PublicKey   []byte
	LocalUhid         string
}

// StoredSignedPreKey is one signed pre-key entry as stored in the SPK
// history. Each rotation generates a new entry; the active entry is the
// most-recently-generated one. Older entries are retained for the
// configured rotation window so that messages signed under a recently-
// rotated SPK can still decrypt.
//
// Mirrors the C# StoredSignedPreKey record.
type StoredSignedPreKey struct {
	ID          int32
	PrivateKey  []byte
	PublicKey   []byte
	Signature   []byte
	GeneratedAt time.Time
}

// StoredSignedPreKeyHistory is the full signed-pre-key history: the active
// SPK plus the retained prior entries in generation order (oldest first).
// Empty until first GeneratePreKeyBundle call.
//
// Mirrors the C# StoredSignedPreKeyHistory record.
type StoredSignedPreKeyHistory struct {
	Entries []StoredSignedPreKey
}

// StoredOneTimePreKey is one one-time pre-key in the pool. Removed from
// the store on consumption (Signal §3.3 — each OPK is consumed exactly once).
// Issued reflects whether this OPK has already been handed out in a bundle.
//
// Mirrors the C# StoredOneTimePreKey record.
type StoredOneTimePreKey struct {
	ID         int32
	PrivateKey []byte
	PublicKey  []byte
	Issued     bool
}

// SignedPreKeyRotationOptions configures periodic signed-pre-key rotation
// (Signal §3.3 — keys SHOULD be rotated periodically).
//
// On every GeneratePreKeyBundle call the service checks whether the active
// SPK is older than RotationInterval; if it is, a fresh SPK is generated
// and the old one is appended to the history. The history is then trimmed
// to keep at most RetainedHistoryCount prior entries (plus the new active
// one). Messages signed under any retained SPK still decrypt; messages
// signed under a pruned SPK fail.
//
// Mirrors the C# SignedPreKeyRotationOptions record.
type SignedPreKeyRotationOptions struct {
	RotationInterval     time.Duration
	RetainedHistoryCount int
}

// DefaultSignedPreKeyRotationOptions is the reference policy: rotate every
// 7 days (Signal §3.3 recommends weekly), keep up to 3 prior entries.
func DefaultSignedPreKeyRotationOptions() SignedPreKeyRotationOptions {
	return SignedPreKeyRotationOptions{
		RotationInterval:     7 * 24 * time.Hour,
		RetainedHistoryCount: 3,
	}
}
