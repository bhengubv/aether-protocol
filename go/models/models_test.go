// SPDX-License-Identifier: MIT

package models_test

import (
	"testing"
	"time"

	"github.com/thegeeknetwork/aether-protocol-go/models"
)

// ── NodeCapabilities ──────────────────────────────────────────────────────────

func TestNodeCapabilities_SingleBits(t *testing.T) {
	caps := []struct {
		name string
		cap  models.NodeCapabilities
		want models.NodeCapabilities
	}{
		{"CapabilityBLE", models.CapabilityBLE, 1},
		{"CapabilityWifiDirect", models.CapabilityWifiDirect, 2},
		{"CapabilityGateway", models.CapabilityGateway, 4},
		{"CapabilityRelay", models.CapabilityRelay, 8},
		{"CapabilitySos", models.CapabilitySos, 16},
		{"CapabilityStreaming", models.CapabilityStreaming, 32},
		{"CapabilityVoice", models.CapabilityVoice, 64},
		{"CapabilityDtnCarrier", models.CapabilityDtnCarrier, 128},
		{"CapabilityNearLink", models.CapabilityNearLink, 256},
		{"CapabilityVideo", models.CapabilityVideo, 512},
	}
	for _, tc := range caps {
		if tc.cap != tc.want {
			t.Errorf("%s: got %d, want %d", tc.name, tc.cap, tc.want)
		}
	}
}

func TestNodeCapabilities_BitwiseCombination(t *testing.T) {
	combined := models.CapabilityBLE | models.CapabilityRelay | models.CapabilitySos
	if combined&models.CapabilityBLE == 0 {
		t.Error("BLE bit should be set")
	}
	if combined&models.CapabilityRelay == 0 {
		t.Error("Relay bit should be set")
	}
	if combined&models.CapabilitySos == 0 {
		t.Error("SOS bit should be set")
	}
	if combined&models.CapabilityGateway != 0 {
		t.Error("Gateway bit should NOT be set")
	}
}

func TestNodeCapabilities_AllSet(t *testing.T) {
	all := models.CapabilityBLE | models.CapabilityWifiDirect | models.CapabilityGateway |
		models.CapabilityRelay | models.CapabilitySos | models.CapabilityStreaming |
		models.CapabilityVoice | models.CapabilityDtnCarrier | models.CapabilityNearLink |
		models.CapabilityVideo
	if all != 1023 {
		t.Errorf("all capabilities OR'd: got %d, want 1023", all)
	}
}

func TestNodeCapabilities_UnderlyingType(t *testing.T) {
	// Must be a uint16, so it can hold all 10 capability bits (≤ 65535)
	var cap models.NodeCapabilities = 1023
	if cap != 1023 {
		t.Error("NodeCapabilities should hold 1023 without overflow")
	}
}

// ── AetherNode ────────────────────────────────────────────────────────────────

func TestAetherNode_FieldAssignment(t *testing.T) {
	now := time.Now().UTC()
	node := models.AetherNode{
		UHID:             "test-uhid-123",
		IdentityKey:      []byte("32-byte-ed25519-identity-key-xxx"),
		Capabilities:     models.CapabilityBLE | models.CapabilityRelay,
		IsLocal:          true,
		LastSeen:         now,
		ReliabilityScore: 85,
	}
	if node.UHID != "test-uhid-123" {
		t.Errorf("UHID: got %q", node.UHID)
	}
	if len(node.IdentityKey) != 32 {
		t.Errorf("IdentityKey length: got %d", len(node.IdentityKey))
	}
	if node.Capabilities&models.CapabilityBLE == 0 {
		t.Error("BLE capability should be set")
	}
	if !node.IsLocal {
		t.Error("IsLocal should be true")
	}
	if node.ReliabilityScore != 85 {
		t.Errorf("ReliabilityScore: got %d", node.ReliabilityScore)
	}
}

func TestAetherNode_ZeroValue(t *testing.T) {
	var node models.AetherNode
	if node.UHID != "" {
		t.Error("zero-value UHID should be empty string")
	}
	if node.IsLocal {
		t.Error("zero-value IsLocal should be false")
	}
	if node.Capabilities != 0 {
		t.Error("zero-value Capabilities should be 0")
	}
}

// ── PeerInfo ──────────────────────────────────────────────────────────────────

func TestPeerInfo_FieldAssignment(t *testing.T) {
	now := time.Now().UTC()
	peer := models.PeerInfo{
		UHID:             "peer-456",
		Addresses:        []string{"192.168.1.1:8080", "ble://aa:bb:cc"},
		Capabilities:     models.CapabilityWifiDirect,
		LastSeen:         now,
		HopCount:         3,
		ReliabilityScore: 70,
	}
	if peer.UHID != "peer-456" {
		t.Errorf("UHID: got %q", peer.UHID)
	}
	if len(peer.Addresses) != 2 {
		t.Errorf("Addresses count: got %d", len(peer.Addresses))
	}
	if peer.HopCount != 3 {
		t.Errorf("HopCount: got %d", peer.HopCount)
	}
}

