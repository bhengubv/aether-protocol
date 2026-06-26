// SPDX-License-Identifier: MIT
package market

import (
	"context"
	"math"
	"testing"
)

func TestMarketplaceLifecycle(t *testing.T) {
	ctx := context.Background()
	m := NewInMemoryMarketService()

	var received *MarketListing
	m.OnListingReceived = func(l *MarketListing) { received = l }

	l, err := m.CreateListing(ctx, "seller1", "Bicycle", "Red mountain bike", 1500, "k3vf9z", CategoryGoods)
	if err != nil {
		t.Fatal(err)
	}
	if l.ListingID == "" {
		t.Fatal("listing has no id")
	}
	if received == nil || received.ListingID != l.ListingID {
		t.Fatal("ListingReceived not fired")
	}

	// BrowseNearby: prefix match vs a far geohash.
	near, _ := m.BrowseNearby(ctx, "k3vf9z", 2)
	if len(near) != 1 {
		t.Fatalf("expected 1 nearby, got %d", len(near))
	}
	far, _ := m.BrowseNearby(ctx, "xxxxxx", 2)
	if len(far) != 0 {
		t.Fatalf("expected 0 far, got %d", len(far))
	}

	// Search by text + category filter.
	if res, _ := m.Search(ctx, "bike", nil); len(res) != 1 {
		t.Fatalf("search 'bike' -> %d", len(res))
	}
	svc := CategoryServices
	if res, _ := m.Search(ctx, "bike", &svc); len(res) != 0 {
		t.Fatalf("search 'bike' in Services -> %d", len(res))
	}

	// Trade state machine: Initiated -> BuyerConfirmed -> Complete.
	esc, _ := m.InitiateTrade(ctx, l, "buyer1")
	if esc.State != StateInitiated {
		t.Fatalf("state %d != Initiated", esc.State)
	}
	esc, _ = m.ConfirmTrade(ctx, esc, RoleBuyer)
	if esc.State != StateBuyerConfirmed {
		t.Fatalf("state %d != BuyerConfirmed", esc.State)
	}
	esc, _ = m.ConfirmTrade(ctx, esc, RoleSeller)
	if esc.State != StateComplete {
		t.Fatalf("state %d != Complete", esc.State)
	}

	// Dispute path.
	esc2, _ := m.InitiateTrade(ctx, l, "buyer2")
	if err := m.Dispute(ctx, esc2, "item not as described"); err != nil {
		t.Fatal(err)
	}
	if esc2.State != StateDisputed {
		t.Fatalf("state %d != Disputed", esc2.State)
	}
}

func TestPoVServiceScoreAndDefection(t *testing.T) {
	p, err := NewInMemoryPoVService()
	if err != nil {
		t.Fatal(err)
	}

	tok, _ := p.IssueToken("w1", "A", TransportBle)
	if !p.VerifyToken(tok) {
		t.Fatal("issued token must verify")
	}
	if err := p.AcceptToken(tok); err != nil {
		t.Fatal(err)
	}

	sc := p.GetScore("A")
	if sc.UniqueWitnesses != 1 {
		t.Fatalf("unique witnesses %d != 1", sc.UniqueWitnesses)
	}
	if math.Abs(sc.WeightedScore-0.5) > 1e-9 { // 1/(1+1)
		t.Fatalf("weighted score %v != 0.5", sc.WeightedScore)
	}

	// Tampering invalidates the signatures.
	bad := *tok
	bad.SubjectUhid = "C"
	if p.VerifyToken(&bad) {
		t.Fatal("tampered token must not verify")
	}

	// A node cannot vouch for itself.
	self, _ := p.IssueToken("x", "x", TransportNfc)
	if p.VerifyToken(self) {
		t.Fatal("self-vouch must not verify")
	}

	// Defection penalty: A's score 0.5 -> 0.4.
	p.ReportDefection("A", "victim")
	if got := p.GetScore("A").WeightedScore; math.Abs(got-0.4) > 1e-9 {
		t.Fatalf("post-defection score %v != 0.4", got)
	}
}
