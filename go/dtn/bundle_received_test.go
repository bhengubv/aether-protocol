// SPDX-License-Identifier: MIT

package dtn

import (
	"context"
	"testing"
	"time"

	"github.com/google/uuid"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/models"
)

// ─── OnBundleReceived (v1.2.0, Issue #59) ────────────────────────────────────

func TestHandle_InboundBundleAddressedToLocal_FiresOnBundleReceived(t *testing.T) {
	// Construct a Service whose local UHID is "recipient" — bundles addressed
	// to "recipient" must fire OnBundleReceived.
	sender := newFakeSender("recipient")
	store := NewInMemoryBundleStore()
	svc := NewService(sender, store, nil, nil, nil)

	var captured *DtnBundleReceivedEvent
	svc.OnBundleReceived = func(e *DtnBundleReceivedEvent) {
		captured = e
	}

	bundle := &models.DtnBundle{
		ID:               uuid.NewString(),
		SenderUhid:       "remote-sender",
		RecipientUhid:    "recipient", // matches local
		EncryptedPayload: []byte{0x01, 0x02, 0x03, 0x04},
		Priority:         models.DtnPriorityHigh,
		Status:           models.DtnStatusPending,
		CopyCount:        1,
		MaxCopies:        constants.DtnMaxCopies,
		HopCount:         2,
		CreatedAt:        time.Now(),
		ExpiresAt:        time.Now().Add(72 * time.Hour),
	}
	pkt := buildBundlePacket(t, "carrier", bundle)
	if err := svc.Handle(context.Background(), pkt); err != nil {
		t.Fatalf("handle: %v", err)
	}

	if captured == nil {
		t.Fatal("expected OnBundleReceived to fire when bundle is addressed locally")
	}
	if captured.BundleID != bundle.ID {
		t.Errorf("expected bundle_id=%q, got %q", bundle.ID, captured.BundleID)
	}
	if captured.SenderUhid != "remote-sender" {
		t.Errorf("expected sender=remote-sender, got %q", captured.SenderUhid)
	}
	if captured.RecipientUhid != "recipient" {
		t.Errorf("expected recipient=recipient, got %q", captured.RecipientUhid)
	}
	if got := captured.EncryptedPayload; len(got) != 4 || got[0] != 0x01 || got[3] != 0x04 {
		t.Errorf("expected encrypted_payload=[1 2 3 4], got %v", got)
	}
	if captured.Priority != models.DtnPriorityHigh {
		t.Errorf("expected priority=High, got %v", captured.Priority)
	}
	if captured.HopCount != 2 {
		t.Errorf("expected hop_count=2, got %d", captured.HopCount)
	}
	if captured.ReceivedAtUtc.IsZero() {
		t.Error("expected received_at_utc to be set")
	}
}

func TestHandle_InboundBundleForOtherNode_DoesNotFireOnBundleReceived(t *testing.T) {
	// Local UHID is "carrier" — a bundle addressed to "someone-else" should
	// trigger the custody-acceptance path, NOT the local-delivery path, and
	// must NOT fire OnBundleReceived.
	sender := newFakeSender("carrier")
	store := NewInMemoryBundleStore()
	svc := NewService(sender, store, nil, nil, nil)
	sender.AddPeer(models.PeerInfo{UHID: "peer-z"})

	fired := false
	svc.OnBundleReceived = func(e *DtnBundleReceivedEvent) {
		fired = true
	}

	bundle := &models.DtnBundle{
		ID:               uuid.NewString(),
		SenderUhid:       "remote-sender",
		RecipientUhid:    "someone-else", // NOT local
		EncryptedPayload: []byte{0xff},
		Priority:         models.DtnPriorityNormal,
		Status:           models.DtnStatusPending,
		CopyCount:        1,
		MaxCopies:        constants.DtnMaxCopies,
		CreatedAt:        time.Now(),
		ExpiresAt:        time.Now().Add(72 * time.Hour),
	}
	pkt := buildBundlePacket(t, "remote-sender", bundle)
	if err := svc.Handle(context.Background(), pkt); err != nil {
		t.Fatalf("handle: %v", err)
	}

	if fired {
		t.Fatal("OnBundleReceived must fire ONLY when the local node is the final recipient")
	}
}
