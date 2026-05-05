// SPDX-License-Identifier: MIT

package dtn

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/thegeeknetwork/aether-protocol-go/constants"
	"github.com/thegeeknetwork/aether-protocol-go/models"
	"github.com/thegeeknetwork/aether-protocol-go/protocol"
)

type unicastRecord struct {
	Packet      *protocol.MeshPacket
	NextHopUhid string
}

const local = "local"

// reuse FakeMeshSender from the routing package via a thin wrapper here.
type fakeSender struct {
	uhid       string
	geohash    string
	peers      []models.PeerInfo
	Unicasts   []unicastRecord
	Broadcasts []*protocol.MeshPacket
}

func newFakeSender(uhid string) *fakeSender { return &fakeSender{uhid: uhid} }

func (f *fakeSender) LocalUhid() string                 { return f.uhid }
func (f *fakeSender) LocalGeohash() string              { return f.geohash }
func (f *fakeSender) ConnectedPeers() []models.PeerInfo { return f.peers }
func (f *fakeSender) AddPeer(p models.PeerInfo)         { f.peers = append(f.peers, p) }

func (f *fakeSender) Send(ctx context.Context, packet *protocol.MeshPacket, nextHopUhid string) (bool, error) {
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.Unicasts = append(f.Unicasts, unicastRecord{Packet: &c, NextHopUhid: nextHopUhid})
	return true, nil
}

func (f *fakeSender) Broadcast(ctx context.Context, packet *protocol.MeshPacket) (int, error) {
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.Broadcasts = append(f.Broadcasts, &c)
	return len(f.peers), nil
}

func newDtnSvc(t *testing.T) (*Service, *fakeSender, BundleStore) {
	t.Helper()
	sender := newFakeSender(local)
	store := NewInMemoryBundleStore()
	svc := NewService(sender, store, nil, nil, nil)
	return svc, sender, store
}

// ─── CreateBundle ──────────────────────────────────────────

func TestCreateBundle_PersistsAndAttemptsDelivery(t *testing.T) {
	svc, _, store := newDtnSvc(t)

	bundle, err := svc.CreateBundle(context.Background(), "recipient", []byte{1, 2, 3}, models.DtnPriorityNormal, "")
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	if bundle == nil || bundle.RecipientUhid != "recipient" {
		t.Fatalf("unexpected bundle: %+v", bundle)
	}
	if bundle.Status != models.DtnStatusPending {
		t.Fatalf("expected pending, got %v", bundle.Status)
	}
	active, _ := store.GetActive(context.Background())
	if len(active) != 1 {
		t.Fatalf("expected 1 active bundle, got %d", len(active))
	}
}

func TestCreateBundle_WithDirectPeer_DeliversImmediately(t *testing.T) {
	svc, sender, _ := newDtnSvc(t)
	sender.AddPeer(models.PeerInfo{
		UHID:         "recipient",
		Capabilities: models.CapabilityDtnCarrier,
	})

	bundle, err := svc.CreateBundle(context.Background(), "recipient", []byte{1, 2, 3}, models.DtnPriorityNormal, "")
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	if bundle.Status != models.DtnStatusDelivered {
		t.Fatalf("expected delivered, got %v", bundle.Status)
	}

	hit := false
	for _, u := range sender.Unicasts {
		if u.NextHopUhid == "recipient" && u.Packet.Type == protocol.DtnBundle {
			hit = true
			break
		}
	}
	if !hit {
		t.Fatalf("expected unicast to recipient")
	}
}

// ─── HandleAsync — DtnBundle ───────────────────────────────

func TestHandle_AsRecipient_MarksDeliveredAndSendsReceipt(t *testing.T) {
	svc, sender, store := newDtnSvc(t)
	bundle := &models.DtnBundle{
		ID:               uuid.NewString(),
		SenderUhid:       "alice",
		RecipientUhid:    local,
		EncryptedPayload: []byte{9},
		Priority:         models.DtnPriorityNormal,
		Status:           models.DtnStatusPending,
		CopyCount:        1,
		MaxCopies:        constants.DtnMaxCopies,
		CreatedAt:        time.Now(),
		ExpiresAt:        time.Now().Add(72 * time.Hour),
	}
	pkt := buildBundlePacket(t, "alice", bundle)
	if err := svc.Handle(context.Background(), pkt); err != nil {
		t.Fatalf("handle: %v", err)
	}
	stored, _ := store.Get(context.Background(), bundle.ID)
	if stored == nil || stored.Status != models.DtnStatusDelivered {
		t.Fatalf("expected delivered, got %+v", stored)
	}
	hit := false
	for _, u := range sender.Unicasts {
		if u.Packet.Type == protocol.DtnDeliveryReceipt && u.NextHopUhid == "alice" {
			hit = true
			break
		}
	}
	if !hit {
		t.Fatalf("expected delivery receipt sent back to alice")
	}
}

