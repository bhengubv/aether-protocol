// SPDX-License-Identifier: MIT

package extensibility_test

import (
	"context"
	"testing"

	"github.com/google/uuid"
	"github.com/bhengubv/aether-protocol/go/extensibility"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

// ── helpers ───────────────────────────────────────────────────────────────────

func makePacket(from string) *protocol.MeshPacket {
	return &protocol.MeshPacket{
		ID:              uuid.New(),
		Type:            protocol.Data,
		SourceUhid:      from,
		DestinationUhid: "bob",
		Payload:         []byte("hello aether"),
	}
}

// ── NoopIncentiveProvider ─────────────────────────────────────────────────────

func TestNoopIncentiveProvider_RecordRelay_ReturnsNil(t *testing.T) {
	p := extensibility.NoopIncentiveProvider{}
	err := p.RecordRelay(context.Background(), "alice", makePacket("alice"))
	if err != nil {
		t.Fatalf("expected nil error, got %v", err)
	}
}

func TestNoopIncentiveProvider_RecordRelay_MultipleCalls(t *testing.T) {
	p := extensibility.NoopIncentiveProvider{}
	for i := 0; i < 10; i++ {
		if err := p.RecordRelay(context.Background(), "node", makePacket("node")); err != nil {
			t.Fatalf("call %d returned error: %v", i, err)
		}
	}
}

func TestNoopIncentiveProvider_ShouldPrioritize_ReturnsFalse(t *testing.T) {
	p := extensibility.NoopIncentiveProvider{}
	got := p.ShouldPrioritize(context.Background(), makePacket("alice"))
	if got {
		t.Fatal("expected false, got true")
	}
}

func TestNoopIncentiveProvider_ShouldPrioritize_AlwaysFalse(t *testing.T) {
	p := extensibility.NoopIncentiveProvider{}
	senders := []string{"alice", "bob", "carol", "dave", "eve"}
	for _, s := range senders {
		if p.ShouldPrioritize(context.Background(), makePacket(s)) {
			t.Errorf("expected false for sender %q", s)
		}
	}
}

// Verify NoopIncentiveProvider implements the interface.
var _ extensibility.IncentiveProvider = extensibility.NoopIncentiveProvider{}

// ── NoopBackendClient ─────────────────────────────────────────────────────────

func TestNoopBackendClient_RelayMessage_ReturnsFalse(t *testing.T) {
	c := extensibility.NoopBackendClient{}
	ok, err := c.RelayMessage(context.Background(), "alice", "bob", []byte{1, 2, 3}, 0)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if ok {
		t.Fatal("expected false, got true")
	}
}

func TestNoopBackendClient_RelayMessage_EmptyContent(t *testing.T) {
	c := extensibility.NoopBackendClient{}
	ok, err := c.RelayMessage(context.Background(), "a", "b", []byte{}, 1)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if ok {
		t.Fatal("expected false, got true")
	}
}

func TestNoopBackendClient_RelayMessage_PriorityIndependent(t *testing.T) {
	c := extensibility.NoopBackendClient{}
	for _, pri := range []byte{0, 1, 5, 100, 255} {
		ok, err := c.RelayMessage(context.Background(), "a", "b", []byte{1}, pri)
		if err != nil {
			t.Fatalf("priority=%d: unexpected error: %v", pri, err)
		}
		if ok {
			t.Errorf("priority=%d: expected false, got true", pri)
		}
	}
}

func TestNoopBackendClient_SyncDtnBundle_ReturnsFalse(t *testing.T) {
	c := extensibility.NoopBackendClient{}
	ok, err := c.SyncDtnBundle(context.Background(), []byte(`{"id":"test"}`))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if ok {
		t.Fatal("expected false, got true")
	}
}

func TestNoopBackendClient_SyncSos_ReturnsFalse(t *testing.T) {
	c := extensibility.NoopBackendClient{}
	ok, err := c.SyncSos(context.Background(), []byte(`{"sender_uhid":"alice"}`))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if ok {
		t.Fatal("expected false, got true")
	}
}

// Verify NoopBackendClient implements the interface.
var _ extensibility.BackendClient = extensibility.NoopBackendClient{}

// ── NoopFeatureFlagProvider ───────────────────────────────────────────────────

func TestNoopFeatureFlagProvider_IsEnabled_ReturnsTrue(t *testing.T) {
	f := extensibility.NoopFeatureFlagProvider{}
	if !f.IsEnabled(context.Background(), "any-feature") {
		t.Fatal("expected true, got false")
	}
}

func TestNoopFeatureFlagProvider_IsEnabled_TrueForAllFlags(t *testing.T) {
	f := extensibility.NoopFeatureFlagProvider{}
	flags := []string{
		"rlnc", "dtn", "voice", "video", "watch-together",
		"group-voice", "sos", "", "FEATURE_UNDER_DEVELOPMENT",
	}
	for _, flag := range flags {
		if !f.IsEnabled(context.Background(), flag) {
			t.Errorf("expected true for flag %q, got false", flag)
		}
	}
}

// Verify NoopFeatureFlagProvider implements the interface.
var _ extensibility.FeatureFlagProvider = extensibility.NoopFeatureFlagProvider{}
