// SPDX-License-Identifier: MIT

// In-memory aether-vault service (Phase-2 extension): erasure-coded distributed
// backup over the file-level ReedSolomon codec in this package. Port of the C#
// reference (AetherNet.Vault.InMemoryVaultService) — K=10 / M=4, shard layout
// byte-identical so a shard set produced here is decodable by any other node.
package vault

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"sync"
	"time"
)

const (
	vaultK = 10
	vaultM = 4
)

// Manifest is the only thing the owner must retain to reconstruct a vaulted file.
type Manifest struct {
	ContentHash string    // SHA-256 hex of the plaintext
	ShardHashes []string  // SHA-256 hex of each of the K+M shards
	K           int       // data shards (default 10)
	M           int       // parity shards (default 4)
	SizeBytes   int64     // original plaintext size
	Label       string
	CreatedAt   time.Time
}

// TotalShards returns K + M.
func (m *Manifest) TotalShards() int { return m.K + m.M }

// Health is a current reachability report for a vaulted file.
type Health struct {
	TotalShards     int
	ReachableShards int
	IsRecoverable   bool
	RedundancyScore float64
}

// Service is the aether-vault erasure-coded backup store.
type Service interface {
	// Store shards and persists data; returns the manifest the owner must keep.
	Store(ctx context.Context, data []byte, label string) (*Manifest, error)
	// Recover reconstructs the original file from any K available shards.
	Recover(ctx context.Context, manifest *Manifest) ([]byte, error)
	// CheckHealth reports how many shards are reachable and whether recovery is possible.
	CheckHealth(ctx context.Context, manifest *Manifest) Health
	// Replicate re-replicates shards (no-op in the in-memory implementation).
	Replicate(ctx context.Context, manifest *Manifest, targetRedundancy int) error
}

// InMemoryService is an in-memory Service for testing / single-node use; shards
// live in a hash-keyed map and are lost on restart.
type InMemoryService struct {
	mu     sync.Mutex
	shards map[string][]byte // shard content hash -> bytes
}

// NewInMemoryService constructs an empty in-memory vault service.
func NewInMemoryService() *InMemoryService {
	return &InMemoryService{shards: make(map[string][]byte)}
}

func sha256Hex(b []byte) string {
	h := sha256.Sum256(b)
	return hex.EncodeToString(h[:])
}

// Store implements Service.
func (s *InMemoryService) Store(ctx context.Context, data []byte, label string) (*Manifest, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	contentHash := sha256Hex(data)
	codec, err := NewReedSolomonCodec(vaultK, vaultM)
	if err != nil {
		return nil, err
	}

	var shards [][]byte
	if len(data) == 0 {
		// Empty file: K zero-padded 1-byte data shards (mirrors the C# shardSize = 1 case).
		ds := make([][]byte, vaultK)
		for i := range ds {
			ds[i] = make([]byte, 1)
		}
		shards, err = codec.Encode(ds)
	} else {
		shards, err = codec.EncodeData(data)
	}
	if err != nil {
		return nil, err
	}

	shardHashes := make([]string, len(shards))
	s.mu.Lock()
	for i, sh := range shards {
		h := sha256Hex(sh)
		shardHashes[i] = h
		s.shards[h] = sh
	}
	s.mu.Unlock()

	return &Manifest{
		ContentHash: contentHash,
		ShardHashes: shardHashes,
		K:           vaultK,
		M:           vaultM,
		SizeBytes:   int64(len(data)),
		Label:       label,
		CreatedAt:   time.Now().UTC(),
	}, nil
}

// Recover implements Service.
func (s *InMemoryService) Recover(ctx context.Context, manifest *Manifest) ([]byte, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	total := len(manifest.ShardHashes)
	k := manifest.K
	m := total - k
	codec, err := NewReedSolomonCodec(k, m)
	if err != nil {
		return nil, err
	}

	available := make(map[int][]byte)
	s.mu.Lock()
	for i, h := range manifest.ShardHashes {
		if sh, ok := s.shards[h]; ok {
			available[i] = sh
		}
	}
	s.mu.Unlock()

	if len(available) < k {
		return nil, fmt.Errorf("vault: cannot recover — only %d/%d shards available", len(available), k)
	}
	return codec.ReconstructData(available, int(manifest.SizeBytes))
}

// CheckHealth implements Service.
func (s *InMemoryService) CheckHealth(ctx context.Context, manifest *Manifest) Health {
	reachable := 0
	s.mu.Lock()
	for _, h := range manifest.ShardHashes {
		if _, ok := s.shards[h]; ok {
			reachable++
		}
	}
	s.mu.Unlock()

	total := manifest.TotalShards()
	score := 0.0
	if total > 0 {
		score = float64(reachable) / float64(total)
	}
	return Health{
		TotalShards:     total,
		ReachableShards: reachable,
		IsRecoverable:   reachable >= manifest.K,
		RedundancyScore: score,
	}
}

// Replicate implements Service (no-op in the in-memory implementation).
func (s *InMemoryService) Replicate(ctx context.Context, manifest *Manifest, targetRedundancy int) error {
	return nil
}