// ── RouteEntry.IsStale ────────────────────────────────────────────────────────

func TestRouteEntry_IsStale_Expired(t *testing.T) {
	re := models.RouteEntry{
		DestinationUhid: "dest",
		NextHop:         "hop",
		HopCount:        2,
		ExpiresAt:       time.Now().Add(-1 * time.Second), // already expired
		QualityScore:    90,
		SourceUhid:      "src",
	}
	if !re.IsStale() {
		t.Error("route expired 1s ago should be stale")
	}
}

func TestRouteEntry_IsStale_NotYetExpired(t *testing.T) {
	re := models.RouteEntry{
		DestinationUhid: "dest",
		NextHop:         "hop",
		HopCount:        1,
		ExpiresAt:       time.Now().Add(60 * time.Second),
		QualityScore:    100,
		SourceUhid:      "src",
	}
	if re.IsStale() {
		t.Error("route expiring in 60s should not be stale")
	}
}

func TestRouteEntry_IsStale_ExactlyNow(t *testing.T) {
	// A route expiring exactly at time.Now() — behaviour: time.Now().After(expiresAt) ≈ false
	// (this test is inherently racy; just verify no panic)
	re := models.RouteEntry{ExpiresAt: time.Now()}
	_ = re.IsStale() // must not panic
}

func TestRouteEntry_IsStale_FarFuture(t *testing.T) {
	re := models.RouteEntry{ExpiresAt: time.Now().Add(24 * time.Hour)}
	if re.IsStale() {
		t.Error("route expiring tomorrow should not be stale")
	}
}

// ── DtnBundle.IsExpired ───────────────────────────────────────────────────────

func TestDtnBundle_IsExpired_True(t *testing.T) {
	b := models.DtnBundle{
		ID:               "bundle-001",
		SenderUhid:       "alice",
		RecipientUhid:    "bob",
		EncryptedPayload: []byte("data"),
		ExpiresAt:        time.Now().Add(-5 * time.Minute),
	}
	if !b.IsExpired() {
		t.Error("bundle expired 5m ago should be expired")
	}
}

func TestDtnBundle_IsExpired_False(t *testing.T) {
	b := models.DtnBundle{
		ID:               "bundle-002",
		SenderUhid:       "alice",
		RecipientUhid:    "bob",
		EncryptedPayload: []byte("data"),
		ExpiresAt:        time.Now().Add(30 * time.Minute),
	}
	if b.IsExpired() {
		t.Error("bundle expiring in 30m should not be expired")
	}
}

func TestDtnBundle_IsExpired_AllFieldsPreserved(t *testing.T) {
	now := time.Now().UTC()
	b := models.DtnBundle{
		ID:                   "bndl-xyz",
		SenderUhid:           "s",
		RecipientUhid:        "r",
		EncryptedPayload:     []byte{0xAB, 0xCD},
		Priority:             models.DtnPriorityHigh,
		Status:               models.DtnStatusInCustody,
		CopyCount:            3,
		MaxCopies:            5,
		SenderGeohash:        "abcdef",
		RecipientLastGeohash: "ghijkl",
		HopCount:             2,
		CreatedAt:            now,
		ExpiresAt:            now.Add(time.Hour),
	}
	if b.Priority != models.DtnPriorityHigh {
		t.Error("Priority not preserved")
	}
	if b.Status != models.DtnStatusInCustody {
		t.Error("Status not preserved")
	}
	if b.CopyCount != 3 || b.MaxCopies != 5 {
		t.Error("CopyCount/MaxCopies not preserved")
	}
}

// ── DtnPriority constants ─────────────────────────────────────────────────────

func TestDtnPriority_Values(t *testing.T) {
	if models.DtnPriorityLow != 0 {
		t.Errorf("DtnPriorityLow: got %d, want 0", models.DtnPriorityLow)
	}
	if models.DtnPriorityNormal != 1 {
		t.Errorf("DtnPriorityNormal: got %d, want 1", models.DtnPriorityNormal)
	}
	if models.DtnPriorityHigh != 2 {
		t.Errorf("DtnPriorityHigh: got %d, want 2", models.DtnPriorityHigh)
	}
	if models.DtnPrioritySos != 3 {
		t.Errorf("DtnPrioritySos: got %d, want 3", models.DtnPrioritySos)
	}
}

func TestDtnPriority_Ordering(t *testing.T) {
	if !(models.DtnPriorityLow < models.DtnPriorityNormal &&
		models.DtnPriorityNormal < models.DtnPriorityHigh &&
		models.DtnPriorityHigh < models.DtnPrioritySos) {
		t.Error("DtnPriority constants should be in increasing order")
	}
}

// ── DtnStatus constants ───────────────────────────────────────────────────────

