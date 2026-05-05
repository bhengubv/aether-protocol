// SPDX-License-Identifier: MIT

package security

import (
	"bytes"
	"fmt"
	"testing"
)

// Double-Ratchet (Signal §5) end-to-end exercises. These mirror the C#
// reference's `SignalProtocolServiceTests` Double-Ratchet section so any
// behavioral drift between the two implementations surfaces here.

func TestDoubleRatchet_EveryMessageCarriesSenderEphemeralKey(t *testing.T) {
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	if err := alice.ProcessPreKeyBundle(bobBundle); err != nil {
		t.Fatalf("ProcessPreKeyBundle: %v", err)
	}

	first, err := alice.Encrypt("bob", []byte("a"))
	if err != nil {
		t.Fatalf("Encrypt first: %v", err)
	}
	if len(first.SenderEphemeralKeyX25519) != 32 {
		t.Fatalf("first.SenderEphemeralKeyX25519 length = %d, want 32", len(first.SenderEphemeralKeyX25519))
	}

	if _, err := bob.Decrypt("alice", first); err != nil {
		t.Fatalf("Bob.Decrypt first: %v", err)
	}

	// Subsequent message also carries SenderEphemeralKeyX25519 (same value
	// — Alice hasn't ratcheted because Bob hasn't responded yet).
	second, err := alice.Encrypt("bob", []byte("b"))
	if err != nil {
		t.Fatalf("Encrypt second: %v", err)
	}
	if len(second.SenderEphemeralKeyX25519) != 32 {
		t.Fatalf("second.SenderEphemeralKeyX25519 length = %d, want 32", len(second.SenderEphemeralKeyX25519))
	}
	if !bytes.Equal(first.SenderEphemeralKeyX25519, second.SenderEphemeralKeyX25519) {
		t.Fatalf("SenderEphemeralKeyX25519 should match across same-chain messages")
	}
}

func TestDoubleRatchet_SenderEphemeralKey_RotatesAfterRoundtrip(t *testing.T) {
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	alice.ProcessPreKeyBundle(bobBundle)

	// Alice -> Bob: Alice's first ratchet pub.
	aliceFirst, err := alice.Encrypt("bob", []byte("ping"))
	if err != nil {
		t.Fatalf("alice.Encrypt: %v", err)
	}
	if _, err := bob.Decrypt("alice", aliceFirst); err != nil {
		t.Fatalf("bob.Decrypt: %v", err)
	}

	// Bob -> Alice: Bob's first ratchet pub (rotated by responder-side DH ratchet).
	bobReply, err := bob.Encrypt("alice", []byte("pong"))
	if err != nil {
		t.Fatalf("bob.Encrypt: %v", err)
	}
	if len(bobReply.SenderEphemeralKeyX25519) != 32 {
		t.Fatalf("bobReply.SenderEphemeralKeyX25519 length = %d, want 32", len(bobReply.SenderEphemeralKeyX25519))
	}
	// Bob's ratchet pub should be DIFFERENT from Alice's (Bob generated
	// fresh DHs on his DH-ratchet step).
	if bytes.Equal(aliceFirst.SenderEphemeralKeyX25519, bobReply.SenderEphemeralKeyX25519) {
		t.Fatalf("Bob's reply should use a different ratchet pub than Alice's first msg")
	}

	if _, err := alice.Decrypt("bob", bobReply); err != nil {
		t.Fatalf("alice.Decrypt: %v", err)
	}

	// Alice -> Bob (after roundtrip): Alice should now use a NEW ratchet pub
	// (rotated on her DH-ratchet step when she received Bob's reply).
	aliceSecond, err := alice.Encrypt("bob", []byte("ping2"))
	if err != nil {
		t.Fatalf("alice.Encrypt second: %v", err)
	}
	if bytes.Equal(aliceFirst.SenderEphemeralKeyX25519, aliceSecond.SenderEphemeralKeyX25519) {
		t.Fatalf("Alice's second ratchet pub should differ from her first")
	}
	if bytes.Equal(bobReply.SenderEphemeralKeyX25519, aliceSecond.SenderEphemeralKeyX25519) {
		t.Fatalf("Alice's second ratchet pub should differ from Bob's")
	}

	dec, err := bob.Decrypt("alice", aliceSecond)
	if err != nil {
		t.Fatalf("bob.Decrypt aliceSecond: %v", err)
	}
	if string(dec) != "ping2" {
		t.Fatalf("plaintext = %q, want ping2", string(dec))
	}
}

