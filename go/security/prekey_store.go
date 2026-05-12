// SPDX-License-Identifier: MIT

package security

import (
	"context"
	"encoding/json"
	"fmt"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/bhengubv/aether-protocol/go/storage"
)

// IPreKeyStore is the persistent storage interface for the long-term
// identity keys, signed-pre-key history, and one-time pre-key pool. All
// methods are best-effort from the caller's perspective: failures are
// logged but never propagate up the message-flow stack.
//
// Implementations are not required to be thread-safe; SignalProtocolService
// serialises access through its own pre-key lock before calling.
//
// Mirrors the C# IPreKeyStore interface.
type IPreKeyStore interface {
	LoadIdentity(ctx context.Context) (*StoredIdentityKeys, error)
	SaveIdentity(ctx context.Context, identity *StoredIdentityKeys) error
	LoadSignedPreKeys(ctx context.Context) (StoredSignedPreKeyHistory, error)
	SaveSignedPreKeys(ctx context.Context, history StoredSignedPreKeyHistory) error
	LoadOneTimePreKeys(ctx context.Context) (map[int32]StoredOneTimePreKey, error)
	SaveOneTimePreKeys(ctx context.Context, pool map[int32]StoredOneTimePreKey) error
	ConsumeOneTimePreKey(ctx context.Context, id int32) error
}

// inMemoryPreKeyStore is a process-local IPreKeyStore. Useful for tests
// and demos. Loses everything on process exit.
type inMemoryPreKeyStore struct {
	mu       sync.Mutex
	identity []byte // serialised IdentityDto, or nil
	spkHist  []byte // serialised SpkHistoryDto, or nil
	opks     map[int32][]byte
}

// NewInMemoryPreKeyStore returns an empty in-memory IPreKeyStore.
func NewInMemoryPreKeyStore() IPreKeyStore {
	return &inMemoryPreKeyStore{opks: make(map[int32][]byte)}
}

func (s *inMemoryPreKeyStore) LoadIdentity(ctx context.Context) (*StoredIdentityKeys, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if len(s.identity) == 0 {
		return nil, nil
	}
	return decodeIdentity(s.identity)
}

func (s *inMemoryPreKeyStore) SaveIdentity(ctx context.Context, identity *StoredIdentityKeys) error {
	if identity == nil {
		return fmt.Errorf("SaveIdentity: identity is nil")
	}
	bytes, err := encodeIdentity(identity)
	if err != nil {
		return err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.identity = bytes
	return nil
}

func (s *inMemoryPreKeyStore) LoadSignedPreKeys(ctx context.Context) (StoredSignedPreKeyHistory, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if len(s.spkHist) == 0 {
		return StoredSignedPreKeyHistory{Entries: nil}, nil
	}
	return decodeSpkHistory(s.spkHist)
}

func (s *inMemoryPreKeyStore) SaveSignedPreKeys(ctx context.Context, history StoredSignedPreKeyHistory) error {
	bytes, err := encodeSpkHistory(history)
	if err != nil {
		return err
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.spkHist = bytes
	return nil
}

func (s *inMemoryPreKeyStore) LoadOneTimePreKeys(ctx context.Context) (map[int32]StoredOneTimePreKey, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make(map[int32]StoredOneTimePreKey, len(s.opks))
	for id, b := range s.opks {
		opk, err := decodeOpk(b)
		if err != nil {
			return nil, err
		}
		out[id] = opk
	}
	return out, nil
}

func (s *inMemoryPreKeyStore) SaveOneTimePreKeys(ctx context.Context, pool map[int32]StoredOneTimePreKey) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	// Replace whole pool: remove ids not in the new pool, write all from the new pool.
	for id := range s.opks {
		if _, ok := pool[id]; !ok {
			delete(s.opks, id)
		}
	}
	for id, opk := range pool {
		bytes, err := encodeOpk(opk)
		if err != nil {
			return err
		}
		s.opks[id] = bytes
	}
	return nil
}

func (s *inMemoryPreKeyStore) ConsumeOneTimePreKey(ctx context.Context, id int32) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.opks, id)
	return nil
}

