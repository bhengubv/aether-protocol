// SPDX-License-Identifier: MIT

package security

import (
	"crypto/ed25519"
	"crypto/rand"
	"fmt"
)

// Ed25519Service provides Ed25519 signing and verification operations.
// Key format: 32-byte seed (private), 32-byte public key, 64-byte signature.
type Ed25519Service struct{}

// NewEd25519Service creates a new Ed25519Service.
func NewEd25519Service() *Ed25519Service {
	return &Ed25519Service{}
}

// GenerateKeyPair generates a new Ed25519 key pair.
// Returns (privateKey: 32-byte seed, publicKey: 32-byte point).
func (es *Ed25519Service) GenerateKeyPair() (privateKey, publicKey []byte, err error) {
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		return nil, nil, fmt.Errorf("failed to generate key pair: %w", err)
	}

	// ed25519 private key is 64 bytes (32-byte seed + 32-byte public key)
	// Extract the 32-byte seed
	seed := priv[:32]

	return seed, pub, nil
}

// Sign signs data using an Ed25519 private key (32-byte seed).
// Returns a 64-byte Ed25519 signature.
func (es *Ed25519Service) Sign(privateKey []byte, data []byte) ([]byte, error) {
	if len(privateKey) != 32 {
		return nil, fmt.Errorf("ed25519 private key must be 32 bytes, got %d", len(privateKey))
	}

	if data == nil {
		return nil, fmt.Errorf("data cannot be nil")
	}

	// Reconstruct full private key (seed + public key)
	// We need to derive the public key from the seed
	// ed25519 private key in Go's format is seed + public key
	fullPrivateKey := ed25519.NewKeyFromSeed(privateKey)

	signature := ed25519.Sign(fullPrivateKey, data)
	if len(signature) != 64 {
		return nil, fmt.Errorf("ed25519 signature must be 64 bytes, got %d", len(signature))
	}

	return signature, nil
}

// Verify verifies an Ed25519 signature using the public key.
func (es *Ed25519Service) Verify(publicKey []byte, data []byte, signature []byte) bool {
	if len(publicKey) != 32 {
		return false
	}

	if len(signature) != 64 {
		return false
	}

	if data == nil {
		return false
	}

	return ed25519.Verify(ed25519.PublicKey(publicKey), data, signature)
}

// ZeroMemory securely zeros out a byte slice.
func ZeroMemory(data []byte) {
	if len(data) == 0 {
		return
	}
	for i := range data {
		data[i] = 0
	}
}
