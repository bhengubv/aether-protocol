// SPDX-License-Identifier: MIT

package storage

import (
	"context"
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"errors"
	"fmt"
	"log"
)

// Constants for the encrypted blob format. Mirror the C#
// EncryptedKeyValueStore wire layout exactly.
const (
	// EncKeySize is the AES-256 key length in bytes.
	EncKeySize = 32

	// EncNonceSize is the AES-GCM nonce length in bytes.
	EncNonceSize = 12

	// EncTagSize is the AES-GCM authentication tag length in bytes.
	EncTagSize = 16

	// EncVersionHeaderSize is the length of the version-byte header at the
	// start of every blob.
	EncVersionHeaderSize = 1

	// EncMinimumBlobSize is the minimum byte count for any well-formed
	// encrypted blob (version + nonce + tag).
	EncMinimumBlobSize = EncVersionHeaderSize + EncNonceSize + EncTagSize
)

// Logger is the minimum logging interface used by EncryptedKeyValueStore.
// The standard library *log.Logger satisfies this. Pass nil to disable
// logging.
type Logger interface {
	Printf(format string, v ...interface{})
}

// EncryptedKeyValueStore is a transparent encryption-at-rest wrapper for an
// arbitrary IKeyValueStore. Encrypts every value on the way down and
// decrypts on the way up using AES-256-GCM with a per-write random nonce.
// Keys are passed through unchanged so list/range queries continue to work.
//
// Threat model: protects persisted bytes from an attacker who recovers the
// underlying medium (stolen disk, recycled SD card, leaked backup) without
// compromising the master-key material that the host hands to the
// IDataAtRestKeyProvider. The wrapper does NOT hide write patterns, key
// names, or value sizes. It does NOT defend against in-process memory
// disclosure — values are plaintext while the application holds them.
//
// Wire format (per stored blob):
//
//	keyVersion (1 byte) || nonce (12 bytes) || ciphertext (N bytes) || tag (16 bytes)
//
// The keyVersion byte names which key in the provider was used; the wrapper
// looks it up on read, so hosts can run a rotation window with both old and
// new keys loaded. Tampering with any byte fails GCM authentication and the
// read returns nil (treated as "not present" by callers).
//
// Composition: existing adapters consume any IKeyValueStore, so wrapping is
// a one-line composition:
//
//	inner := storage.NewInMemoryKeyValueStore()
//	provider, _ := storage.NewStaticDataAtRestKeyProvider(myKey)
//	secure := storage.NewEncryptedKeyValueStore(inner, provider, nil)
type EncryptedKeyValueStore struct {
	inner       IKeyValueStore
	keyProvider IDataAtRestKeyProvider
	logger      Logger
}

// NewEncryptedKeyValueStore wraps inner with transparent AES-256-GCM
// encryption. Pass nil for logger to disable warnings; otherwise the
// wrapper logs tamper events and missing-key-version events at warning
// level via Printf.
func NewEncryptedKeyValueStore(inner IKeyValueStore, keyProvider IDataAtRestKeyProvider, logger Logger) *EncryptedKeyValueStore {
	if inner == nil {
		panic("storage: inner cannot be nil")
	}
	if keyProvider == nil {
		panic("storage: keyProvider cannot be nil")
	}
	if logger == nil {
		logger = nopLogger{}
	}
	return &EncryptedKeyValueStore{inner: inner, keyProvider: keyProvider, logger: logger}
}

// Get reads the encrypted blob from the inner store, validates the format,
// and decrypts. Returns (nil, nil) for any of: absent key, malformed blob,
// unknown key version, GCM authentication failure. Tamper events are logged
// at warning level.
func (s *EncryptedKeyValueStore) Get(ctx context.Context, key string) ([]byte, error) {
	if key == "" {
		return nil, errors.New("storage: key cannot be empty")
	}
	blob, err := s.inner.Get(ctx, key)
	if err != nil {
		return nil, err
	}
	if blob == nil {
		return nil, nil
	}
	if len(blob) < EncMinimumBlobSize {
		s.logger.Printf("EncryptedKeyValueStore: blob under key=%q is %d bytes < minimum %d — treating as tampered/missing.",
			key, len(blob), EncMinimumBlobSize)
		return nil, nil
	}

	version := int(blob[0])
	keyBytes := s.keyProvider.GetKey(version)
	if keyBytes == nil {
		s.logger.Printf("EncryptedKeyValueStore: no data-at-rest key registered for version=%d under key=%q — cannot decrypt.",
			version, key)
		return nil, nil
	}
	if len(keyBytes) != EncKeySize {
		s.logger.Printf("EncryptedKeyValueStore: provider returned %d-byte key for version=%d (need %d).",
			len(keyBytes), version, EncKeySize)
		return nil, nil
	}

	nonce := blob[EncVersionHeaderSize : EncVersionHeaderSize+EncNonceSize]
	// Go's AES-GCM Open expects ciphertext+tag concatenated; that's exactly
	// the layout from EncVersionHeaderSize+EncNonceSize through the end.
	ctAndTag := blob[EncVersionHeaderSize+EncNonceSize:]

	block, err := aes.NewCipher(keyBytes)
	if err != nil {
		return nil, fmt.Errorf("storage: aes.NewCipher: %w", err)
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return nil, fmt.Errorf("storage: cipher.NewGCM: %w", err)
	}
	plaintext, err := aead.Open(nil, nonce, ctAndTag, nil)
	if err != nil {
		// GCM authentication failed: caller treats the value as absent
		// rather than raising — mirrors the C# EncryptedKeyValueStore.
		s.logger.Printf("EncryptedKeyValueStore: AES-GCM authentication failed reading key=%q (version=%d): %v. "+
			"Either the wrong key is configured or the blob has been tampered with.",
			key, version, err)
		return nil, nil
	}
	return plaintext, nil
}

