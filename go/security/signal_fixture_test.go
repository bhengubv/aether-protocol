// SPDX-License-Identifier: MIT

package security

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"golang.org/x/crypto/curve25519"
	"golang.org/x/crypto/hkdf"
)

// Cross-language Signal-protocol fixture verifier. Runs the X3DH and HMAC
// ratchet steps against the canonical inputs in fixtures/signal/inputs.json
// and asserts the outputs match fixtures/signal/expected/*.json byte-for-byte.
//
// Any drift between this Go implementation and the C# reference (or any
// other language) shows up here as a hex mismatch.

type signalInputs struct {
	Cases []map[string]any `json:"cases"`
}

func TestSignalFixture_X3DHBasic(t *testing.T) {
	inputs, expected := loadFixturePair(t, "x3dh_basic")

	aliceIK := mustHex(t, inputs["alice_identity_priv_hex"].(string))
	aliceEK := mustHex(t, inputs["alice_ephemeral_priv_hex"].(string))
	bobIK := mustHex(t, inputs["bob_identity_priv_hex"].(string))
	bobSPK := mustHex(t, inputs["bob_signed_pre_key_priv_hex"].(string))
	bobOPK := mustHex(t, inputs["bob_one_time_pre_key_priv_hex"].(string))

	aliceIKPub := mustDerive(t, aliceIK)
	aliceEKPub := mustDerive(t, aliceEK)
	bobIKPub := mustDerive(t, bobIK)
	bobSPKPub := mustDerive(t, bobSPK)
	bobOPKPub := mustDerive(t, bobOPK)

	// Initiator-side X3DH.
	dh1, err := curve25519.X25519(aliceIK, bobSPKPub)
	if err != nil {
		t.Fatalf("DH1: %v", err)
	}
	dh2, err := curve25519.X25519(aliceEK, bobIKPub)
	if err != nil {
		t.Fatalf("DH2: %v", err)
	}
	dh3, err := curve25519.X25519(aliceEK, bobSPKPub)
	if err != nil {
		t.Fatalf("DH3: %v", err)
	}
	dh4, err := curve25519.X25519(aliceEK, bobOPKPub)
	if err != nil {
		t.Fatalf("DH4: %v", err)
	}

	shared := append(append(append(append([]byte{}, dh1...), dh2...), dh3...), dh4...)
	rootInfo := []byte(inputs["hkdf_root_info_utf8"].(string))
	sendInfo := []byte(inputs["hkdf_chain_initiator_send_info_utf8"].(string))
	recvInfo := []byte(inputs["hkdf_chain_initiator_recv_info_utf8"].(string))

	rootKey := hkdfDerive(shared, rootInfo)
	sendChain := hkdfDerive(rootKey, sendInfo)
	recvChain := hkdfDerive(rootKey, recvInfo)

	checks := map[string][]byte{
		"alice_identity_pub_hex":       aliceIKPub,
		"alice_ephemeral_pub_hex":      aliceEKPub,
		"bob_identity_pub_hex":         bobIKPub,
		"bob_signed_pre_key_pub_hex":   bobSPKPub,
		"bob_one_time_pre_key_pub_hex": bobOPKPub,
		"dh1_hex":                      dh1,
		"dh2_hex":                      dh2,
		"dh3_hex":                      dh3,
		"dh4_hex":                      dh4,
		"shared_secret_hex":            shared,
		"root_key_hex":                 rootKey,
		"initiator_send_chain_key_hex": sendChain,
		"initiator_recv_chain_key_hex": recvChain,
	}
	verifyAll(t, "x3dh_basic", expected, checks)
}

func TestSignalFixture_RatchetStepBasic(t *testing.T) {
	inputs, expected := loadFixturePair(t, "ratchet_step_basic")
	chainKey := mustHex(t, inputs["chain_key_hex"].(string))

	verifyAll(t, "ratchet_step_basic", expected, map[string][]byte{
		"message_key_hex":    hmacOne(chainKey, 0x01),
		"next_chain_key_hex": hmacOne(chainKey, 0x02),
	})
}

func TestSignalFixture_RatchetStepThreeIterations(t *testing.T) {
	inputs, expected := loadFixturePair(t, "ratchet_step_three_iterations")
	chainKey := mustHex(t, inputs["initial_chain_key_hex"].(string))

	checks := make(map[string][]byte)
	for i := 0; i < 3; i++ {
		msgKey := hmacOne(chainKey, 0x01)
		next := hmacOne(chainKey, 0x02)
		checks[stepKey(i, "message_key_hex")] = msgKey
		checks[stepKey(i, "chain_key_after_hex")] = next
		chainKey = next
	}
	verifyAll(t, "ratchet_step_three_iterations", expected, checks)
}

// ─── End-to-end exercises of the X3DH + ratchet flow ─────────────────────

