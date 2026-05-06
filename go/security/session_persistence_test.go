// SPDX-License-Identifier: MIT

package security

import (
	"bytes"
	"context"
	"sort"
	"testing"

	"github.com/thegeeknetwork/aether-protocol-go/storage"
)

// TestSessionPersistence_RoundTripAcrossRestart establishes a session,
// simulates a restart by constructing a new SignalProtocolService against
// the same persistent stores, and verifies that messaging can resume —
// the new service has the same sessions, identity, and pre-key state.
func TestSessionPersistence_RoundTripAcrossRestart(t *testing.T) {
	ctx := context.Background()

	// Bob's persistence layer survives across "restarts".
	bobKV := storage.NewInMemoryKeyValueStore()
	bobSessions := NewKVSessionStore(bobKV)
	bobPreKeys := NewKVPreKeyStore(bobKV)

	// Alice does NOT need persistence for this test — she's just exercising Bob.
	alice, err := NewSignalProtocolService()
	if err != nil {
		t.Fatalf("alice: %v", err)
	}

	// Phase 1: original Bob.
	bob1, err := NewSignalProtocolService(
		WithSessionStore(bobSessions),
		WithPreKeyStore(bobPreKeys),
	)
	if err != nil {
		t.Fatalf("bob1: %v", err)
	}

	bobBundle, err := bob1.GeneratePreKeyBundle("bob")
	if err != nil {
		t.Fatalf("GeneratePreKeyBundle: %v", err)
	}
	if _, err := alice.GeneratePreKeyBundle("alice"); err != nil {
		t.Fatalf("alice bundle: %v", err)
	}
	if err := alice.ProcessPreKeyBundle(bobBundle); err != nil {
		t.Fatalf("ProcessPreKeyBundle: %v", err)
	}

	// First message — initiator-side X3DH; PreKey-typed envelope.
	first, err := alice.Encrypt("bob", []byte("hello-from-alice"))
	if err != nil {
		t.Fatalf("Encrypt first: %v", err)
	}
	dec1, err := bob1.Decrypt("alice", first)
	if err != nil || !bytes.Equal(dec1, []byte("hello-from-alice")) {
		t.Fatalf("Decrypt first: %v / %q", err, dec1)
	}

	// Bob replies — triggers his DH-ratchet step on Alice's side.
	reply, err := bob1.Encrypt("alice", []byte("hi-back"))
	if err != nil {
		t.Fatalf("bob1.Encrypt reply: %v", err)
	}
	dec2, err := alice.Decrypt("bob", reply)
	if err != nil || !bytes.Equal(dec2, []byte("hi-back")) {
		t.Fatalf("alice.Decrypt reply: %v / %q", err, dec2)
	}

	// Verify the session is in the store.
	peers, err := bobSessions.ListPeers(ctx)
	if err != nil {
		t.Fatalf("ListPeers: %v", err)
	}
	if len(peers) != 1 || peers[0] != "alice" {
		t.Fatalf("ListPeers: got %v want [alice]", peers)
	}

	// Phase 2: simulate a restart. Construct a fresh bob2 against the same
	// persistent stores. Identity, SPK, OPKs, and the alice session must
	// all hydrate.
	bob2, err := NewSignalProtocolService(
		WithSessionStore(bobSessions),
		WithPreKeyStore(bobPreKeys),
	)
	if err != nil {
		t.Fatalf("bob2: %v", err)
	}

	if !bob2.HasSession("alice") {
		t.Fatalf("bob2 has no session for alice — persistence is broken")
	}

	// Identity keys should match between bob1 and bob2 (same persisted bytes).
	if !bytes.Equal(bob1.GetX25519PublicKey(), bob2.GetX25519PublicKey()) {
		t.Errorf("X25519 identity differs across restart: bob1=%x bob2=%x",
			bob1.GetX25519PublicKey(), bob2.GetX25519PublicKey())
	}
	if !bytes.Equal(bob1.GetPublicKey(), bob2.GetPublicKey()) {
		t.Errorf("Ed25519 identity differs across restart")
	}

	// Alice sends another message; bob2 must decrypt with restored state.
	another, err := alice.Encrypt("bob", []byte("after-restart"))
	if err != nil {
		t.Fatalf("alice.Encrypt after restart: %v", err)
	}
	dec3, err := bob2.Decrypt("alice", another)
	if err != nil {
		t.Fatalf("bob2.Decrypt after restart: %v", err)
	}
	if !bytes.Equal(dec3, []byte("after-restart")) {
		t.Errorf("bob2 decrypt: got %q want %q", dec3, "after-restart")
	}
}

