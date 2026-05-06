// SPDX-License-Identifier: MIT

package storage

import (
	"bytes"
	"context"
	"crypto/rand"
	"testing"
)

// TestEncryptedKV_RoundTrip verifies that Get(Put(value)) == value with
// the correct key.
func TestEncryptedKV_RoundTrip(t *testing.T) {
	ctx := context.Background()
	inner := NewInMemoryKeyValueStore()
	key := make([]byte, 32)
	if _, err := rand.Read(key); err != nil {
		t.Fatalf("rand.Read: %v", err)
	}
	provider, err := NewStaticDataAtRestKeyProvider(key)
	if err != nil {
		t.Fatalf("NewStaticDataAtRestKeyProvider: %v", err)
	}
	enc := NewEncryptedKeyValueStore(inner, provider, nil)

	plaintext := []byte("hello, encrypted world")
	if err := enc.Put(ctx, "k1", plaintext); err != nil {
		t.Fatalf("Put: %v", err)
	}

	out, err := enc.Get(ctx, "k1")
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if !bytes.Equal(out, plaintext) {
		t.Errorf("round-trip: got %q want %q", out, plaintext)
	}

	// Inner blob should be different from plaintext (i.e. actually encrypted).
	rawBlob, err := inner.Get(ctx, "k1")
	if err != nil {
		t.Fatalf("inner.Get: %v", err)
	}
	if bytes.Equal(rawBlob, plaintext) {
		t.Errorf("inner blob equals plaintext — encryption did not occur")
	}
	if len(rawBlob) < EncMinimumBlobSize {
		t.Errorf("inner blob length=%d < minimum %d", len(rawBlob), EncMinimumBlobSize)
	}
	if rawBlob[0] != 1 {
		t.Errorf("inner blob version byte=%d, expected 1", rawBlob[0])
	}
}

// TestEncryptedKV_WrongKey_ReturnsNil verifies that decrypting with the
// wrong key returns (nil, nil) rather than raising — mirrors the C# wrapper's
// "treat as not present" semantics.
func TestEncryptedKV_WrongKey_ReturnsNil(t *testing.T) {
	ctx := context.Background()
	inner := NewInMemoryKeyValueStore()

	keyA := make([]byte, 32)
	keyB := make([]byte, 32)
	for i := range keyB {
		keyB[i] = 0xff
	}

	providerA, _ := NewStaticDataAtRestKeyProvider(keyA)
	encA := NewEncryptedKeyValueStore(inner, providerA, nil)
	if err := encA.Put(ctx, "k", []byte("secret")); err != nil {
		t.Fatalf("Put: %v", err)
	}

	providerB, _ := NewStaticDataAtRestKeyProvider(keyB)
	encB := NewEncryptedKeyValueStore(inner, providerB, nil)

	out, err := encB.Get(ctx, "k")
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if out != nil {
		t.Errorf("Get with wrong key: got %v, want nil", out)
	}
}

// TestEncryptedKV_TamperDetection verifies that mutating a single byte of
// the stored ciphertext makes Get return nil (GCM authentication fails).
func TestEncryptedKV_TamperDetection(t *testing.T) {
	ctx := context.Background()
	inner := NewInMemoryKeyValueStore()
	key := make([]byte, 32)
	rand.Read(key)
	provider, _ := NewStaticDataAtRestKeyProvider(key)
	enc := NewEncryptedKeyValueStore(inner, provider, nil)

	if err := enc.Put(ctx, "k", []byte("important")); err != nil {
		t.Fatalf("Put: %v", err)
	}

	// Mutate one ciphertext byte deep in the blob (avoid the version byte
	// at index 0 which is meant to be a routing label, and the 12-byte
	// nonce after that).
	blob, _ := inner.Get(ctx, "k")
	tamperIdx := EncVersionHeaderSize + EncNonceSize + 1
	if tamperIdx >= len(blob) {
		t.Fatalf("blob too short for tamper test: len=%d", len(blob))
	}
	blob[tamperIdx] ^= 0xff
	if err := inner.Put(ctx, "k", blob); err != nil {
		t.Fatalf("inner.Put tampered blob: %v", err)
	}

	out, err := enc.Get(ctx, "k")
	if err != nil {
		t.Fatalf("Get tampered: %v", err)
	}
	if out != nil {
		t.Errorf("Get of tampered blob: got %v, want nil (GCM should reject)", out)
	}
}

