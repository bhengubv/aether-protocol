// SPDX-License-Identifier: MIT
package vault

import (
	"bytes"
	"context"
	"testing"
)

func TestInMemoryVaultStoreRecoverRoundTrip(t *testing.T) {
	ctx := context.Background()
	svc := NewInMemoryService()

	data := make([]byte, 3333)
	for i := range data {
		data[i] = byte((i * 7) % 256)
	}

	m, err := svc.Store(ctx, data, "doc.bin")
	if err != nil {
		t.Fatal(err)
	}
	if len(m.ShardHashes) != vaultK+vaultM {
		t.Fatalf("shard count %d != %d", len(m.ShardHashes), vaultK+vaultM)
	}
	if m.SizeBytes != int64(len(data)) {
		t.Fatalf("size %d != %d", m.SizeBytes, len(data))
	}
	if len(m.ContentHash) != 64 {
		t.Fatalf("content hash len %d != 64", len(m.ContentHash))
	}

	got, err := svc.Recover(ctx, m)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(got, data) {
		t.Fatal("recovered bytes differ from original")
	}

	// Health: all shards reachable.
	h := svc.CheckHealth(ctx, m)
	if h.ReachableShards != vaultK+vaultM || !h.IsRecoverable || h.RedundancyScore < 0.99 {
		t.Fatalf("unexpected full health: %+v", h)
	}
}

func TestInMemoryVaultRecoversFromAnyKShards(t *testing.T) {
	ctx := context.Background()
	svc := NewInMemoryService()
	data := []byte("the quick brown fox jumps over the lazy dog, repeatedly and on")

	m, err := svc.Store(ctx, data, "x")
	if err != nil {
		t.Fatal(err)
	}

	// Drop M shards from the store (same-package test reaches the private map).
	for i := 0; i < vaultM; i++ {
		delete(svc.shards, m.ShardHashes[i])
	}
	h := svc.CheckHealth(ctx, m)
	if h.ReachableShards != vaultK || !h.IsRecoverable {
		t.Fatalf("expected K reachable + recoverable, got %+v", h)
	}
	got, err := svc.Recover(ctx, m)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(got, data) {
		t.Fatal("K-shard recovery differs from original")
	}

	// Drop one more — only K-1 remain — unrecoverable.
	delete(svc.shards, m.ShardHashes[vaultM])
	if svc.CheckHealth(ctx, m).IsRecoverable {
		t.Fatal("should be unrecoverable below K")
	}
	if _, err := svc.Recover(ctx, m); err == nil {
		t.Fatal("expected recover error below K")
	}
}

func TestInMemoryVaultEmptyRoundTrip(t *testing.T) {
	ctx := context.Background()
	svc := NewInMemoryService()
	m, err := svc.Store(ctx, nil, "empty")
	if err != nil {
		t.Fatal(err)
	}
	if m.SizeBytes != 0 {
		t.Fatalf("empty size %d != 0", m.SizeBytes)
	}
	got, err := svc.Recover(ctx, m)
	if err != nil {
		t.Fatal(err)
	}
	if len(got) != 0 {
		t.Fatalf("empty recovered length %d != 0", len(got))
	}
}