func TestHandle_NotRecipientWithCapacity_AcceptsCustody(t *testing.T) {
	svc, sender, store := newDtnSvc(t)
	bundle := &models.DtnBundle{
		ID:               uuid.NewString(),
		SenderUhid:       "alice",
		RecipientUhid:    "bob",
		EncryptedPayload: []byte{1},
		Priority:         models.DtnPriorityNormal,
		Status:           models.DtnStatusPending,
		CopyCount:        1,
		MaxCopies:        constants.DtnMaxCopies,
		CreatedAt:        time.Now(),
		ExpiresAt:        time.Now().Add(72 * time.Hour),
	}
	pkt := buildBundlePacket(t, "alice", bundle)

	if err := svc.Handle(context.Background(), pkt); err != nil {
		t.Fatalf("handle: %v", err)
	}
	stored, _ := store.Get(context.Background(), bundle.ID)
	if stored == nil || stored.Status != models.DtnStatusInCustody {
		t.Fatalf("expected in-custody, got %+v", stored)
	}
	if stored.HopCount != 1 {
		t.Fatalf("expected hop_count=1, got %d", stored.HopCount)
	}
	hit := false
	for _, u := range sender.Unicasts {
		if u.Packet.Type == protocol.DtnCustodyAck && u.NextHopUhid == "alice" {
			hit = true
			break
		}
	}
	if !hit {
		t.Fatalf("expected custody-ack to alice")
	}
}

func TestHandle_AtCapacity_RefusesCustody(t *testing.T) {
	svc, sender, store := newDtnSvc(t)
	for i := int32(0); i < constants.DtnMaxBundlesPerNode; i++ {
		_ = store.Save(context.Background(), &models.DtnBundle{
			ID:            uuid.NewString(),
			SenderUhid:    "x",
			RecipientUhid: "y",
			Status:        models.DtnStatusInCustody,
			ExpiresAt:     time.Now().Add(time.Hour),
		})
	}
	sender.Unicasts = nil

	pkt := buildBundlePacket(t, "alice", &models.DtnBundle{
		ID:            uuid.NewString(),
		SenderUhid:    "alice",
		RecipientUhid: "bob",
		ExpiresAt:     time.Now().Add(time.Hour),
		MaxCopies:     constants.DtnMaxCopies,
	})
	if err := svc.Handle(context.Background(), pkt); err != nil {
		t.Fatalf("handle: %v", err)
	}

	var ack map[string]any
	for _, u := range sender.Unicasts {
		if u.Packet.Type == protocol.DtnCustodyAck {
			_ = json.Unmarshal(u.Packet.Payload, &ack)
			break
		}
	}
	if ack == nil {
		t.Fatalf("expected a custody-ack")
	}
	if ack["accepted"] != false {
		t.Fatalf("expected accepted=false, got %v", ack["accepted"])
	}
}

// ─── DtnCustodyAck ─────────────────────────────────────────

func TestHandle_PositiveCustodyAck_IncrementsCopyCount(t *testing.T) {
	svc, _, store := newDtnSvc(t)
	bundle, _ := svc.CreateBundle(context.Background(), "recipient", []byte{1}, models.DtnPriorityNormal, "")
	initialCopies := bundle.CopyCount // capture before handler may mutate the shared pointer

	body, _ := json.Marshal(map[string]any{"bundle_id": bundle.ID, "accepted": true})
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.DtnCustodyAck
	pkt.SourceUhid = "carrier"
	pkt.DestinationUhid = local
	pkt.Payload = body

	if err := svc.Handle(context.Background(), pkt); err != nil {
		t.Fatalf("handle: %v", err)
	}
	stored, _ := store.Get(context.Background(), bundle.ID)
	if stored.CopyCount != initialCopies+1 {
		t.Fatalf("expected copy_count=%d, got %d", initialCopies+1, stored.CopyCount)
	}
}