// TestEncryptedKV_MalformedShortBlob verifies that a blob shorter than the
// minimum well-formed length is treated as absent.
func TestEncryptedKV_MalformedShortBlob(t *testing.T) {
	ctx := context.Background()
	inner := NewInMemoryKeyValueStore()
	key := make([]byte, 32)
	rand.Read(key)
	provider, _ := NewStaticDataAtRestKeyProvider(key)
	enc := NewEncryptedKeyValueStore(inner, provider, nil)

	// Inject a too-short blob directly.
	if err := inner.Put(ctx, "k", []byte{0x01, 0x02}); err != nil {
		t.Fatalf("inner.Put: %v", err)
	}
	out, err := enc.Get(ctx, "k")
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if out != nil {
		t.Errorf("malformed short blob: got %v, want nil", out)
	}
}

// TestEncryptedKV_KeyVersionRotation verifies the rotation flow: write
// under v1, read with a multi-version provider that has both v1 and v2,
// then write under v2 and confirm both reads still work.
func TestEncryptedKV_KeyVersionRotation(t *testing.T) {
	ctx := context.Background()
	inner := NewInMemoryKeyValueStore()

	keyV1 := make([]byte, 32)
	keyV2 := make([]byte, 32)
	rand.Read(keyV1)
	rand.Read(keyV2)

	// v1 provider writes under version 1.
	provV1, _ := NewStaticDataAtRestKeyProvider(keyV1)
	encV1 := NewEncryptedKeyValueStore(inner, provV1, nil)
	if err := encV1.Put(ctx, "old", []byte("value-old")); err != nil {
		t.Fatalf("Put v1: %v", err)
	}

	// Multi-version provider with both v1 (decrypt only) and v2 (current).
	provBoth, err := NewStaticDataAtRestKeyProviderMulti(map[int][]byte{1: keyV1, 2: keyV2}, 2)
	if err != nil {
		t.Fatalf("multi-version provider: %v", err)
	}
	encBoth := NewEncryptedKeyValueStore(inner, provBoth, nil)

	// Read of the v1 blob still works because v1 key is in the provider.
	out, err := encBoth.Get(ctx, "old")
	if err != nil {
		t.Fatalf("Get during rotation: %v", err)
	}
	if !bytes.Equal(out, []byte("value-old")) {
		t.Errorf("Get during rotation: got %q want %q", out, "value-old")
	}

	// New writes go under v2.
	if err := encBoth.Put(ctx, "new", []byte("value-new")); err != nil {
		t.Fatalf("Put during rotation: %v", err)
	}
	rawNew, _ := inner.Get(ctx, "new")
	if rawNew[0] != 2 {
		t.Errorf("new blob version byte=%d, expected 2", rawNew[0])
	}

	// After rewrap, the v1 blob should be re-encrypted under v2.
	rewrapped, err := encBoth.Rewrap(ctx)
	if err != nil {
		t.Fatalf("Rewrap: %v", err)
	}
	if rewrapped < 1 {
		t.Errorf("Rewrap rewrapped %d, expected >= 1", rewrapped)
	}
	rawOld, _ := inner.Get(ctx, "old")
	if rawOld[0] != 2 {
		t.Errorf("old blob version byte after rewrap=%d, expected 2", rawOld[0])
	}

	// After rotating away from v1 entirely, decrypting old data should fail
	// gracefully (return nil) because the v2-only provider has no v1 key.
	provV2only, _ := NewStaticDataAtRestKeyProviderMulti(map[int][]byte{2: keyV2}, 2)
	encV2 := NewEncryptedKeyValueStore(inner, provV2only, nil)
	out2, err := encV2.Get(ctx, "old")
	if err != nil {
		t.Fatalf("Get on v2-only after rewrap: %v", err)
	}
	// After rewrap the value is on v2 so this read should succeed.
	if !bytes.Equal(out2, []byte("value-old")) {
		t.Errorf("Get on v2-only after rewrap: got %q want %q", out2, "value-old")
	}
}

// TestEncryptedKV_RemoveAndContains verifies the pass-through metadata ops.
func TestEncryptedKV_RemoveAndContains(t *testing.T) {
	ctx := context.Background()
	inner := NewInMemoryKeyValueStore()
	key := make([]byte, 32)
	rand.Read(key)
	provider, _ := NewStaticDataAtRestKeyProvider(key)
	enc := NewEncryptedKeyValueStore(inner, provider, nil)

	if err := enc.Put(ctx, "k", []byte("v")); err != nil {
		t.Fatalf("Put: %v", err)
	}
	has, err := enc.Contains(ctx, "k")
	if err != nil {
		t.Fatalf("Contains: %v", err)
	}
	if !has {
		t.Errorf("Contains after Put: got false, want true")
	}

	removed, err := enc.Remove(ctx, "k")
	if err != nil {
		t.Fatalf("Remove: %v", err)
	}
	if !removed {
		t.Errorf("Remove existing key: got false, want true")
	}

	has, _ = enc.Contains(ctx, "k")
	if has {
		t.Errorf("Contains after Remove: got true, want false")
	}
}