// kvPreKeyStore adapts a storage.IKeyValueStore to IPreKeyStore. Layout
// mirrors the C# KeyValuePreKeyStore byte-for-byte:
//
//	signal:identity      — IdentityDto JSON
//	signal:spk-history   — SpkHistoryDto JSON
//	signal:opk:<id>      — OpkDto JSON, one per id
//
// OPKs are written as one entry per id (rather than one combined blob) so
// ConsumeOneTimePreKey is a single Remove call without a read-modify-write
// cycle on the whole pool.
type kvPreKeyStore struct {
	kv storage.IKeyValueStore
}

const (
	identityKeyName = "signal:identity"
	spkHistoryKey   = "signal:spk-history"
	opkKeyPrefix    = "signal:opk:"
)

// NewKVPreKeyStore wraps the given storage.IKeyValueStore as an IPreKeyStore.
// Hosts that want encryption-at-rest wrap the kv argument in
// EncryptedKeyValueStore.
func NewKVPreKeyStore(kv storage.IKeyValueStore) IPreKeyStore {
	if kv == nil {
		panic("NewKVPreKeyStore: kv is nil")
	}
	return &kvPreKeyStore{kv: kv}
}

func (s *kvPreKeyStore) LoadIdentity(ctx context.Context) (*StoredIdentityKeys, error) {
	bytes, err := s.kv.Get(ctx, identityKeyName)
	if err != nil {
		return nil, err
	}
	if bytes == nil {
		return nil, nil
	}
	return decodeIdentity(bytes)
}

func (s *kvPreKeyStore) SaveIdentity(ctx context.Context, identity *StoredIdentityKeys) error {
	if identity == nil {
		return fmt.Errorf("SaveIdentity: identity is nil")
	}
	bytes, err := encodeIdentity(identity)
	if err != nil {
		return err
	}
	return s.kv.Put(ctx, identityKeyName, bytes)
}

func (s *kvPreKeyStore) LoadSignedPreKeys(ctx context.Context) (StoredSignedPreKeyHistory, error) {
	bytes, err := s.kv.Get(ctx, spkHistoryKey)
	if err != nil {
		return StoredSignedPreKeyHistory{}, err
	}
	if bytes == nil {
		return StoredSignedPreKeyHistory{Entries: nil}, nil
	}
	return decodeSpkHistory(bytes)
}

func (s *kvPreKeyStore) SaveSignedPreKeys(ctx context.Context, history StoredSignedPreKeyHistory) error {
	bytes, err := encodeSpkHistory(history)
	if err != nil {
		return err
	}
	return s.kv.Put(ctx, spkHistoryKey, bytes)
}

func (s *kvPreKeyStore) LoadOneTimePreKeys(ctx context.Context) (map[int32]StoredOneTimePreKey, error) {
	keys, err := s.kv.ListKeys(ctx)
	if err != nil {
		return nil, err
	}
	out := make(map[int32]StoredOneTimePreKey)
	for _, k := range keys {
		if !strings.HasPrefix(k, opkKeyPrefix) {
			continue
		}
		bytes, gerr := s.kv.Get(ctx, k)
		if gerr != nil {
			return nil, gerr
		}
		if bytes == nil {
			continue
		}
		opk, decErr := decodeOpk(bytes)
		if decErr != nil {
			continue
		}
		out[opk.ID] = opk
	}
	return out, nil
}

func (s *kvPreKeyStore) SaveOneTimePreKeys(ctx context.Context, pool map[int32]StoredOneTimePreKey) error {
	// Find existing OPK ids in the store. Any id not present in the new
	// pool must be deleted.
	keys, err := s.kv.ListKeys(ctx)
	if err != nil {
		return err
	}
	existing := make(map[int32]struct{})
	for _, k := range keys {
		if !strings.HasPrefix(k, opkKeyPrefix) {
			continue
		}
		idStr := k[len(opkKeyPrefix):]
		idInt, perr := strconv.ParseInt(idStr, 10, 32)
		if perr != nil {
			continue
		}
		existing[int32(idInt)] = struct{}{}
	}

	// Write each pool entry; remove from "existing" so leftovers are deleted below.
	for id, opk := range pool {
		bytes, eerr := encodeOpk(opk)
		if eerr != nil {
			return eerr
		}
		if perr := s.kv.Put(ctx, opkKey(id), bytes); perr != nil {
			return perr
		}
		delete(existing, id)
	}

	for id := range existing {
		if _, rerr := s.kv.Remove(ctx, opkKey(id)); rerr != nil {
			return rerr
		}
	}
	return nil
}

func (s *kvPreKeyStore) ConsumeOneTimePreKey(ctx context.Context, id int32) error {
	_, err := s.kv.Remove(ctx, opkKey(id))
	return err
}