func TestX3DH_EndToEnd_FirstMessageRoundTrips(t *testing.T) {
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, err := bob.GeneratePreKeyBundle("bob")
	if err != nil {
		t.Fatalf("Bob.GeneratePreKeyBundle: %v", err)
	}
	if _, err := alice.GeneratePreKeyBundle("alice"); err != nil {
		t.Fatalf("Alice.GeneratePreKeyBundle: %v", err)
	}
	if err := alice.ProcessPreKeyBundle(bobBundle); err != nil {
		t.Fatalf("Alice.ProcessPreKeyBundle: %v", err)
	}

	encrypted, err := alice.Encrypt("bob", []byte("the mesh is alive"))
	if err != nil {
		t.Fatalf("Alice.Encrypt: %v", err)
	}
	if encrypted.MessageType != MessageTypePreKey {
		t.Fatalf("first msg should be PreKey (1), got %d", encrypted.MessageType)
	}
	if len(encrypted.InitiatorIdentityKeyX25519) != 32 || len(encrypted.InitiatorEphemeralKeyX25519) != 32 {
		t.Fatalf("PreKey msg missing IK/EK")
	}
	if encrypted.SenderUhid != "alice" {
		t.Fatalf("SenderUhid = %q, want alice", encrypted.SenderUhid)
	}

	plaintext, err := bob.Decrypt("alice", encrypted)
	if err != nil {
		t.Fatalf("Bob.Decrypt: %v", err)
	}
	if string(plaintext) != "the mesh is alive" {
		t.Fatalf("plaintext mismatch: %q", string(plaintext))
	}
}

func TestX3DH_SubsequentMessage_IsNormalNotPreKey(t *testing.T) {
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	alice.ProcessPreKeyBundle(bobBundle)

	first, _ := alice.Encrypt("bob", []byte("a"))
	bob.Decrypt("alice", first)

	second, err := alice.Encrypt("bob", []byte("b"))
	if err != nil {
		t.Fatalf("second encrypt: %v", err)
	}
	if second.MessageType != MessageTypeNormal {
		t.Fatalf("second msg should be normal (0), got %d", second.MessageType)
	}
	if len(second.InitiatorIdentityKeyX25519) != 0 {
		t.Fatalf("second msg should not carry IK")
	}

	out, err := bob.Decrypt("alice", second)
	if err != nil {
		t.Fatalf("Bob.Decrypt second: %v", err)
	}
	if string(out) != "b" {
		t.Fatalf("plaintext = %q, want b", string(out))
	}
}

func TestX3DH_BidirectionalAfterFirstMessage(t *testing.T) {
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	alice.ProcessPreKeyBundle(bobBundle)

	a, _ := alice.Encrypt("bob", []byte("ping"))
	if _, err := bob.Decrypt("alice", a); err != nil {
		t.Fatalf("Bob.Decrypt ping: %v", err)
	}

	b, err := bob.Encrypt("alice", []byte("pong"))
	if err != nil {
		t.Fatalf("Bob.Encrypt: %v", err)
	}
	if b.MessageType != MessageTypeNormal {
		t.Fatalf("Bob's reply should be normal (0), got %d", b.MessageType)
	}
	out, err := alice.Decrypt("bob", b)
	if err != nil {
		t.Fatalf("Alice.Decrypt pong: %v", err)
	}
	if string(out) != "pong" {
		t.Fatalf("plaintext = %q, want pong", string(out))
	}
}

func TestX3DH_FiveSequentialMessages_RatchetsForward(t *testing.T) {
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	alice.ProcessPreKeyBundle(bobBundle)

	for i := 0; i < 5; i++ {
		msg := []byte{byte(i)}
		enc, err := alice.Encrypt("bob", msg)
		if err != nil {
			t.Fatalf("encrypt %d: %v", i, err)
		}
		if enc.Counter != int32(i) {
			t.Fatalf("counter %d, want %d", enc.Counter, i)
		}
		dec, err := bob.Decrypt("alice", enc)
		if err != nil {
			t.Fatalf("decrypt %d: %v", i, err)
		}
		if len(dec) != 1 || dec[0] != byte(i) {
			t.Fatalf("plaintext %d mismatch", i)
		}
	}
}

func TestX3DH_OneTimePreKey_ConsumedAfterResponderEstablishes(t *testing.T) {
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()

	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	alice.GeneratePreKeyBundle("alice")
	alice.ProcessPreKeyBundle(bobBundle)

	first, _ := alice.Encrypt("bob", []byte("first"))
	if _, err := bob.Decrypt("alice", first); err != nil {
		t.Fatalf("Bob.Decrypt first: %v", err)
	}

	// A second initiator using the same bundle (and therefore same OPK id)
	// should fail because Bob consumed the OPK.
	alice2, _ := NewSignalProtocolService()
	alice2.GeneratePreKeyBundle("alice2")
	alice2.ProcessPreKeyBundle(bobBundle)
	replay, _ := alice2.Encrypt("bob", []byte("replay"))

	if _, err := bob.Decrypt("alice2", replay); err == nil {
		t.Fatalf("expected error decrypting replayed OPK msg, got nil")
	}
}

