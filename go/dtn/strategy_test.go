// SPDX-License-Identifier: MIT

package dtn

import (
	"fmt"
	"testing"

	"github.com/bhengubv/aether-protocol/go/models"
)

// ── helpers ───────────────────────────────────────────────────────────────────

func carrier(uhid string, reliability int32) models.PeerInfo {
	return models.PeerInfo{
		UHID:             uhid,
		Capabilities:     models.CapabilityDtnCarrier,
		ReliabilityScore: reliability,
	}
}

func nonCarrier(uhid string) models.PeerInfo {
	return models.PeerInfo{UHID: uhid}
}

func bundle(sender, recipient string, priority models.DtnPriority, copyCount, maxCopies int32) *models.DtnBundle {
	return &models.DtnBundle{
		SenderUhid:    sender,
		RecipientUhid: recipient,
		Priority:      priority,
		CopyCount:     copyCount,
		MaxCopies:     maxCopies,
	}
}

func bundleWithGeohash(sender string, recipGeo string, copyCount, maxCopies int32) *models.DtnBundle {
	b := bundle(sender, "bob", models.DtnPriorityNormal, copyCount, maxCopies)
	b.RecipientLastGeohash = recipGeo
	return b
}

var strat = GeohashEpidemicStrategy{}

// ── sharedPrefix (unexported — accessible because package is "dtn") ───────────

func TestSharedPrefix_BothEmpty(t *testing.T) {
	if got := sharedPrefix("", ""); got != 0 {
		t.Errorf("sharedPrefix(\"\",\"\") = %d, want 0", got)
	}
}

func TestSharedPrefix_AEmpty(t *testing.T) {
	if got := sharedPrefix("", "gcpv"); got != 0 {
		t.Errorf("sharedPrefix(\"\",\"gcpv\") = %d, want 0", got)
	}
}

func TestSharedPrefix_BEmpty(t *testing.T) {
	if got := sharedPrefix("gcpv", ""); got != 0 {
		t.Errorf("sharedPrefix(\"gcpv\",\"\") = %d, want 0", got)
	}
}

func TestSharedPrefix_NoMatch(t *testing.T) {
	if got := sharedPrefix("abc", "xyz"); got != 0 {
		t.Errorf("sharedPrefix(\"abc\",\"xyz\") = %d, want 0", got)
	}
}

func TestSharedPrefix_PartialMatch(t *testing.T) {
	if got := sharedPrefix("gcpv", "gcAA"); got != 2 {
		t.Errorf("sharedPrefix(\"gcpv\",\"gcAA\") = %d, want 2", got)
	}
}

func TestSharedPrefix_FullMatch(t *testing.T) {
	if got := sharedPrefix("gcpv", "gcpv"); got != 4 {
		t.Errorf("sharedPrefix(\"gcpv\",\"gcpv\") = %d, want 4", got)
	}
}

func TestSharedPrefix_AIsPrefix(t *testing.T) {
	// "gc" is a prefix of "gcpv" — shared = len("gc") = 2
	if got := sharedPrefix("gc", "gcpv"); got != 2 {
		t.Errorf("sharedPrefix(\"gc\",\"gcpv\") = %d, want 2", got)
	}
}

// ── GeohashEpidemicStrategy.SelectTargets ─────────────────────────────────────

func TestSelectTargets_NilBundle_ReturnsNil(t *testing.T) {
	result := strat.SelectTargets(nil, []models.PeerInfo{carrier("p1", 50)}, "")
	if result != nil {
		t.Errorf("expected nil for nil bundle, got %v", result)
	}
}

func TestSelectTargets_SlotsExhausted_ReturnsNil(t *testing.T) {
	b := bundle("alice", "bob", models.DtnPriorityNormal, 3, 3) // copyCount == maxCopies
	result := strat.SelectTargets(b, []models.PeerInfo{carrier("p1", 50)}, "")
	if len(result) != 0 {
		t.Errorf("expected empty when slots exhausted, got %v", result)
	}
}

func TestSelectTargets_SlotsNegative_ReturnsNil(t *testing.T) {
	b := bundle("alice", "bob", models.DtnPriorityNormal, 5, 3) // copyCount > maxCopies
	result := strat.SelectTargets(b, []models.PeerInfo{carrier("p1", 50)}, "")
	if len(result) != 0 {
		t.Errorf("expected empty when slots negative, got %v", result)
	}
}

