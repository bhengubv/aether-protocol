// SPDX-License-Identifier: MIT

package security

import (
	"crypto/ecdsa"
	"crypto/ed25519"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
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

// VerifyWithFallback verifies a signature, trying Ed25519 first and falling back to
// legacy P-256 ECDSA for public keys longer than 32 bytes (Protocol Version 1
// identity keys during the migration window — see PROTOCOL_SPEC.md §7.5).
//
// A 32-byte key takes the Ed25519 path; a longer key is a DER SubjectPublicKeyInfo
// P-256 key verified against an ASN.1 DER ECDSA signature over SHA-256.
func (es *Ed25519Service) VerifyWithFallback(publicKey, data, signature []byte) bool {
	if publicKey == nil || data == nil || signature == nil {
		return false
	}
	if len(publicKey) == 32 {
		return es.Verify(publicKey, data, signature)
	}
	return verifyP256(publicKey, data, signature)
}

// verifyP256 verifies a legacy P-256 (secp256r1) ECDSA signature over SHA-256.
// Public key is X.509 SubjectPublicKeyInfo (DER); signature is ASN.1 DER.
func verifyP256(spkiPublicKey, data, derSignature []byte) bool {
	pub, err := x509.ParsePKIXPublicKey(spkiPublicKey)
	if err != nil {
		return false
	}
	ecPub, ok := pub.(*ecdsa.PublicKey)
	if !ok || ecPub.Curve != elliptic.P256() {
		return false
	}
	digest := sha256.Sum256(data)
	return ecdsa.VerifyASN1(ecPub, digest[:], derSignature)
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