func TestEncrypt_WithoutLocalUhid_ReturnsError(t *testing.T) {
	alice, _ := NewSignalProtocolService()
	bob, _ := NewSignalProtocolService()
	bobBundle, _ := bob.GeneratePreKeyBundle("bob")
	// Note: no GeneratePreKeyBundle / SetLocalUhid on Alice.
	if err := alice.ProcessPreKeyBundle(bobBundle); err != nil {
		t.Fatalf("ProcessPreKeyBundle: %v", err)
	}
	if _, err := alice.Encrypt("bob", []byte("x")); err == nil {
		t.Fatalf("expected error when local UHID unset")
	}
}

func TestPreKeyBundle_HasBothIdentityKeys(t *testing.T) {
	svc, _ := NewSignalProtocolService()
	bundle, err := svc.GeneratePreKeyBundle("alice")
	if err != nil {
		t.Fatalf("GeneratePreKeyBundle: %v", err)
	}
	if len(bundle.IdentityKey) != 32 {
		t.Fatalf("Ed25519 identity key length = %d, want 32", len(bundle.IdentityKey))
	}
	if len(bundle.IdentityKeyX25519) != 32 {
		t.Fatalf("X25519 identity key length = %d, want 32", len(bundle.IdentityKeyX25519))
	}
	if len(bundle.SignedPreKey) != 32 {
		t.Fatalf("SPK length = %d, want 32", len(bundle.SignedPreKey))
	}
	if len(bundle.PreKey) != 32 {
		t.Fatalf("OPK length = %d, want 32", len(bundle.PreKey))
	}
	if len(bundle.SignedPreKeySignature) != 64 {
		t.Fatalf("SPK signature length = %d, want 64", len(bundle.SignedPreKeySignature))
	}
}

// ─── Helpers ─────────────────────────────────────────────────────────────

func loadFixturePair(t *testing.T, caseName string) (inputs, expected map[string]any) {
	t.Helper()
	root := repoRoot(t)
	inputsPath := filepath.Join(root, "fixtures", "signal", "inputs.json")
	expectedPath := filepath.Join(root, "fixtures", "signal", "expected", caseName+".json")

	inputsBytes, err := os.ReadFile(inputsPath)
	if err != nil {
		t.Fatalf("read inputs: %v", err)
	}
	var doc signalInputs
	if err := json.Unmarshal(inputsBytes, &doc); err != nil {
		t.Fatalf("unmarshal inputs: %v", err)
	}
	for _, c := range doc.Cases {
		if c["name"] == caseName {
			inputs = c
			break
		}
	}
	if inputs == nil {
		t.Fatalf("case %q not in inputs.json", caseName)
	}

	expectedBytes, err := os.ReadFile(expectedPath)
	if err != nil {
		t.Fatalf("read expected: %v", err)
	}
	if err := json.Unmarshal(expectedBytes, &expected); err != nil {
		t.Fatalf("unmarshal expected: %v", err)
	}
	return inputs, expected
}

func verifyAll(t *testing.T, caseName string, expected map[string]any, actuals map[string][]byte) {
	t.Helper()
	for k, v := range actuals {
		exp, ok := expected[k].(string)
		if !ok {
			t.Errorf("[%s] expected fixture missing field %q", caseName, k)
			continue
		}
		got := hex.EncodeToString(v)
		if exp != got {
			t.Errorf("[%s] %s mismatch:\n  expected: %s\n  actual:   %s", caseName, k, exp, got)
		}
	}
}

func repoRoot(t *testing.T) string {
	t.Helper()
	dir, err := os.Getwd()
	if err != nil {
		t.Fatalf("getwd: %v", err)
	}
	for {
		if _, err := os.Stat(filepath.Join(dir, "AetherProtocol.slnx")); err == nil {
			return dir
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			t.Fatalf("repo root not found from %s", dir)
		}
		dir = parent
	}
}

func mustHex(t *testing.T, s string) []byte {
	t.Helper()
	b, err := hex.DecodeString(s)
	if err != nil {
		t.Fatalf("hex.DecodeString: %v", err)
	}
	return b
}

func mustDerive(t *testing.T, priv []byte) []byte {
	t.Helper()
	pub, err := curve25519.X25519(priv, curve25519.Basepoint)
	if err != nil {
		t.Fatalf("X25519 base: %v", err)
	}
	return pub
}

func hkdfDerive(ikm, info []byte) []byte {
	h := hkdf.New(sha256.New, ikm, nil, info)
	out := make([]byte, 32)
	h.Read(out)
	return out
}

func hmacOne(key []byte, b byte) []byte {
	h := hmac.New(sha256.New, key)
	h.Write([]byte{b})
	return h.Sum(nil)
}

func stepKey(i int, suffix string) string {
	return "step_" + string(rune('0'+i)) + "_" + suffix
}
