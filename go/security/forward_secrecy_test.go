// SPDX-License-Identifier: MIT

package security

import (
	"bytes"
	"testing"
)

// Forward-secrecy deletion proof tests.
//
// These three tests verify the core forward-secrecy guarantee of the
// Double-Ratchet: once a message key has been consumed and the ratchet
// has advanced, the ciphertext cannot be re-decrypted and every fresh
// encryption of identical plaintext produces a distinct ciphertext.

// TestForwardSecrecy_ReplayOfConsumedMessageFails advances the ratchet
// past a message and then asserts that re-submitting the original
// ciphertext is rejected.
func TestForwardSecrecy_ReplayOfConsumedMessageFails(t *testing.T) {
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	if err := alice.ProcessPreKeyBundle(bobBundle); err != nil {
		t.Fatalf("ProcessPreKeyBundle: %v", err)
	}

	// Encrypt M1 and let Bob decrypt it (message key consumed).
	m1, err := alice.Encrypt("bob", []byte("m1"))
	if err != nil {
		t.Fatalf("alice.Encrypt m1: %v", err)
	}
	if _, err := bob.Decrypt("alice", m1); err != nil {
		t.Fatalf("bob.Decrypt m1 (first): %v", err)
	}

	// Advance the ratchet with 5 bidirectional exchanges so the sending
	// chain key that produced M1 is long gone.
	for i := 0; i < 5; i++ {
		aEnc, err := alice.Encrypt("bob", []byte("advance"))
		if err != nil {
			t.Fatalf("advance alice.Encrypt[%d]: %v", i, err)
		}
		if _, err := bob.Decrypt("alice", aEnc); err != nil {
			t.Fatalf("advance bob.Decrypt[%d]: %v", i, err)
		}

		bEnc, err := bob.Encrypt("alice", []byte("advance"))
		if err != nil {
			t.Fatalf("advance bob.Encrypt[%d]: %v", i, err)
		}
		if _, err := alice.Decrypt("bob", bEnc); err != nil {
			t.Fatalf("advance alice.Decrypt[%d]: %v", i, err)
		}
	}

	// Replay M1 — must fail.
	_, replayErr := bob.Decrypt("alice", m1)
	if replayErr == nil {
		t.Errorf("expected error replaying consumed message M1, but got nil (forward secrecy violated)")
	}
}

// TestForwardSecrecy_SessionRemainsHealthyAfterReplayAttempt confirms that
// a failed replay attempt does not corrupt the live session: a new message
// sent immediately after the replay attempt must still decrypt correctly.
func TestForwardSecrecy_SessionRemainsHealthyAfterReplayAttempt(t *testing.T) {
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	if err := alice.ProcessPreKeyBundle(bobBundle); err != nil {
		t.Fatalf("ProcessPreKeyBundle: %v", err)
	}

	// Consume M1.
	m1, err := alice.Encrypt("bob", []byte("m1"))
	if err != nil {
		t.Fatalf("alice.Encrypt m1: %v", err)
	}
	if _, err := bob.Decrypt("alice", m1); err != nil {
		t.Fatalf("bob.Decrypt m1: %v", err)
	}

	// Advance the ratchet.
	for i := 0; i < 5; i++ {
		aEnc, err := alice.Encrypt("bob", []byte("advance"))
		if err != nil {
			t.Fatalf("advance alice.Encrypt[%d]: %v", i, err)
		}
		if _, err := bob.Decrypt("alice", aEnc); err != nil {
			t.Fatalf("advance bob.Decrypt[%d]: %v", i, err)
		}

		bEnc, err := bob.Encrypt("alice", []byte("advance"))
		if err != nil {
			t.Fatalf("advance bob.Encrypt[%d]: %v", i, err)
		}
		if _, err := alice.Decrypt("bob", bEnc); err != nil {
			t.Fatalf("advance alice.Decrypt[%d]: %v", i, err)
		}
	}

	// Attempt replay (expected to fail; we don't care about the error value).
	bob.Decrypt("alice", m1) //nolint:errcheck

	// Session must still be usable.
	fresh, err := alice.Encrypt("bob", []byte("post-replay"))
	if err != nil {
		t.Fatalf("alice.Encrypt post-replay: %v", err)
	}
	got, err := bob.Decrypt("alice", fresh)
	if err != nil {
		t.Fatalf("bob.Decrypt post-replay: %v (session corrupted by replay attempt)", err)
	}
	if string(got) != "post-replay" {
		t.Fatalf("post-replay plaintext = %q, want %q", string(got), "post-replay")
	}
}

// TestForwardSecrecy_SamePlaintextProducesDifferentCiphertexts verifies that
// encrypting the same plaintext twice yields distinct wire messages because
// each encryption consumes a fresh message key and a fresh random nonce.
func TestForwardSecrecy_SamePlaintextProducesDifferentCiphertexts(t *testing.T) {
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	if err := alice.ProcessPreKeyBundle(bobBundle); err != nil {
		t.Fatalf("ProcessPreKeyBundle: %v", err)
	}

	const plaintext = "hello"

	c1, err := alice.Encrypt("bob", []byte(plaintext))
	if err != nil {
		t.Fatalf("alice.Encrypt c1: %v", err)
	}
	c2, err := alice.Encrypt("bob", []byte(plaintext))
	if err != nil {
		t.Fatalf("alice.Encrypt c2: %v", err)
	}

	// The serialized wire forms must differ.  Checking both Nonce and
	// Ciphertext is belt-and-suspenders: the random nonce alone
	// guarantees divergence, and the different message keys make the
	// ciphertext bytes independently distinct.
	if bytes.Equal(c1.Nonce, c2.Nonce) {
		t.Errorf("c1 and c2 share the same nonce — should be independently random")
	}
	if bytes.Equal(c1.Ciphertext, c2.Ciphertext) {
		t.Errorf("c1 and c2 have identical ciphertext bytes — different message keys should diverge")
	}

	// Bob must be able to decrypt both.
	got1, err := bob.Decrypt("alice", c1)
	if err != nil {
		t.Fatalf("bob.Decrypt c1: %v", err)
	}
	if string(got1) != plaintext {
		t.Fatalf("c1 plaintext = %q, want %q", string(got1), plaintext)
	}

	got2, err := bob.Decrypt("alice", c2)
	if err != nil {
		t.Fatalf("bob.Decrypt c2: %v", err)
	}
	if string(got2) != plaintext {
		t.Fatalf("c2 plaintext = %q, want %q", string(got2), plaintext)
	}
}
