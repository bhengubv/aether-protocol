// SPDX-License-Identifier: MIT

package storage

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

// FileSystemKeyValueStore is a durable IKeyValueStore backed by one file per
// entry in a configurable root directory. Mirrors the C#
// FileSystemKeyValueStore semantics:
//
//   - Writes are atomic on the local filesystem: bytes go to a temp file in
//     the same directory and are then renamed over the target.
//   - Keys are sanitised to the SHA-256 hex of the original key. The original
//     key is recoverable via a sidecar manifest, so arbitrary key strings —
//     including paths, slashes, and Unicode — round-trip safely on every
//     host OS.
//
// This is a simple reference impl, not a database: it doesn't compact,
// doesn't transact across multiple keys, and has no encryption-at-rest.
// Hosts that need any of those wrap the store via EncryptedKeyValueStore or
// supply their own IKeyValueStore implementation.
type FileSystemKeyValueStore struct {
	mu   sync.Mutex
	root string
}

const (
	entrySuffix       = ".kv"
	tempSuffix        = ".tmp"
	keyManifestSuffix = ".key"
)

// NewFileSystemKeyValueStore returns a store rooted at rootDirectory. The
// directory is created if it does not exist. If namespace is non-empty, it
// is appended to the root so multiple stores can share a parent directory
// with disjoint namespaces.
func NewFileSystemKeyValueStore(rootDirectory string, namespace string) (*FileSystemKeyValueStore, error) {
	if rootDirectory == "" {
		return nil, errors.New("storage: rootDirectory cannot be empty")
	}
	root := rootDirectory
	if namespace != "" {
		root = filepath.Join(rootDirectory, namespace)
	}
	if err := os.MkdirAll(root, 0o755); err != nil {
		return nil, fmt.Errorf("storage: create root %q: %w", root, err)
	}
	return &FileSystemKeyValueStore{root: root}, nil
}

func (s *FileSystemKeyValueStore) entryPath(key string) string {
	return filepath.Join(s.root, hashKey(key)+entrySuffix)
}

func (s *FileSystemKeyValueStore) manifestPath(key string) string {
	return s.entryPath(key) + keyManifestSuffix
}

// Get returns the bytes stored under key, or (nil, nil) if absent.
func (s *FileSystemKeyValueStore) Get(ctx context.Context, key string) ([]byte, error) {
	if key == "" {
		return nil, errors.New("storage: key cannot be empty")
	}
	s.mu.Lock()
	defer s.mu.Unlock()

	path := s.entryPath(key)
	bytes, err := os.ReadFile(path)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return nil, nil
		}
		return nil, fmt.Errorf("storage: read %q: %w", path, err)
	}
	return bytes, nil
}

// Put inserts or replaces the bytes stored under key. The write is atomic:
// bytes go to a temp file in the same directory, then the temp file is
// renamed over the target.
func (s *FileSystemKeyValueStore) Put(ctx context.Context, key string, value []byte) error {
	if key == "" {
		return errors.New("storage: key cannot be empty")
	}
	if value == nil {
		return errors.New("storage: value cannot be nil")
	}
	s.mu.Lock()
	defer s.mu.Unlock()

	entry := s.entryPath(key)
	temp := entry + tempSuffix
	if err := os.WriteFile(temp, value, 0o600); err != nil {
		return fmt.Errorf("storage: write temp %q: %w", temp, err)
	}
	if err := os.Rename(temp, entry); err != nil {
		// Best-effort cleanup of the temp file on failure.
		_ = os.Remove(temp)
		return fmt.Errorf("storage: rename %q -> %q: %w", temp, entry, err)
	}

	manifest := s.manifestPath(key)
	if _, err := os.Stat(manifest); err != nil && errors.Is(err, fs.ErrNotExist) {
		if werr := os.WriteFile(manifest, []byte(key), 0o600); werr != nil {
			return fmt.Errorf("storage: write manifest %q: %w", manifest, werr)
		}
	}
	return nil
}

// Remove deletes the entry under key. Returns (true, nil) if a value was
// removed; (false, nil) if there was nothing to remove.
func (s *FileSystemKeyValueStore) Remove(ctx context.Context, key string) (bool, error) {
	if key == "" {
		return false, errors.New("storage: key cannot be empty")
	}
	s.mu.Lock()
	defer s.mu.Unlock()

	entry := s.entryPath(key)
	manifest := s.manifestPath(key)
	if _, err := os.Stat(entry); err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return false, nil
		}
		return false, fmt.Errorf("storage: stat %q: %w", entry, err)
	}
	if err := os.Remove(entry); err != nil {
		return false, fmt.Errorf("storage: remove %q: %w", entry, err)
	}
	// Manifest is best-effort — leftovers are harmless.
	_ = os.Remove(manifest)
	return true, nil
}

// Contains returns true if a value exists under key.
func (s *FileSystemKeyValueStore) Contains(ctx context.Context, key string) (bool, error) {
	if key == "" {
		return false, errors.New("storage: key cannot be empty")
	}
	s.mu.Lock()
	defer s.mu.Unlock()

	if _, err := os.Stat(s.entryPath(key)); err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return false, nil
		}
		return false, fmt.Errorf("storage: stat: %w", err)
	}
	return true, nil
}

// ListKeys returns every key currently in the store. Implementation walks
// the root directory's manifest files (.kv.key) and reads the original key
// from each.
func (s *FileSystemKeyValueStore) ListKeys(ctx context.Context) ([]string, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	entries, err := os.ReadDir(s.root)
	if err != nil {
		if errors.Is(err, fs.ErrNotExist) {
			return nil, nil
		}
		return nil, fmt.Errorf("storage: readdir %q: %w", s.root, err)
	}

	manifestExt := entrySuffix + keyManifestSuffix
	out := make([]string, 0)
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		name := e.Name()
		if !strings.HasSuffix(name, manifestExt) {
			continue
		}
		path := filepath.Join(s.root, name)
		b, rerr := os.ReadFile(path)
		if rerr != nil {
			// Best-effort — skip unreadable manifests.
			continue
		}
		out = append(out, string(b))
	}
	return out, nil
}

// hashKey returns the lowercase SHA-256 hex of the UTF-8 encoding of key.
// Fixed-length, filesystem-safe filename for any input.
func hashKey(key string) string {
	sum := sha256.Sum256([]byte(key))
	return hex.EncodeToString(sum[:])
}
