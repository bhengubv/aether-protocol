// SPDX-License-Identifier: MIT

// Package storage provides a generic key-value persistence primitive used as
// the foundation for every Aether store that needs to survive a process
// restart. Implementations are responsible for atomicity and durability
// guarantees; the protocol layer just reads and writes opaque bytes.
//
// Two reference implementations ship with this package:
// InMemoryKeyValueStore (volatile, process-local) and
// FileSystemKeyValueStore (one file per key, atomic via temp + rename).
// Hosts that need richer guarantees (transactions, encrypted-at-rest,
// network-attached) supply their own implementation.
package storage

import "context"

// IKeyValueStore is a byte-array-keyed-by-string persistence primitive.
//
// Mirrors Aether.Storage.IKeyValueStore from the C# reference implementation.
// Method names use Go-idiomatic style (no "Async" suffix, context as first
// argument) but the semantic contract is identical:
//
//   - Get returns the bytes stored under the key, or (nil, nil) if absent.
//   - Put inserts or replaces the bytes stored under the key.
//   - Remove deletes the entry under the key, returning (true, nil) if a
//     value was removed and (false, nil) if there was nothing to remove.
//   - Contains returns true if a value exists under the key.
//   - ListKeys enumerates every key currently in the store. Order is
//     implementation-defined.
type IKeyValueStore interface {
	Get(ctx context.Context, key string) ([]byte, error)
	Put(ctx context.Context, key string, value []byte) error
	Remove(ctx context.Context, key string) (bool, error)
	Contains(ctx context.Context, key string) (bool, error)
	ListKeys(ctx context.Context) ([]string, error)
}