func TestDtnStatus_Values(t *testing.T) {
	cases := []struct {
		name string
		got  models.DtnStatus
		want models.DtnStatus
	}{
		{"Pending", models.DtnStatusPending, 0},
		{"InCustody", models.DtnStatusInCustody, 1},
		{"Delivered", models.DtnStatusDelivered, 2},
		{"Expired", models.DtnStatusExpired, 3},
		{"Failed", models.DtnStatusFailed, 4},
	}
	for _, tc := range cases {
		if tc.got != tc.want {
			t.Errorf("DtnStatus%s: got %d, want %d", tc.name, tc.got, tc.want)
		}
	}
}

// ── PresenceStatus constants ──────────────────────────────────────────────────

func TestPresenceStatus_Values(t *testing.T) {
	if models.PresenceOnline != 0 {
		t.Errorf("PresenceOnline: got %d, want 0", models.PresenceOnline)
	}
	if models.PresenceBusy != 1 {
		t.Errorf("PresenceBusy: got %d, want 1", models.PresenceBusy)
	}
	if models.PresenceAway != 2 {
		t.Errorf("PresenceAway: got %d, want 2", models.PresenceAway)
	}
	if models.PresenceOffline != 3 {
		t.Errorf("PresenceOffline: got %d, want 3", models.PresenceOffline)
	}
}

// ── SosAlert ─────────────────────────────────────────────────────────────────

func TestSosAlert_FieldAssignment(t *testing.T) {
	now := time.Now().UTC()
	alert := models.SosAlert{
		ID:            "sos-001",
		SenderUhid:    "alice",
		BroadcastType: "sos",
		Message:       "Need help!",
		Latitude:      -26.2041,
		Longitude:     28.0473,
		Geohash:       "ke7fq5",
		Timestamp:     now,
		ReceivedAt:    now,
	}
	if alert.ID != "sos-001" {
		t.Errorf("ID: got %q", alert.ID)
	}
	if alert.SenderUhid != "alice" {
		t.Errorf("SenderUhid: got %q", alert.SenderUhid)
	}
	if alert.Latitude != -26.2041 {
		t.Errorf("Latitude: got %f", alert.Latitude)
	}
	if alert.Longitude != 28.0473 {
		t.Errorf("Longitude: got %f", alert.Longitude)
	}
	if alert.Message != "Need help!" {
		t.Errorf("Message: got %q", alert.Message)
	}
}

// ── CustodyRecord ─────────────────────────────────────────────────────────────

func TestCustodyRecord_FieldAssignment(t *testing.T) {
	now := time.Now().UTC()
	rec := models.CustodyRecord{
		ID:            "cust-001",
		BundleID:      "bundle-abc",
		FromUhid:      "node-a",
		ToUhid:        "node-b",
		Accepted:      true,
		TransferredAt: now,
	}
	if rec.ID != "cust-001" {
		t.Errorf("ID: got %q", rec.ID)
	}
	if rec.BundleID != "bundle-abc" {
		t.Errorf("BundleID: got %q", rec.BundleID)
	}
	if !rec.Accepted {
		t.Error("Accepted should be true")
	}
	if !rec.TransferredAt.Equal(now) {
		t.Error("TransferredAt not preserved")
	}
}

func TestCustodyRecord_Rejected(t *testing.T) {
	rec := models.CustodyRecord{
		ID:       "cust-002",
		BundleID: "bundle-xyz",
		FromUhid: "node-c",
		ToUhid:   "node-d",
		Accepted: false,
	}
	if rec.Accepted {
		t.Error("Accepted should be false when custody is rejected")
	}
}

// ── DtnDeliveryReceipt ────────────────────────────────────────────────────────

func TestDtnDeliveryReceipt_FieldAssignment(t *testing.T) {
	now := time.Now().UTC()
	receipt := models.DtnDeliveryReceipt{
		BundleID:              "bundle-001",
		RecipientUhid:         "bob",
		TotalHops:             5,
		TotalCustodyTransfers: 3,
		DeliveredAt:           now,
	}
	if receipt.BundleID != "bundle-001" {
		t.Errorf("BundleID: got %q", receipt.BundleID)
	}
	if receipt.RecipientUhid != "bob" {
		t.Errorf("RecipientUhid: got %q", receipt.RecipientUhid)
	}
	if receipt.TotalHops != 5 {
		t.Errorf("TotalHops: got %d", receipt.TotalHops)
	}
	if receipt.TotalCustodyTransfers != 3 {
		t.Errorf("TotalCustodyTransfers: got %d", receipt.TotalCustodyTransfers)
	}
}

// ── PresenceBeacon ────────────────────────────────────────────────────────────

func TestPresenceBeacon_FieldAssignment(t *testing.T) {
	now := time.Now().UTC()
	beacon := models.PresenceBeacon{
		UHID:          "node-xyz",
		Status:        models.PresenceOnline,
		StatusMessage: "Available",
		Timestamp:     now,
		Geohash:       "ke7fq5",
	}
	if beacon.UHID != "node-xyz" {
		t.Errorf("UHID: got %q", beacon.UHID)
	}
	if beacon.Status != models.PresenceOnline {
		t.Errorf("Status: got %d", beacon.Status)
	}
	if beacon.StatusMessage != "Available" {
		t.Errorf("StatusMessage: got %q", beacon.StatusMessage)
	}
}
