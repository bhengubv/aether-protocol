// SPDX-License-Identifier: MIT

package storage

import (
	"errors"
	"fmt"
)

// StaticDataAtRestKeyProvider is a simple IDataAtRestKeyProvider backed by
// one or more pre-derived 32-byte AES-256 keys that the host supplies
// directly. Useful for tests, demos, and deployments that derive key
// material out of band (OS keychain, hardware enclave, remote KMS) and
// just need to inject the resulting bytes into the wrapper.
//
// Mirrors the C# StaticDataAtRestKeyProvider.
type StaticDataAtRestKeyProvider struct {
	keys           map[int][]byte
	currentVersion int
}

// NewStaticDataAtRestKeyProvider creates a single-version provider where
// key is the AES-256 master key and currentVersion is 1.
func NewStaticDataAtRestKeyProvider(key []byte) (*StaticDataAtRestKeyProvider, error) {
	cp, err := validateKey(key)
	if err != nil {
		return nil, err
	}
	return &StaticDataAtRestKeyProvider{
		keys:           map[int][]byte{1: cp},
		currentVersion: 1,
	}, nil
}

// NewStaticDataAtRestKeyProviderMulti creates a multi-version provider for
// key-rotation deployments. Every value in keysByVersion must be 32 bytes;
// currentVersion must reference a key present in the dictionary and be in
// the range [1, 255].
func NewStaticDataAtRestKeyProviderMulti(keysByVersion map[int][]byte, currentVersion int) (*StaticDataAtRestKeyProvider, error) {
	if keysByVersion == nil {
		return nil, errors.New("storage: keysByVersion cannot be nil")
	}
	if currentVersion < 1 || currentVersion > 255 {
		return nil, fmt.Errorf("storage: currentVersion=%d outside [1, 255]", currentVersion)
	}
	if _, ok := keysByVersion[currentVersion]; !ok {
		return nil, fmt.Errorf("storage: keysByVersion missing entry for currentVersion=%d", currentVersion)
	}

	out := make(map[int][]byte, len(keysByVersion))
	for v, k := range keysByVersion {
		if v < 1 || v > 255 {
			return nil, fmt.Errorf("storage: key version %d outside [1, 255]", v)
		}
		cp, err := validateKey(k)
		if err != nil {
			return nil, err
		}
		out[v] = cp
	}
	return &StaticDataAtRestKeyProvider{keys: out, currentVersion: currentVersion}, nil
}

// CurrentVersion returns the version stamped onto every newly written blob.
func (p *StaticDataAtRestKeyProvider) CurrentVersion() int { return p.currentVersion }

// GetKey returns the 32-byte AES-256 key for the given version, or nil if
// no such version exists.
func (p *StaticDataAtRestKeyProvider) GetKey(version int) []byte {
	k, ok := p.keys[version]
	if !ok {
		return nil
	}
	return k
}

// validateKey returns a defensive copy of key, or an error if key is the
// wrong length. Caller cannot subsequently zero our internal buffer.
func validateKey(key []byte) ([]byte, error) {
	if key == nil {
		return nil, errors.New("storage: key cannot be nil")
	}
	if len(key) != 32 {
		return nil, fmt.Errorf("storage: data-at-rest key must be 32 bytes (AES-256), got %d", len(key))
	}
	cp := make([]byte, len(key))
	copy(cp, key)
	return cp, nil
}