func TestHandle_NegativeCustodyAck_DoesNotIncrement(t *testing.T) {
	svc, _, store := newDtnSvc(t)
	bundle, _ := svc.CreateBundle(context.Background(), "recipient", []byte{1}, models.DtnPriorityNormal, "")
	initialCopies := bundle.CopyCount

	body, _ := json.Marshal(map[string]any{"bundle_id": bundle.ID, "accepted": false})
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.DtnCustodyAck
	pkt.SourceUhid = "carrier"
	pkt.DestinationUhid = local
	pkt.Payload = body

	if err := svc.Handle(context.Background(), pkt); err != nil {
		t.Fatalf("handle: %v", err)
	}
	stored, _ := store.Get(context.Background(), bundle.ID)
	if stored.CopyCount != initialCopies {
		t.Fatalf("expected copy_count=%d unchanged, got %d", initialCopies, stored.CopyCount)
	}
}

// ─── DtnDeliveryReceipt ────────────────────────────────────

func TestHandle_DeliveryReceipt_MarksBundleDeliveredAndFiresCallback(t *testing.T) {
	svc, _, store := newDtnSvc(t)
	bundle, _ := svc.CreateBundle(context.Background(), "recipient", []byte{1}, models.DtnPriorityNormal, "")

	var observed *models.DtnDeliveryReceipt
	svc.OnBundleDelivered = func(r *models.DtnDeliveryReceipt) { observed = r }

	receipt := map[string]any{
		"bundle_id":               bundle.ID,
		"recipient_uhid":          "recipient",
		"total_hops":              3,
		"total_custody_transfers": 2,
		"delivered_at_ms":         time.Now().UnixMilli(),
	}
	body, _ := json.Marshal(receipt)
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.DtnDeliveryReceipt
	pkt.SourceUhid = "recipient"
	pkt.DestinationUhid = local
	pkt.Payload = body

	if err := svc.Handle(context.Background(), pkt); err != nil {
		t.Fatalf("handle: %v", err)
	}
	stored, _ := store.Get(context.Background(), bundle.ID)
	if stored.Status != models.DtnStatusDelivered {
		t.Fatalf("expected delivered, got %v", stored.Status)
	}
	if observed == nil {
		t.Fatalf("expected callback fired")
	}
	if observed.TotalHops != 3 {
		t.Fatalf("expected hops=3, got %d", observed.TotalHops)
	}
}

// ─── ExpireStale ───────────────────────────────────────────

func TestExpireStale_FlipsStatusForExpiredBundles(t *testing.T) {
	svc, _, store := newDtnSvc(t)
	expiredID := uuid.NewString()
	freshID := uuid.NewString()
	_ = store.Save(context.Background(), &models.DtnBundle{
		ID: expiredID, SenderUhid: "a", RecipientUhid: "b",
		Status: models.DtnStatusPending,
		ExpiresAt: time.Now().Add(-time.Minute),
	})
	_ = store.Save(context.Background(), &models.DtnBundle{
		ID: freshID, SenderUhid: "a", RecipientUhid: "b",
		Status: models.DtnStatusPending,
		ExpiresAt: time.Now().Add(time.Hour),
	})

	n, err := svc.ExpireStale(context.Background())
	if err != nil {
		t.Fatalf("expire: %v", err)
	}
	if n != 1 {
		t.Fatalf("expected 1 expired, got %d", n)
	}
	fresh, _ := store.Get(context.Background(), freshID)
	if fresh.Status != models.DtnStatusPending {
		t.Fatalf("fresh bundle should remain pending")
	}
}

// ─── helpers ────────────────────────────────────────────────

func buildBundlePacket(t *testing.T, source string, bundle *models.DtnBundle) *protocol.MeshPacket {
	t.Helper()
	wire := map[string]any{
		"id":                       bundle.ID,
		"sender_uhid":              bundle.SenderUhid,
		"recipient_uhid":           bundle.RecipientUhid,
		"encrypted_payload":        bundle.EncryptedPayload,
		"priority":                 int(bundle.Priority),
		"status":                   int(bundle.Status),
		"copy_count":               bundle.CopyCount,
		"max_copies":               bundle.MaxCopies,
		"sender_geohash":           bundle.SenderGeohash,
		"recipient_last_geohash":   bundle.RecipientLastGeohash,
		"hop_count":                bundle.HopCount,
		"created_at_ms":            bundle.CreatedAt.UnixMilli(),
		"expires_at_ms":            bundle.ExpiresAt.UnixMilli(),
	}
	body, _ := json.Marshal(wire)
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.DtnBundle
	pkt.SourceUhid = source
	pkt.DestinationUhid = bundle.RecipientUhid
	pkt.Ttl = constants.DtnTtl
	pkt.Payload = body
	return pkt
}