func TestSelectTargets_EmptyPeerList_ReturnsNil(t *testing.T) {
	b := bundle("alice", "bob", models.DtnPriorityNormal, 0, 3)
	result := strat.SelectTargets(b, []models.PeerInfo{}, "")
	if len(result) != 0 {
		t.Errorf("expected empty for empty peer list, got %v", result)
	}
}

func TestSelectTargets_ExcludesNonDtnCarrier(t *testing.T) {
	b := bundle("alice", "bob", models.DtnPriorityNormal, 0, 3)
	result := strat.SelectTargets(b, []models.PeerInfo{nonCarrier("nc1")}, "")
	if len(result) != 0 {
		t.Errorf("expected empty when peer lacks DTN carrier capability, got %v", result)
	}
}

func TestSelectTargets_ExcludesEmptyUhidPeer(t *testing.T) {
	b := bundle("alice", "bob", models.DtnPriorityNormal, 0, 3)
	empty := models.PeerInfo{UHID: "", Capabilities: models.CapabilityDtnCarrier}
	result := strat.SelectTargets(b, []models.PeerInfo{empty}, "")
	if len(result) != 0 {
		t.Errorf("expected empty for empty-UHID peer, got %v", result)
	}
}

func TestSelectTargets_ExcludesBundleSender(t *testing.T) {
	b := bundle("alice", "bob", models.DtnPriorityNormal, 0, 3)
	senderPeer := carrier("alice", 50) // same UHID as sender
	result := strat.SelectTargets(b, []models.PeerInfo{senderPeer}, "")
	if len(result) != 0 {
		t.Errorf("expected empty when only peer is the sender, got %v", result)
	}
}

// ── SOS priority (DtnPrioritySos) — flood up to slots ─────────────────────────

func TestSelectTargets_SOS_FloodsToAllEligibleUpToSlots(t *testing.T) {
	b := bundle("alice", "bob", models.DtnPrioritySos, 1, 6) // 5 slots
	peers := []models.PeerInfo{
		carrier("p1", 50), carrier("p2", 50), carrier("p3", 50), carrier("p4", 50),
	}
	result := strat.SelectTargets(b, peers, "")
	if len(result) != 4 {
		t.Errorf("SOS: expected all 4 eligible carriers, got %d", len(result))
	}
}

func TestSelectTargets_SOS_RespectsSlotCap(t *testing.T) {
	b := bundle("alice", "bob", models.DtnPrioritySos, 4, 5) // 1 slot left
	peers := []models.PeerInfo{
		carrier("p1", 50), carrier("p2", 50), carrier("p3", 50),
	}
	result := strat.SelectTargets(b, peers, "")
	if len(result) != 1 {
		t.Errorf("SOS slot cap: expected 1, got %d", len(result))
	}
}

// ── Normal priority — reliability fallback (no geohash data) ──────────────────

func TestSelectTargets_ReliabilityFallback_SelectsHighestFirst(t *testing.T) {
	b := bundle("alice", "bob", models.DtnPriorityNormal, 1, 2) // 1 slot
	peers := []models.PeerInfo{
		carrier("low", 20),
		carrier("high", 90),
	}
	result := strat.SelectTargets(b, peers, "")
	if len(result) != 1 {
		t.Fatalf("expected 1 result, got %d", len(result))
	}
	if result[0] != "high" {
		t.Errorf("expected 'high' to be selected first, got %q", result[0])
	}
}

func TestSelectTargets_ReliabilityFallback_RespectsSlotCap(t *testing.T) {
	b := bundle("alice", "bob", models.DtnPriorityNormal, 1, 2) // 1 slot
	peers := make([]models.PeerInfo, 5)
	for i := range peers {
		peers[i] = carrier(fmt.Sprintf("p%d", i+1), 50)
	}
	result := strat.SelectTargets(b, peers, "")
	if len(result) != 1 {
		t.Errorf("reliability fallback slot cap: expected 1, got %d", len(result))
	}
}

func TestSelectTargets_RecipientGeohash_UsesReliabilityFallback(t *testing.T) {
	// In Go, PeerInfo lacks a Geohash field, so even with RecipientLastGeohash set
	// the strategy falls back to reliability ordering.
	b := bundleWithGeohash("alice", "gcpv", 1, 3) // 2 slots
	peers := []models.PeerInfo{
		carrier("low", 10),
		carrier("high", 80),
	}
	result := strat.SelectTargets(b, peers, "gc00")
	if len(result) < 1 {
		t.Fatalf("expected at least 1 result, got 0")
	}
	// high-reliability peer should be first
	if result[0] != "high" {
		t.Errorf("expected 'high' first (reliability sort), got %q", result[0])
	}
}