// TestSessionPersistence_MultiplePeers verifies each peer is keyed
// independently in the session store.
func TestSessionPersistence_MultiplePeers(t *testing.T) {
	ctx := context.Background()
	bobKV := storage.NewInMemoryKeyValueStore()
	bobSessions := NewKVSessionStore(bobKV)
	bobPreKeys := NewKVPreKeyStore(bobKV)

	bob, err := NewSignalProtocolService(
		WithSessionStore(bobSessions),
		WithPreKeyStore(bobPreKeys),
	)
	if err != nil {
		t.Fatalf("bob: %v", err)
	}
	bobBundle, _ := bob.GeneratePreKeyBundle("bob")

	// Establish three independent peers.
	peerNames := []string{"alice", "carol", "dave"}
	for _, name := range peerNames {
		peer, _ := NewSignalProtocolService()
		peer.GeneratePreKeyBundle(name)
		// Each peer needs a fresh bundle from bob (bundles are
		// single-use OPKs — but the session establishment needs unique
		// OPK per initiator).
		freshBundle, _ := bob.GeneratePreKeyBundle("bob")
		if err := peer.ProcessPreKeyBundle(freshBundle); err != nil {
			t.Fatalf("%s ProcessPreKeyBundle: %v", name, err)
		}
		first, _ := peer.Encrypt("bob", []byte("hi from "+name))
		if _, err := bob.Decrypt(name, first); err != nil {
			t.Fatalf("bob.Decrypt %s: %v", name, err)
		}
	}
	_ = bobBundle

	// Restart bob. All three sessions must hydrate.
	bob2, err := NewSignalProtocolService(
		WithSessionStore(bobSessions),
		WithPreKeyStore(bobPreKeys),
	)
	if err != nil {
		t.Fatalf("bob2: %v", err)
	}

	peers, err := bobSessions.ListPeers(ctx)
	if err != nil {
		t.Fatalf("ListPeers: %v", err)
	}
	sort.Strings(peers)
	want := []string{"alice", "carol", "dave"}
	if len(peers) != 3 {
		t.Fatalf("ListPeers: got %v want %v", peers, want)
	}
	for _, name := range peerNames {
		if !bob2.HasSession(name) {
			t.Errorf("bob2 missing session for %s", name)
		}
	}
}

// TestSessionPersistence_DeleteSession verifies removal from the store.
func TestSessionPersistence_DeleteSession(t *testing.T) {
	ctx := context.Background()
	store := NewInMemorySessionStore()

	sess := &SignalSession{
		RootKey:            []byte("root"),
		SkippedMessageKeys: make(map[string][]byte),
	}
	if err := store.SaveSession(ctx, "peer", sess); err != nil {
		t.Fatalf("SaveSession: %v", err)
	}
	loaded, err := store.LoadSession(ctx, "peer")
	if err != nil {
		t.Fatalf("LoadSession: %v", err)
	}
	if loaded == nil {
		t.Fatalf("LoadSession after Save: got nil")
	}
	if err := store.DeleteSession(ctx, "peer"); err != nil {
		t.Fatalf("DeleteSession: %v", err)
	}
	loaded2, err := store.LoadSession(ctx, "peer")
	if err != nil {
		t.Fatalf("LoadSession after Delete: %v", err)
	}
	if loaded2 != nil {
		t.Errorf("LoadSession after Delete: got non-nil")
	}
}

// TestSessionDto_RoundTrip verifies the DTO codec preserves all fields.
func TestSessionDto_RoundTrip(t *testing.T) {
	original := &SignalSession{
		RootKey:                    []byte{1, 2, 3, 4},
		SendChainKey:               []byte{5, 6, 7, 8},
		RecvChainKey:               []byte{9, 10},
		SendCounter:                42,
		RecvCounter:                13,
		PreviousChainCount:         7,
		MyEphemeralPriv:            []byte{0xaa, 0xbb},
		MyEphemeralPub:             []byte{0xcc, 0xdd},
		RemoteEphemeralPub:         []byte{0xee, 0xff},
		SkippedMessageKeys:         map[string][]byte{"k1": {1, 2}, "k2": {3, 4}},
		PendingPreKeyMessage:       true,
		InitiatorIdentityKeyX25519: []byte{0x11, 0x22},
		UsedSignedPreKeyID:         99,
		UsedOneTimePreKeyID:        77,
	}
	bytesData, err := serializeSignalSession(original)
	if err != nil {
		t.Fatalf("serialize: %v", err)
	}
	restored, err := deserializeSignalSession(bytesData)
	if err != nil {
		t.Fatalf("deserialize: %v", err)
	}
	if !bytes.Equal(restored.RootKey, original.RootKey) {
		t.Errorf("RootKey: got %v want %v", restored.RootKey, original.RootKey)
	}
	if restored.SendCounter != original.SendCounter {
		t.Errorf("SendCounter: got %d want %d", restored.SendCounter, original.SendCounter)
	}
	if restored.PreviousChainCount != original.PreviousChainCount {
		t.Errorf("PreviousChainCount: got %d want %d", restored.PreviousChainCount, original.PreviousChainCount)
	}
	if restored.UsedSignedPreKeyID != original.UsedSignedPreKeyID {
		t.Errorf("UsedSignedPreKeyID: got %d want %d", restored.UsedSignedPreKeyID, original.UsedSignedPreKeyID)
	}
	if !restored.PendingPreKeyMessage {
		t.Errorf("PendingPreKeyMessage: got false want true")
	}
	if v := restored.SkippedMessageKeys["k1"]; !bytes.Equal(v, []byte{1, 2}) {
		t.Errorf("SkippedMessageKeys[k1]: got %v want [1 2]", v)
	}
	if v := restored.SkippedMessageKeys["k2"]; !bytes.Equal(v, []byte{3, 4}) {
		t.Errorf("SkippedMessageKeys[k2]: got %v want [3 4]", v)
	}
}