func TestDoubleRatchet_PreviousChainCount_TracksMessagesPerChain(t *testing.T) {
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	alice.ProcessPreKeyBundle(bobBundle)

	// Alice sends 3 messages without a roundtrip.
	for i := 0; i < 3; i++ {
		enc, err := alice.Encrypt("bob", []byte(fmt.Sprintf("a%d", i)))
		if err != nil {
			t.Fatalf("alice.Encrypt %d: %v", i, err)
		}
		// PN is 0 because this IS Alice's first chain.
		if enc.PreviousChainCount != 0 {
			t.Fatalf("enc[%d].PreviousChainCount = %d, want 0", i, enc.PreviousChainCount)
		}
		if _, err := bob.Decrypt("alice", enc); err != nil {
			t.Fatalf("bob.Decrypt %d: %v", i, err)
		}
	}

	// Bob sends a reply, triggering his DH-ratchet step.
	bobReply, err := bob.Encrypt("alice", []byte("hi"))
	if err != nil {
		t.Fatalf("bob.Encrypt: %v", err)
	}
	// Bob's PN reflects however many messages Bob sent in his previous
	// sending chain — which was 0 (Bob hadn't sent anything yet before
	// his DH-ratchet step rotated his chain).
	if bobReply.PreviousChainCount != 0 {
		t.Fatalf("bobReply.PreviousChainCount = %d, want 0", bobReply.PreviousChainCount)
	}
	if _, err := alice.Decrypt("bob", bobReply); err != nil {
		t.Fatalf("alice.Decrypt: %v", err)
	}

	// Alice's next message after her DH-ratchet step. Her PN should be 3
	// — that's how many messages she sent on her previous chain before
	// Bob's reply triggered her ratchet.
	aliceNew, err := alice.Encrypt("bob", []byte("a3"))
	if err != nil {
		t.Fatalf("alice.Encrypt a3: %v", err)
	}
	if aliceNew.PreviousChainCount != 3 {
		t.Fatalf("aliceNew.PreviousChainCount = %d, want 3", aliceNew.PreviousChainCount)
	}
}

func TestDoubleRatchet_OutOfOrderAcrossDhRatchetBoundary_StillDecrypts(t *testing.T) {
	// Alice sends 3 messages on chain 1. Bob receives only the first 2,
	// then Alice does a DH-ratchet (because Bob replied) and sends a 4th
	// on chain 2. The 3rd message (from chain 1) arrives last — Bob must
	// still be able to decrypt it via the skipped-keys cache keyed by
	// (Alice's old DHs pub, counter=2).
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	alice.ProcessPreKeyBundle(bobBundle)

	a0, _ := alice.Encrypt("bob", []byte("a0"))
	a1, _ := alice.Encrypt("bob", []byte("a1"))
	a2, _ := alice.Encrypt("bob", []byte("a2"))

	// Bob receives a0, a1 only.
	dec0, err := bob.Decrypt("alice", a0)
	if err != nil || string(dec0) != "a0" {
		t.Fatalf("Decrypt a0: %v / %q", err, string(dec0))
	}
	dec1, err := bob.Decrypt("alice", a1)
	if err != nil || string(dec1) != "a1" {
		t.Fatalf("Decrypt a1: %v / %q", err, string(dec1))
	}

	// Bob replies — triggers his DH-ratchet step.
	bReply, err := bob.Encrypt("alice", []byte("hi"))
	if err != nil {
		t.Fatalf("bob.Encrypt: %v", err)
	}
	if _, err := alice.Decrypt("bob", bReply); err != nil {
		t.Fatalf("alice.Decrypt: %v", err)
	}

	// Alice sends a4 on her new chain (after her DH-ratchet step).
	a4, err := alice.Encrypt("bob", []byte("a4"))
	if err != nil {
		t.Fatalf("alice.Encrypt a4: %v", err)
	}
	// Bob receives a4 — triggers his second DH-ratchet step. He must
	// skip-derive a key for Alice's old chain counter=2 because PN=3.
	dec4, err := bob.Decrypt("alice", a4)
	if err != nil || string(dec4) != "a4" {
		t.Fatalf("Decrypt a4: %v / %q", err, string(dec4))
	}

	// Now the missing a2 (from Alice's OLD chain) finally arrives. Bob
	// should pull the skipped key from cache.
	dec2, err := bob.Decrypt("alice", a2)
	if err != nil {
		t.Fatalf("Decrypt a2 (from cache): %v", err)
	}
	if string(dec2) != "a2" {
		t.Fatalf("a2 plaintext = %q, want a2", string(dec2))
	}
}

