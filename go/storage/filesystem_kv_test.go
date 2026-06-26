// SPDX-License-Identifier: MIT

package storage

import (
	"bytes"
	"context"
	"os"
	"sort"
	"strings"
	"testing"
)

// TestFileSystemKV_PutGetRoundTrip verifies a stored value reads back identically.
func TestFileSystemKV_PutGetRoundTrip(t *testing.T) {
	ctx := context.Background()
	s, err := NewFileSystemKeyValueStore(t.TempDir(), "")
	if err != nil {
		t.Fatalf("NewFileSystemKeyValueStore: %v", err)
	}
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

// TestFileSystemKV_DurableAcrossInstances verifies bytes written by one store
// instance are readable by a fresh instance over the same directory — i.e. the
// data actually persists to disk, not just in memory.
func TestFileSystemKV_DurableAcrossInstances(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()

	writer, err := NewFileSystemKeyValueStore(dir, "")
	if err != nil {
		t.Fatalf("writer: %v", err)
	}
	if err := writer.Put(ctx, "persisted", []byte("on-disk")); err != nil {
		t.Fatalf("Put: %v", err)
	}

	reader, err := NewFileSystemKeyValueStore(dir, "")
	if err != nil {
		t.Fatalf("reader: %v", err)
	}
	got, err := reader.Get(ctx, "persisted")
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if !bytes.Equal(got, []byte("on-disk")) {
		t.Errorf("durable read: got %q want %q", got, "on-disk")
	}
}

// TestFileSystemKV_GetAbsentReturnsNil verifies a missing key reads as (nil, nil).
func TestFileSystemKV_GetAbsentReturnsNil(t *testing.T) {
	ctx := context.Background()
	s, _ := NewFileSystemKeyValueStore(t.TempDir(), "")
	got, err := s.Get(ctx, "missing")
	if err != nil {
		t.Fatalf("Get: %v", err)
	}
	if got != nil {
		t.Errorf("Get absent: got %v want nil", got)
	}
}

// TestFileSystemKV_RemoveAndContains verifies presence + delete semantics, and
// that the atomic write leaves no temp file behind.
func TestFileSystemKV_RemoveAndContains(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	s, _ := NewFileSystemKeyValueStore(dir, "")
	_ = s.Put(ctx, "k", []byte("v"))

	has, _ := s.Contains(ctx, "k")
	if !has {
		t.Errorf("Contains after Put: got false want true")
	}

	// The atomic write (temp + rename) must not leave a .tmp residue.
	entries, _ := os.ReadDir(dir)
	for _, e := range entries {
		if strings.HasSuffix(e.Name(), tempSuffix) {
			t.Errorf("leftover temp file after Put: %s", e.Name())
		}
	}

	removed, _ := s.Remove(ctx, "k")
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

// TestFileSystemKV_ListKeysRecoversOriginalKeys verifies ListKeys returns the
// original (un-hashed) key strings via the sidecar manifest, including keys
// with characters that are not filesystem-safe.
func TestFileSystemKV_ListKeysRecoversOriginalKeys(t *testing.T) {
	ctx := context.Background()
	s, _ := NewFileSystemKeyValueStore(t.TempDir(), "")

	want := []string{"plain", "with/slash", "ünïcode"}
	for _, k := range want {
		if err := s.Put(ctx, k, []byte("x")); err != nil {
			t.Fatalf("Put %q: %v", k, err)
		}
	}
	keys, err := s.ListKeys(ctx)
	if err != nil {
		t.Fatalf("ListKeys: %v", err)
	}
	sort.Strings(keys)
	sort.Strings(want)
	if len(keys) != len(want) {
		t.Fatalf("ListKeys: got %v want %v", keys, want)
	}
	for i := range want {
		if keys[i] != want[i] {
			t.Errorf("ListKeys[%d]: got %q want %q", i, keys[i], want[i])
		}
	}

	// A key with a slash must round-trip its value too (not just its name).
	got, _ := s.Get(ctx, "with/slash")
	if !bytes.Equal(got, []byte("x")) {
		t.Errorf("slash key value: got %q want %q", got, "x")
	}
}

// TestFileSystemKV_NamespaceIsolation verifies two stores sharing a root but
// with different namespaces do not see each other's keys.
func TestFileSystemKV_NamespaceIsolation(t *testing.T) {
	ctx := context.Background()
	root := t.TempDir()

	a, _ := NewFileSystemKeyValueStore(root, "alpha")
	b, _ := NewFileSystemKeyValueStore(root, "beta")

	if err := a.Put(ctx, "shared", []byte("from-alpha")); err != nil {
		t.Fatalf("Put: %v", err)
	}

	has, _ := b.Contains(ctx, "shared")
	if has {
		t.Errorf("namespace leak: beta sees alpha's key")
	}
	got, _ := b.Get(ctx, "shared")
	if got != nil {
		t.Errorf("namespace leak: beta read alpha's value %q", got)
	}
}

// TestFileSystemKV_ReplaceOverwrites verifies a second Put replaces the value.
func TestFileSystemKV_ReplaceOverwrites(t *testing.T) {
	ctx := context.Background()
	s, _ := NewFileSystemKeyValueStore(t.TempDir(), "")
	_ = s.Put(ctx, "k", []byte("first"))
	if err := s.Put(ctx, "k", []byte("second")); err != nil {
		t.Fatalf("Put: %v", err)
	}
	got, _ := s.Get(ctx, "k")
	if !bytes.Equal(got, []byte("second")) {
		t.Errorf("after replace: got %q want %q", got, "second")
	}
}

// TestFileSystemKV_EmptyKeyNilValueAndEmptyRootRejected verifies input validation.
func TestFileSystemKV_EmptyKeyNilValueAndEmptyRootRejected(t *testing.T) {
	ctx := context.Background()
	s, _ := NewFileSystemKeyValueStore(t.TempDir(), "")

	if _, err := s.Get(ctx, ""); err == nil {
		t.Errorf("Get empty key: want error")
	}
	if err := s.Put(ctx, "", []byte("v")); err == nil {
		t.Errorf("Put empty key: want error")
	}
	if err := s.Put(ctx, "k", nil); err == nil {
		t.Errorf("Put nil value: want error")
	}
	if _, err := NewFileSystemKeyValueStore("", ""); err == nil {
		t.Errorf("empty rootDirectory: want error")
	}
}
