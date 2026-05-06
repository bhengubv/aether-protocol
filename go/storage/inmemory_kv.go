// SPDX-License-Identifier: MIT

package storage

import (
	"context"
	"errors"
	"sync"
)

// InMemoryKeyValueStore is a process-local, volatile IKeyValueStore backed
// by a sync.Mutex-protected map. Mirrors the C# InMemoryKeyValueStore.
//
// Suitable for tests and demos. Loses everything on process exit.
type InMemoryKeyValueStore struct {
	mu      sync.Mutex
	entries map[string][]byte
}

// NewInMemoryKeyValueStore returns an empty in-memory store.
func NewInMemoryKeyValueStore() *InMemoryKeyValueStore {
	return &InMemoryKeyValueStore{
		entries: make(map[string][]byte),
	}
}

// Get returns the bytes stored under key, or (nil, nil) if absent.
// The returned slice is a defensive copy so callers cannot mutate stored bytes.
func (s *InMemoryKeyValueStore) Get(ctx context.Context, key string) ([]byte, error) {
	if key == "" {
		return nil, errors.New("storage: key cannot be empty")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	v, ok := s.entries[key]
	if !ok {
		return nil, nil
	}
	out := make([]byte, len(v))
	copy(out, v)
	return out, nil
}

// Put inserts or replaces the bytes stored under key. The store keeps a
// defensive copy so the caller can mutate the original buffer safely.
func (s *InMemoryKeyValueStore) Put(ctx context.Context, key string, value []byte) error {
	if key == "" {
		return errors.New("storage: key cannot be empty")
	}
	if value == nil {
		return errors.New("storage: value cannot be nil")
	}
	cp := make([]byte, len(value))
	copy(cp, value)
	s.mu.Lock()
	defer s.mu.Unlock()
	s.entries[key] = cp
	return nil
}

// Remove deletes the entry under key. Returns (true, nil) if a value was
// removed; (false, nil) if there was nothing to remove.
func (s *InMemoryKeyValueStore) Remove(ctx context.Context, key string) (bool, error) {
	if key == "" {
		return false, errors.New("storage: key cannot be empty")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.entries[key]; !ok {
		return false, nil
	}
	delete(s.entries, key)
	return true, nil
}

// Contains returns true if a value exists under key.
func (s *InMemoryKeyValueStore) Contains(ctx context.Context, key string) (bool, error) {
	if key == "" {
		return false, errors.New("storage: key cannot be empty")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	_, ok := s.entries[key]
	return ok, nil
}

// ListKeys returns every key currently in the store. Order is unspecified.
func (s *InMemoryKeyValueStore) ListKeys(ctx context.Context) ([]string, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]string, 0, len(s.entries))
	for k := range s.entries {
		out = append(out, k)
	}
	return out, nil
}