// Put encrypts value under the provider's CurrentVersion and writes the
// versioned blob to the inner store.
func (s *EncryptedKeyValueStore) Put(ctx context.Context, key string, value []byte) error {
	if key == "" {
		return errors.New("storage: key cannot be empty")
	}
	if value == nil {
		return errors.New("storage: value cannot be nil")
	}

	version := s.keyProvider.CurrentVersion()
	if version < 1 || version > 255 {
		return fmt.Errorf("storage: keyProvider.CurrentVersion=%d outside [1, 255]", version)
	}
	keyBytes := s.keyProvider.GetKey(version)
	if keyBytes == nil {
		return fmt.Errorf("storage: keyProvider returned nil for its own CurrentVersion=%d", version)
	}
	if len(keyBytes) != EncKeySize {
		return fmt.Errorf("storage: keyProvider returned %d-byte key for CurrentVersion=%d (need %d)",
			len(keyBytes), version, EncKeySize)
	}

	nonce := make([]byte, EncNonceSize)
	if _, err := rand.Read(nonce); err != nil {
		return fmt.Errorf("storage: rand.Read nonce: %w", err)
	}

	block, err := aes.NewCipher(keyBytes)
	if err != nil {
		return fmt.Errorf("storage: aes.NewCipher: %w", err)
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return fmt.Errorf("storage: cipher.NewGCM: %w", err)
	}
	// Go's AES-GCM Seal returns ciphertext || tag in a single buffer.
	ctAndTag := aead.Seal(nil, nonce, value, nil)

	blob := make([]byte, 0, EncVersionHeaderSize+EncNonceSize+len(ctAndTag))
	blob = append(blob, byte(version))
	blob = append(blob, nonce...)
	blob = append(blob, ctAndTag...)

	return s.inner.Put(ctx, key, blob)
}

// Remove deletes the entry under key from the inner store.
func (s *EncryptedKeyValueStore) Remove(ctx context.Context, key string) (bool, error) {
	if key == "" {
		return false, errors.New("storage: key cannot be empty")
	}
	return s.inner.Remove(ctx, key)
}

// Contains returns whether the inner store has an entry under key.
func (s *EncryptedKeyValueStore) Contains(ctx context.Context, key string) (bool, error) {
	if key == "" {
		return false, errors.New("storage: key cannot be empty")
	}
	return s.inner.Contains(ctx, key)
}

// ListKeys forwards directly to the inner store; keys are stored in the
// clear (only values are encrypted).
func (s *EncryptedKeyValueStore) ListKeys(ctx context.Context) ([]string, error) {
	return s.inner.ListKeys(ctx)
}

// Rewrap re-encrypts every value in the underlying store under the
// provider's current key version. Use during a key-rotation window after
// the provider has been swapped out for one that holds both the old and
// new keys — values written under the old version stay readable, and after
// the rewrap completes every blob is on the new version so the host can
// retire the old key on the next deploy. Returns the number of values
// successfully rewrapped.
func (s *EncryptedKeyValueStore) Rewrap(ctx context.Context) (int, error) {
	keys, err := s.inner.ListKeys(ctx)
	if err != nil {
		return 0, err
	}
	rewrapped := 0
	for _, k := range keys {
		if err := ctx.Err(); err != nil {
			return rewrapped, err
		}
		plaintext, gerr := s.Get(ctx, k)
		if gerr != nil {
			return rewrapped, gerr
		}
		if plaintext == nil {
			s.logger.Printf("EncryptedKeyValueStore: skipping rewrap of key=%q — value could not be decrypted under any registered key version.", k)
			continue
		}
		if perr := s.Put(ctx, k, plaintext); perr != nil {
			return rewrapped, perr
		}
		rewrapped++
	}
	return rewrapped, nil
}

// nopLogger satisfies Logger and discards everything.
type nopLogger struct{}

func (nopLogger) Printf(format string, v ...interface{}) {}

// StdLogger adapts *log.Logger to the Logger interface (sugar).
type StdLogger struct{ L *log.Logger }

// Printf delegates to the wrapped *log.Logger.
func (s StdLogger) Printf(format string, v ...interface{}) {
	if s.L != nil {
		s.L.Printf(format, v...)
	}
}