func opkKey(id int32) string {
	return opkKeyPrefix + strconv.FormatInt(int64(id), 10)
}

// JSON DTOs and codec helpers. Field tags match the C# KeyValuePreKeyStore
// byte-for-byte (same property names, same shape) so future cross-language
// migration is possible.

type identityDto struct {
	Ed25519PrivateKey []byte `json:"ed_pk"`
	Ed25519PublicKey  []byte `json:"ed_pub"`
	X25519PrivateKey  []byte `json:"x_pk"`
	X25519PublicKey   []byte `json:"x_pub"`
	LocalUhid         string `json:"uhid,omitempty"`
}

func encodeIdentity(s *StoredIdentityKeys) ([]byte, error) {
	dto := identityDto{
		Ed25519PrivateKey: s.Ed25519PrivateKey,
		Ed25519PublicKey:  s.Ed25519PublicKey,
		X25519PrivateKey:  s.X25519PrivateKey,
		X25519PublicKey:   s.X25519PublicKey,
		LocalUhid:         s.LocalUhid,
	}
	return json.Marshal(dto)
}

func decodeIdentity(bytes []byte) (*StoredIdentityKeys, error) {
	var dto identityDto
	if err := json.Unmarshal(bytes, &dto); err != nil {
		return nil, err
	}
	return &StoredIdentityKeys{
		Ed25519PrivateKey: dto.Ed25519PrivateKey,
		Ed25519PublicKey:  dto.Ed25519PublicKey,
		X25519PrivateKey:  dto.X25519PrivateKey,
		X25519PublicKey:   dto.X25519PublicKey,
		LocalUhid:         dto.LocalUhid,
	}, nil
}

type spkEntryDto struct {
	ID                int32  `json:"id"`
	PrivateKey        []byte `json:"priv"`
	PublicKey         []byte `json:"pub"`
	Signature         []byte `json:"sig"`
	GeneratedAtUnixMs int64  `json:"at"`
}

type spkHistoryDto struct {
	Entries []spkEntryDto `json:"entries"`
}

func encodeSpkHistory(h StoredSignedPreKeyHistory) ([]byte, error) {
	dto := spkHistoryDto{Entries: make([]spkEntryDto, 0, len(h.Entries))}
	for _, e := range h.Entries {
		dto.Entries = append(dto.Entries, spkEntryDto{
			ID:                e.ID,
			PrivateKey:        e.PrivateKey,
			PublicKey:         e.PublicKey,
			Signature:         e.Signature,
			GeneratedAtUnixMs: e.GeneratedAt.UnixMilli(),
		})
	}
	return json.Marshal(dto)
}

func decodeSpkHistory(bytes []byte) (StoredSignedPreKeyHistory, error) {
	var dto spkHistoryDto
	if err := json.Unmarshal(bytes, &dto); err != nil {
		return StoredSignedPreKeyHistory{}, err
	}
	out := StoredSignedPreKeyHistory{Entries: make([]StoredSignedPreKey, 0, len(dto.Entries))}
	for _, e := range dto.Entries {
		out.Entries = append(out.Entries, StoredSignedPreKey{
			ID:          e.ID,
			PrivateKey:  e.PrivateKey,
			PublicKey:   e.PublicKey,
			Signature:   e.Signature,
			GeneratedAt: time.UnixMilli(e.GeneratedAtUnixMs),
		})
	}
	return out, nil
}

type opkDto struct {
	ID         int32  `json:"id"`
	PrivateKey []byte `json:"priv"`
	PublicKey  []byte `json:"pub"`
	Issued     bool   `json:"issued"`
}

func encodeOpk(opk StoredOneTimePreKey) ([]byte, error) {
	return json.Marshal(opkDto{
		ID:         opk.ID,
		PrivateKey: opk.PrivateKey,
		PublicKey:  opk.PublicKey,
		Issued:     opk.Issued,
	})
}

func decodeOpk(bytes []byte) (StoredOneTimePreKey, error) {
	var dto opkDto
	if err := json.Unmarshal(bytes, &dto); err != nil {
		return StoredOneTimePreKey{}, err
	}
	return StoredOneTimePreKey{
		ID:         dto.ID,
		PrivateKey: dto.PrivateKey,
		PublicKey:  dto.PublicKey,
		Issued:     dto.Issued,
	}, nil
}
