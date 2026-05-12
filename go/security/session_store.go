// SPDX-License-Identifier: MIT

package security

import (
	"context"
	"fmt"
	"sync"

	"github.com/bhengubv/aether-protocol/go/storage"
)

// ISignalSessionStore is the persistence boundary for Signal-Protocol
// session state. Each session is keyed by the peer's UHID. Implementations
// are responsible for atomicity and durability — the protocol layer hands
// in an opaque *SignalSession and trusts that LoadSession later returns
// the exact same state (or nil if no session was previously stored).
//
// Mirrors the C# ISignalSessionStore interface (renamed to use Go-idiomatic
// method names: LoadSession/SaveSession/DeleteSession/ListPeers, ctx first).
type ISignalSessionStore interface {
	// LoadSession returns the previously-stored session for peerUhid, or
	// (nil, nil) if no session exists.
	LoadSession(ctx context.Context, peerUhid string) (*SignalSession, error)

	// SaveSession persists the session for peerUhid.
	SaveSession(ctx context.Context, peerUhid string, session *SignalSession) error

	// DeleteSession removes the session for peerUhid. No-op if absent.
	DeleteSession(ctx context.Context, peerUhid string) error

	// ListPeers returns every peerUhid for which a session is currently stored.
	ListPeers(ctx context.Context) ([]string, error)
}

// inMemorySessionStore is a process-local, volatile ISignalSessionStore.
// The session bytes are stored as the same JSON envelope a durable store
// would emit, which keeps the round-trip path identical to the production
// code path and makes accidental in-place mutation of stored state
// impossible. Mirrors the C# InMemorySignalSessionStore.
type inMemorySessionStore struct {
	mu       sync.Mutex
	sessions map[string][]byte
}

// NewInMemorySessionStore returns an empty in-memory ISignalSessionStore.
// Suitable for tests and demos. Loses everything on process exit.
func NewInMemorySessionStore() ISignalSessionStore {
	return &inMemorySessionStore{sessions: make(map[string][]byte)}
}

func (s *inMemorySessionStore) LoadSession(ctx context.Context, peerUhid string) (*SignalSession, error) {
	if peerUhid == "" {
		return nil, fmt.Errorf("LoadSession: peerUhid cannot be empty")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	bytes, ok := s.sessions[peerUhid]
	if !ok {
		return nil, nil
	}
	return deserializeSignalSession(bytes)
}

func (s *inMemorySessionStore) SaveSession(ctx context.Context, peerUhid string, session *SignalSession) error {
	if peerUhid == "" {
		return fmt.Errorf("SaveSession: peerUhid cannot be empty")
	}
	if session == nil {
		return fmt.Errorf("SaveSession: session cannot be nil")
	}
	bytes, err := serializeSignalSession(session)
	if err != nil {
		return fmt.Errorf("SaveSession: %w", err)
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.sessions[peerUhid] = bytes
	return nil
}

func (s *inMemorySessionStore) DeleteSession(ctx context.Context, peerUhid string) error {
	if peerUhid == "" {
		return fmt.Errorf("DeleteSession: peerUhid cannot be empty")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.sessions, peerUhid)
	return nil
}

func (s *inMemorySessionStore) ListPeers(ctx context.Context) ([]string, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]string, 0, len(s.sessions))
	for k := range s.sessions {
		out = append(out, k)
	}
	return out, nil
}

// kvSessionStore adapts a storage.IKeyValueStore to ISignalSessionStore.
// Sessions are JSON-encoded under the key "signal:session:<peerUhid>".
//
// Mirrors the C# KeyValueSignalSessionStore byte-for-byte (same prefix,
// same JSON layout) so future cross-language migration is possible.
type kvSessionStore struct {
	kv storage.IKeyValueStore
}

const sessionKeyPrefix = "signal:session:"

// NewKVSessionStore wraps the given storage.IKeyValueStore as an
// ISignalSessionStore. Hosts that want a different on-disk format
// (encrypted-at-rest, sqlite, etc.) wrap the kv argument in
// EncryptedKeyValueStore or supply their own ISignalSessionStore.
func NewKVSessionStore(kv storage.IKeyValueStore) ISignalSessionStore {
	if kv == nil {
		panic("NewKVSessionStore: kv is nil")
	}
	return &kvSessionStore{kv: kv}
}

func (s *kvSessionStore) LoadSession(ctx context.Context, peerUhid string) (*SignalSession, error) {
	if peerUhid == "" {
		return nil, fmt.Errorf("LoadSession: peerUhid cannot be empty")
	}
	bytes, err := s.kv.Get(ctx, sessionKey(peerUhid))
	if err != nil {
		return nil, err
	}
	if bytes == nil {
		return nil, nil
	}
	return deserializeSignalSession(bytes)
}

func (s *kvSessionStore) SaveSession(ctx context.Context, peerUhid string, session *SignalSession) error {
	if peerUhid == "" {
		return fmt.Errorf("SaveSession: peerUhid cannot be empty")
	}
	if session == nil {
		return fmt.Errorf("SaveSession: session cannot be nil")
	}
	bytes, err := serializeSignalSession(session)
	if err != nil {
		return err
	}
	return s.kv.Put(ctx, sessionKey(peerUhid), bytes)
}

func (s *kvSessionStore) DeleteSession(ctx context.Context, peerUhid string) error {
	if peerUhid == "" {
		return fmt.Errorf("DeleteSession: peerUhid cannot be empty")
	}
	_, err := s.kv.Remove(ctx, sessionKey(peerUhid))
	return err
}

func (s *kvSessionStore) ListPeers(ctx context.Context) ([]string, error) {
	keys, err := s.kv.ListKeys(ctx)
	if err != nil {
		return nil, err
	}
	peers := make([]string, 0, len(keys))
	for _, k := range keys {
		if len(k) <= len(sessionKeyPrefix) {
			continue
		}
		if k[:len(sessionKeyPrefix)] != sessionKeyPrefix {
			continue
		}
		peers = append(peers, k[len(sessionKeyPrefix):])
	}
	return peers, nil
}

func sessionKey(peerUhid string) string { return sessionKeyPrefix + peerUhid }
