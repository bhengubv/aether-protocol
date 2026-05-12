// SPDX-License-Identifier: MIT

package security

import (
	"bytes"
	"context"
	"testing"

	"github.com/bhengubv/aether-protocol/go/storage"
)

// TestPreKeyPersistence_IdentityAndSPKAndOPKsSurviveRestart establishes
// pre-key state, simulates a restart, and verifies the new service has
// the same identity, the same active SPK id, and the same OPK pool.
func TestPreKeyPersistence_IdentityAndSPKAndOPKsSurviveRestart(t *testing.T) {
	ctx := context.Background()
	kv := storage.NewInMemoryKeyValueStore()
	store := NewKVPreKeyStore(kv)

	// Phase 1.
	bob1, err := NewSignalProtocolService(WithPreKeyStore(store))
	if err != nil {
		t.Fatalf("bob1: %v", err)
	}
	bundle1, err := bob1.GeneratePreKeyBundle("bob")
	if err != nil {
		t.Fatalf("GeneratePreKeyBundle: %v", err)
	}

	identity1, err := store.LoadIdentity(ctx)
	if err != nil {
		t.Fatalf("LoadIdentity: %v", err)
	}
	if identity1 == nil {
		t.Fatalf("LoadIdentity after first bundle: got nil")
	}
	if identity1.LocalUhid != "bob" {
		t.Errorf("LocalUhid: got %q want bob", identity1.LocalUhid)
	}

	history1, err := store.LoadSignedPreKeys(ctx)
	if err != nil {
		t.Fatalf("LoadSignedPreKeys: %v", err)
	}
	if len(history1.Entries) != 1 {
		t.Fatalf("history entries: got %d want 1", len(history1.Entries))
	}
	if history1.Entries[0].ID != bundle1.SignedPreKeyID {
		t.Errorf("history[0].ID: got %d want %d", history1.Entries[0].ID, bundle1.SignedPreKeyID)
	}

	opks1, err := store.LoadOneTimePreKeys(ctx)
	if err != nil {
		t.Fatalf("LoadOneTimePreKeys: %v", err)
	}
	if len(opks1) != DefaultOpkPoolSize {
		t.Errorf("opks: got %d want %d", len(opks1), DefaultOpkPoolSize)
	}

	// Phase 2: restart.
	bob2, err := NewSignalProtocolService(WithPreKeyStore(store))
	if err != nil {
		t.Fatalf("bob2: %v", err)
	}

	// Identity must match exactly.
	if !bytes.Equal(bob1.GetX25519PublicKey(), bob2.GetX25519PublicKey()) {
		t.Errorf("X25519 identity differs across restart")
	}
	if !bytes.Equal(bob1.GetPublicKey(), bob2.GetPublicKey()) {
		t.Errorf("Ed25519 identity differs across restart")
	}

	// Active SPK id must match.
	if bob2.ActiveSignedPreKeyID() != bundle1.SignedPreKeyID {
		t.Errorf("ActiveSignedPreKeyID: got %d want %d",
			bob2.ActiveSignedPreKeyID(), bundle1.SignedPreKeyID)
	}

	// OPK pool size must match (ignoring the one OPK we already issued).
	held, available := bob2.GetOpkPoolStatus()
	if held != DefaultOpkPoolSize {
		t.Errorf("held after restart: got %d want %d", held, DefaultOpkPoolSize)
	}
	// One OPK was issued in phase 1 but not yet consumed — it stays in held but
	// is not in available.
	if available != DefaultOpkPoolSize-1 {
		t.Errorf("available after restart: got %d want %d", available, DefaultOpkPoolSize-1)
	}
}

// TestPreKeyPersistence_OpkConsumptionPersists verifies that after a peer
// consumes an OPK via X3DH, restarting the responder reflects the
// consumption — the consumed OPK is gone.
func TestPreKeyPersistence_OpkConsumptionPersists(t *testing.T) {
	ctx := context.Background()
	kv := storage.NewInMemoryKeyValueStore()
	store := NewKVPreKeyStore(kv)
	sessionKV := storage.NewInMemoryKeyValueStore()
	sessions := NewKVSessionStore(sessionKV)

	bob1, err := NewSignalProtocolService(WithPreKeyStore(store), WithSessionStore(sessions))
	if err != nil {
		t.Fatalf("bob1: %v", err)
	}
	bundle, err := bob1.GeneratePreKeyBundle("bob")
	if err != nil {
		t.Fatalf("GeneratePreKeyBundle: %v", err)
	}
	consumedOpkID := bundle.PreKeyID

	alice, _ := NewSignalProtocolService()
	alice.GeneratePreKeyBundle("alice")
	if err := alice.ProcessPreKeyBundle(bundle); err != nil {
		t.Fatalf("ProcessPreKeyBundle: %v", err)
	}

	// First message — initiator-side X3DH; bob1 consumes the OPK on decrypt.
	first, _ := alice.Encrypt("bob", []byte("first"))
	if _, err := bob1.Decrypt("alice", first); err != nil {
		t.Fatalf("bob1.Decrypt: %v", err)
	}

	// Verify in-memory and on-disk: consumed OPK gone.
	if _, ok := bob1.preKeys.oneTimePreKeys[consumedOpkID]; ok {
		t.Errorf("consumed OPK %d still in bob1 in-memory pool", consumedOpkID)
	}
	opks, err := store.LoadOneTimePreKeys(ctx)
	if err != nil {
		t.Fatalf("LoadOneTimePreKeys: %v", err)
	}
	if _, present := opks[consumedOpkID]; present {
		t.Errorf("consumed OPK %d still in persisted pool", consumedOpkID)
	}

	// Restart — consumed OPK must stay gone.
	bob2, err := NewSignalProtocolService(WithPreKeyStore(store), WithSessionStore(sessions))
	if err != nil {
		t.Fatalf("bob2: %v", err)
	}
	if _, ok := bob2.preKeys.oneTimePreKeys[consumedOpkID]; ok {
		t.Errorf("consumed OPK %d resurrected after restart", consumedOpkID)
	}
}

// TestPreKeyPersistence_SetLocalUhidPersists verifies the UHID is saved
// alongside identity keys when explicitly set.
func TestPreKeyPersistence_SetLocalUhidPersists(t *testing.T) {
	ctx := context.Background()
	kv := storage.NewInMemoryKeyValueStore()
	store := NewKVPreKeyStore(kv)

	svc, err := NewSignalProtocolService(WithPreKeyStore(store))
	if err != nil {
		t.Fatalf("svc: %v", err)
	}
	svc.SetLocalUhid("explicit-uhid")

	stored, err := store.LoadIdentity(ctx)
	if err != nil {
		t.Fatalf("LoadIdentity: %v", err)
	}
	if stored == nil {
		t.Fatalf("LoadIdentity: got nil")
	}
	if stored.LocalUhid != "explicit-uhid" {
		t.Errorf("LocalUhid: got %q want explicit-uhid", stored.LocalUhid)
	}
}
