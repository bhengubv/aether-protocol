// SPDX-License-Identifier: MIT

package storage

import (
	"crypto/sha256"
	"errors"
	"fmt"

	"golang.org/x/crypto/pbkdf2"
)

// DefaultPBKDF2Iterations is the OWASP 2023 recommendation for
// PBKDF2-HMAC-SHA256. Mirrors the C# DerivedDataAtRestKeyProvider default.
//
// Tests pass a smaller count to keep the suite fast — never lower this in
// production code.
const DefaultPBKDF2Iterations = 600_000

const (
	derivedKeyByteLength = 32 // AES-256
	minimumSaltLength    = 16
)

// DerivedDataAtRestKeyProvider is an IDataAtRestKeyProvider that derives a
// 32-byte AES-256 key from a passphrase and a salt using PBKDF2-HMAC-SHA256.
// The derived key is cached for the lifetime of the provider so the
// (relatively expensive) PBKDF2 computation runs exactly once per
// passphrase/version pair.
//
// Production iteration count: 600,000. Matches OWASP 2023 PBKDF2-HMAC-SHA256
// recommendation and the C# DerivedDataAtRestKeyProvider default.
//
// The salt is required, must be at least 16 bytes, and MUST be unique to
// this device (or this trust boundary). Reusing the same passphrase + salt
// across devices would let an attacker who recovered the salt from one
// device decrypt blobs from another — domain-separate by appending an
// install-id, hardware-id, or randomly generated per-device value.
//
// To rotate the key, construct a new provider via WithRotation — the old
// version keeps decrypting historical blobs while new writes use the new key.
type DerivedDataAtRestKeyProvider struct {
	derivedKeys    map[int][]byte
	currentVersion int
	iterations     int
}

// NewDerivedDataAtRestKeyProvider derives version 1 from the supplied
// passphrase and salt with the given iteration count.
//
// passphrase: the user/host passphrase; UTF-8 encoded before derivation.
// salt:       at least 16 bytes; should be unique per device.
// iterations: PBKDF2 iteration count. Use DefaultPBKDF2Iterations in production.
func NewDerivedDataAtRestKeyProvider(passphrase string, salt []byte, iterations int) (*DerivedDataAtRestKeyProvider, error) {
	if err := validateDerivedInputs(passphrase, salt, iterations); err != nil {
		return nil, err
	}
	key := derivePbkdf2(passphrase, salt, iterations)
	return &DerivedDataAtRestKeyProvider{
		derivedKeys:    map[int][]byte{1: key},
		currentVersion: 1,
		iterations:     iterations,
	}, nil
}

// CurrentVersion returns the version stamped onto every newly written blob.
func (p *DerivedDataAtRestKeyProvider) CurrentVersion() int { return p.currentVersion }

// Iterations returns the PBKDF2 iteration count this provider was constructed with.
func (p *DerivedDataAtRestKeyProvider) Iterations() int { return p.iterations }

// GetKey returns the 32-byte AES-256 key for the given version, or nil if
// no such version exists.
func (p *DerivedDataAtRestKeyProvider) GetKey(version int) []byte {
	k, ok := p.derivedKeys[version]
	if !ok {
		return nil
	}
	return k
}

// WithRotation returns a new provider that adds a freshly derived key under
// newVersion (which becomes CurrentVersion) while keeping every existing
// version available for decryption. Use during a rotation window: hosts
// swap the registered provider, run a rewrap across the store in the
// background, then drop the old key on the next deploy.
func (p *DerivedDataAtRestKeyProvider) WithRotation(newVersion int, newPassphrase string, newSalt []byte, iterations int) (*DerivedDataAtRestKeyProvider, error) {
	if newVersion < 1 || newVersion > 255 {
		return nil, fmt.Errorf("storage: newVersion=%d outside [1, 255]", newVersion)
	}
	if _, exists := p.derivedKeys[newVersion]; exists {
		return nil, fmt.Errorf("storage: version %d already exists in provider", newVersion)
	}
	iters := iterations
	if iters <= 0 {
		iters = p.iterations
	}
	if err := validateDerivedInputs(newPassphrase, newSalt, iters); err != nil {
		return nil, err
	}

	next := make(map[int][]byte, len(p.derivedKeys)+1)
	for v, k := range p.derivedKeys {
		next[v] = k
	}
	next[newVersion] = derivePbkdf2(newPassphrase, newSalt, iters)
	return &DerivedDataAtRestKeyProvider{
		derivedKeys:    next,
		currentVersion: newVersion,
		iterations:     iters,
	}, nil
}

func validateDerivedInputs(passphrase string, salt []byte, iterations int) error {
	if passphrase == "" {
		return errors.New("storage: passphrase cannot be empty")
	}
	if salt == nil {
		return errors.New("storage: salt cannot be nil")
	}
	if len(salt) < minimumSaltLength {
		return fmt.Errorf("storage: salt must be at least %d bytes, got %d", minimumSaltLength, len(salt))
	}
	if iterations < 1 {
		return fmt.Errorf("storage: iterations must be positive, got %d", iterations)
	}
	return nil
}

// derivePbkdf2 derives a 32-byte AES-256 key from passphrase + salt via
// PBKDF2-HMAC-SHA256. Defensive copy of salt before derivation so caller
// mutations don't affect the cached key.
func derivePbkdf2(passphrase string, salt []byte, iterations int) []byte {
	saltCopy := make([]byte, len(salt))
	copy(saltCopy, salt)
	return pbkdf2.Key([]byte(passphrase), saltCopy, iterations, derivedKeyByteLength, sha256.New)
}
