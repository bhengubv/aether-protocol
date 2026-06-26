// SPDX-License-Identifier: MIT

package storage

import (
	"bytes"
	"context"
	"sort"
	"testing"
)

// TestInMemoryKV_PutGetRoundTrip verifies a stored value reads back identically.
func TestInMemoryKV_PutGetRoundTrip(t *testing.T) {
	ctx := context.Background()
	s := NewInMemoryKeyValueStore()

	if err := s.Put(ctx, "k", []byte("value")); err != nil {
		t.Fatalf("Put: %v", err)
	}
	got, err := s.Get(ctx, "k")
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if !bytes.Equal(got, []byte("value")) {
		t.Errorf("Get: got %q want %q", got, "value")
	}
}

// TestInMemoryKV_GetAbsentReturnsNil verifies a missing key reads as (nil, nil).
func TestInMemoryKV_GetAbsentReturnsNil(t *testing.T) {
	ctx := context.Background()
	s := NewInMemoryKeyValueStore()
	got, err := s.Get(ctx, "missing")
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if got != nil {
		t.Errorf("Get absent: got %v want nil", got)
	}
}

// TestInMemoryKV_PutReplaces verifies a second Put overwrites the first.
func TestInMemoryKV_PutReplaces(t *testing.T) {
	ctx := context.Background()
	s := NewInMemoryKeyValueStore()
	_ = s.Put(ctx, "k", []byte("first"))
	if err := s.Put(ctx, "k", []byte("second")); err != nil {
		t.Fatalf("Put: %v", err)
	}
	got, _ := s.Get(ctx, "k")
	if !bytes.Equal(got, []byte("second")) {
		t.Errorf("after replace: got %q want %q", got, "second")
	}
}

// TestInMemoryKV_RemoveAndContains verifies presence tracking and delete semantics.
func TestInMemoryKV_RemoveAndContains(t *testing.T) {
	ctx := context.Background()
	s := NewInMemoryKeyValueStore()
	_ = s.Put(ctx, "k", []byte("v"))

	has, err := s.Contains(ctx, "k")
	if err != nil {
		t.Fatalf("Contains: %v", err)
	}
	if !has {
		t.Errorf("Contains after Put: got false want true")
	}

	removed, err := s.Remove(ctx, "k")
	if err != nil {
		t.Fatalf("Remove: %v", err)
	}
	if !removed {
		t.Errorf("Remove existing: got false want true")
	}

	removed, _ = s.Remove(ctx, "k")
	if removed {
		t.Errorf("Remove absent: got true want false")
	}

	has, _ = s.Contains(ctx, "k")
	if has {
		t.Errorf("Contains after Remove: got true want false")
	}
}

// TestInMemoryKV_ListKeys verifies every stored key is enumerated.
func TestInMemoryKV_ListKeys(t *testing.T) {
	ctx := context.Background()
	s := NewInMemoryKeyValueStore()
	for _, k := range []string{"a", "b", "c"} {
		_ = s.Put(ctx, k, []byte(k))
	}
	keys, err := s.ListKeys(ctx)
	if err != nil {
		t.Fatalf("ListKeys: %v", err)
	}
	sort.Strings(keys)
	want := []string{"a", "b", "c"}
	if len(keys) != len(want) {
		t.Fatalf("ListKeys: got %v want %v", keys, want)
	}
	for i := range want {
		if keys[i] != want[i] {
			t.Errorf("ListKeys[%d]: got %q want %q", i, keys[i], want[i])
		}
	}
}

// TestInMemoryKV_DefensiveCopy verifies the store neither aliases the caller's
// input buffer nor hands back a slice that lets callers mutate stored bytes.
func TestInMemoryKV_DefensiveCopy(t *testing.T) {
	ctx := context.Background()
	s := NewInMemoryKeyValueStore()

	in := []byte("original")
	_ = s.Put(ctx, "k", in)
	in[0] = 'X' // mutate the caller's buffer after Put

	got, _ := s.Get(ctx, "k")
	if !bytes.Equal(got, []byte("original")) {
		t.Errorf("store aliased input buffer: got %q want %q", got, "original")
	}

	got[0] = 'Y' // mutate the returned slice
	again, _ := s.Get(ctx, "k")
	if !bytes.Equal(again, []byte("original")) {
		t.Errorf("store returned an aliased slice: got %q want %q", again, "original")
	}
}

// TestInMemoryKV_EmptyKeyAndNilValueRejected verifies input validation.
func TestInMemoryKV_EmptyKeyAndNilValueRejected(t *testing.T) {
	ctx := context.Background()
	s := NewInMemoryKeyValueStore()

	if _, err := s.Get(ctx, ""); err == nil {
		t.Errorf("Get empty key: want error, got nil")
	}
	if err := s.Put(ctx, "", []byte("v")); err == nil {
		t.Errorf("Put empty key: want error, got nil")
	}
	if err := s.Put(ctx, "k", nil); err == nil {
		t.Errorf("Put nil value: want error, got nil")
	}
	if _, err := s.Remove(ctx, ""); err == nil {
		t.Errorf("Remove empty key: want error, got nil")
	}
	if _, err := s.Contains(ctx, ""); err == nil {
		t.Errorf("Contains empty key: want error, got nil")
	}
}
