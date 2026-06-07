// SPDX-License-Identifier: MIT

package extensibility_test

import (
	"context"
	"testing"

	"github.com/bhengubv/aether-protocol/go/extensibility"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

// ─── RecordCreatorTip (v1.2.0, Issue #61) ────────────────────────────────────

func TestNoopIncentiveProvider_RecordCreatorTip_ReturnsNilAndDoesNotPanic(t *testing.T) {
	p := extensibility.NoopIncentiveProvider{}
	if err := p.RecordCreatorTip(context.Background(), "creator-uhid", 5.00, "deadbeef"); err != nil {
		t.Fatalf("expected nil error from no-op, got %v", err)
	}
}

func TestNoopIncentiveProvider_RecordCreatorTip_MultipleCalls(t *testing.T) {
	p := extensibility.NoopIncentiveProvider{}
	for i := 0; i < 10; i++ {
		if err := p.RecordCreatorTip(context.Background(), "creator", 1.23, "hash"); err != nil {
			t.Fatalf("call %d returned error: %v", i, err)
		}
	}
}

// ── capturingIncentiveProvider — verifies custom impls receive args verbatim ──

type capturingIncentiveProvider struct {
	Tips   []capturedTip
	Relays []capturedRelay
}

type capturedTip struct {
	Creator     string
	Amount      float64
	ContentHash string
}

type capturedRelay struct {
	Node   string
	Packet *protocol.MeshPacket
}

func (c *capturingIncentiveProvider) RecordRelay(ctx context.Context, localUhid string, packet *protocol.MeshPacket) error {
	c.Relays = append(c.Relays, capturedRelay{Node: localUhid, Packet: packet})
	return nil
}

func (c *capturingIncentiveProvider) ShouldPrioritize(ctx context.Context, packet *protocol.MeshPacket) bool {
	return false
}

func (c *capturingIncentiveProvider) RecordCreatorTip(ctx context.Context, creatorUhid string, amount float64, contentHash string) error {
	c.Tips = append(c.Tips, capturedTip{
		Creator:     creatorUhid,
		Amount:      amount,
		ContentHash: contentHash,
	})
	return nil
}

// Verify the capturing provider implements the full interface.
var _ extensibility.IncentiveProvider = (*capturingIncentiveProvider)(nil)

func TestCustomIncentiveProvider_RecordCreatorTip_ReceivesArgumentsVerbatim(t *testing.T) {
	c := &capturingIncentiveProvider{}
	var provider extensibility.IncentiveProvider = c

	if err := provider.RecordCreatorTip(context.Background(), "creator-zulu", 12.50, "rootHash-abc"); err != nil {
		t.Fatalf("RecordCreatorTip: %v", err)
	}

	if len(c.Tips) != 1 {
		t.Fatalf("expected 1 tip captured, got %d", len(c.Tips))
	}
	tip := c.Tips[0]
	if tip.Creator != "creator-zulu" {
		t.Errorf("expected creator=creator-zulu, got %q", tip.Creator)
	}
	if tip.Amount != 12.50 {
		t.Errorf("expected amount=12.50, got %v", tip.Amount)
	}
	if tip.ContentHash != "rootHash-abc" {
		t.Errorf("expected content_hash=rootHash-abc, got %q", tip.ContentHash)
	}
}

func TestCustomIncentiveProvider_TipAndRelayAreIndependentRecordingPaths(t *testing.T) {
	c := &capturingIncentiveProvider{}
	var provider extensibility.IncentiveProvider = c

	if err := provider.RecordCreatorTip(context.Background(), "author", 1.00, "h1"); err != nil {
		t.Fatalf("RecordCreatorTip: %v", err)
	}
	pkt := &protocol.MeshPacket{Type: protocol.Data}
	if err := provider.RecordRelay(context.Background(), "node-uhid", pkt); err != nil {
		t.Fatalf("RecordRelay: %v", err)
	}

	// Both recorded separately; the relay path doesn't pollute the tip stream
	// and vice versa.
	if len(c.Tips) != 1 {
		t.Errorf("expected 1 tip, got %d", len(c.Tips))
	}
	if len(c.Relays) != 1 {
		t.Errorf("expected 1 relay, got %d", len(c.Relays))
	}
}
