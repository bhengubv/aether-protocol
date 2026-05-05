// SPDX-License-Identifier: MIT

package security

import (
	"crypto/rand"
	"crypto/subtle"
	"fmt"

	"golang.org/x/crypto/curve25519"
)

// X25519 sizes (RFC 7748).
const (
	X25519PublicKeySize   = 32
	X25519PrivateKeySize  = 32
	X25519SharedSecretSize = 32
)

// generateX25519KeyPair returns a fresh X25519 keypair (raw 32-byte private,
// raw 32-byte public).
func generateX25519KeyPair() (priv, pub []byte, err error) {
	priv = make([]byte, X25519PrivateKeySize)
	if _, err = rand.Read(priv); err != nil {
		return nil, nil, fmt.Errorf("rand.Read: %w", err)
	}
	pub, err = curve25519.X25519(priv, curve25519.Basepoint)
	if err != nil {
		return nil, nil, fmt.Errorf("curve25519.X25519 base: %w", err)
	}
	return priv, pub, nil
}

// x25519Agree computes the X25519 ECDH shared secret between localPriv and
// remotePub. Returns 32 raw shared-secret bytes suitable for direct
// concatenation into an HKDF input.
//
// RFC 7748 §6.1 mandates that implementations check the result is not the
// all-zero point — that's a small-subgroup attack indicator via a low-order
// remote public key. curve25519.X25519 already returns an error in that
// case (Go 1.21+), but we double-check defensively.
func x25519Agree(localPriv, remotePub []byte) ([]byte, error) {
	if len(localPriv) != X25519PrivateKeySize {
		return nil, fmt.Errorf("X25519 private key must be %d bytes, got %d", X25519PrivateKeySize, len(localPriv))
	}
	if len(remotePub) != X25519PublicKeySize {
		return nil, fmt.Errorf("X25519 public key must be %d bytes, got %d", X25519PublicKeySize, len(remotePub))
	}
	shared, err := curve25519.X25519(localPriv, remotePub)
	if err != nil {
		return nil, fmt.Errorf("X25519 agreement failed: %w", err)
	}
	if isAllZero(shared) {
		return nil, fmt.Errorf("X25519 produced an all-zero shared secret (low-order point)")
	}
	return shared, nil
}

// x25519DerivePublic returns the X25519 public key for a given raw private
// key (priv * Basepoint).
func x25519DerivePublic(priv []byte) ([]byte, error) {
	if len(priv) != X25519PrivateKeySize {
		return nil, fmt.Errorf("X25519 private key must be %d bytes, got %d", X25519PrivateKeySize, len(priv))
	}
	return curve25519.X25519(priv, curve25519.Basepoint)
}

func isAllZero(b []byte) bool {
	var zero [X25519SharedSecretSize]byte
	return subtle.ConstantTimeCompare(b, zero[:]) == 1
}