func TestDoubleRatchet_LongConversation_AllMessagesDecrypt(t *testing.T) {
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	alice.ProcessPreKeyBundle(bobBundle)

	// 10 alternating messages — each side ratchets at every roundtrip.
	for i := 0; i < 10; i++ {
		aMsg := fmt.Sprintf("alice %d", i)
		aEnc, err := alice.Encrypt("bob", []byte(aMsg))
		if err != nil {
			t.Fatalf("alice.Encrypt[%d]: %v", i, err)
		}
		got, err := bob.Decrypt("alice", aEnc)
		if err != nil {
			t.Fatalf("bob.Decrypt[%d]: %v", i, err)
		}
		if string(got) != aMsg {
			t.Fatalf("alice msg %d: got %q, want %q", i, string(got), aMsg)
		}

		bMsg := fmt.Sprintf("bob %d", i)
		bEnc, err := bob.Encrypt("alice", []byte(bMsg))
		if err != nil {
			t.Fatalf("bob.Encrypt[%d]: %v", i, err)
		}
		got2, err := alice.Decrypt("bob", bEnc)
		if err != nil {
			t.Fatalf("alice.Decrypt[%d]: %v", i, err)
		}
		if string(got2) != bMsg {
			t.Fatalf("bob msg %d: got %q, want %q", i, string(got2), bMsg)
		}
	}
}

func TestDoubleRatchet_LegacyPayloadWithoutSenderEphemeral_FallsBackToInitiatorEphemeral(t *testing.T) {
	// Backward-compat: a legacy peer that only populates
	// InitiatorEphemeralKeyX25519 (PreKey msg, pre-Double-Ratchet wire)
	// must still be decryptable. The code falls back to
	// InitiatorEphemeralKeyX25519 when SenderEphemeralKeyX25519 is nil.
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	alice.ProcessPreKeyBundle(bobBundle)

	enc, err := alice.Encrypt("bob", []byte("legacy"))
	if err != nil {
		t.Fatalf("alice.Encrypt: %v", err)
	}
	// Simulate a legacy wire envelope: drop SenderEphemeralKeyX25519,
	// keep InitiatorEphemeralKeyX25519 (which Encrypt populated to the
	// same value on PreKey msgs).
	if len(enc.InitiatorEphemeralKeyX25519) != 32 {
		t.Fatalf("PreKey msg should populate InitiatorEphemeralKeyX25519; got len=%d", len(enc.InitiatorEphemeralKeyX25519))
	}
	enc.SenderEphemeralKeyX25519 = nil

	dec, err := bob.Decrypt("alice", enc)
	if err != nil {
		t.Fatalf("bob.Decrypt legacy: %v", err)
	}
	if string(dec) != "legacy" {
		t.Fatalf("plaintext = %q, want legacy", string(dec))
	}
}

func TestDoubleRatchet_RootKeyChangesOnEachDhRatchetStep(t *testing.T) {
	// Smoke test: confirm RootKey advances on each DH-ratchet step. This
	// gives post-compromise security: an attacker who steals one chain
	// key cannot derive future chains.
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	alice.ProcessPreKeyBundle(bobBundle)

	// First exchange: Alice -> Bob (responder-side DH-ratchet on Bob).
	a1, _ := alice.Encrypt("bob", []byte("a1"))
	if _, err := bob.Decrypt("alice", a1); err != nil {
		t.Fatalf("decrypt a1: %v", err)
	}
	bobRoot1 := append([]byte{}, bob.sessions["alice"].RootKey...)

	// Bob replies — triggers Alice's DH-ratchet step.
	b1, _ := bob.Encrypt("alice", []byte("b1"))
	if _, err := alice.Decrypt("bob", b1); err != nil {
		t.Fatalf("decrypt b1: %v", err)
	}
	aliceRoot1 := append([]byte{}, alice.sessions["bob"].RootKey...)

	// Alice sends, Bob receives — Bob does another DH-ratchet step.
	a2, _ := alice.Encrypt("bob", []byte("a2"))
	if _, err := bob.Decrypt("alice", a2); err != nil {
		t.Fatalf("decrypt a2: %v", err)
	}
	bobRoot2 := append([]byte{}, bob.sessions["alice"].RootKey...)

	if bytes.Equal(bobRoot1, bobRoot2) {
		t.Fatalf("Bob's root key should have changed after second DH-ratchet step")
	}
	if len(aliceRoot1) != 32 {
		t.Fatalf("Alice's root key length = %d, want 32", len(aliceRoot1))
	}
}
