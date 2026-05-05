// SPDX-License-Identifier: MIT

package sos

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/google/uuid"
	"github.com/thegeeknetwork/aether-protocol-go/constants"
	"github.com/thegeeknetwork/aether-protocol-go/models"
	"github.com/thegeeknetwork/aether-protocol-go/protocol"
)

const local = "local"

type unicastRecord struct {
	Packet      *protocol.MeshPacket
	NextHopUhid string
}

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

func newSosSvc(t *testing.T) (*Service, *fakeSender) {
	t.Helper()
	sender := newFakeSender(local)
	svc := NewService(sender, nil, nil)
	return svc, sender
}

func newSosPacketFromOther(source string, ttl int32) *protocol.MeshPacket {
	body, _ := json.Marshal(map[string]any{
		"broadcast_id":   uuid.NewString(),
		"broadcast_type": "sos",
		"message":        "help",
		"latitude":       -33.9,
		"longitude":      18.4,
		"geohash":        nil,
	})
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.SosBroadcast
	pkt.SourceUhid = source
	pkt.Ttl = ttl
	pkt.Priority = constants.SosPriority
	pkt.Payload = body
	return pkt
}

// ─── Broadcast ────────────────────────────────────────────

func TestBroadcast_FloodsAndStoresAlert(t *testing.T) {
	svc, sender := newSosSvc(t)

	ok, err := svc.Broadcast(context.Background(), "sos", "help", -33.9, 18.4, "")
	if err != nil {
		t.Fatalf("broadcast: %v", err)
	}
	if !ok {
		t.Fatalf("expected true")
	}
	if len(sender.Broadcasts) != 1 {
		t.Fatalf("expected 1 broadcast, got %d", len(sender.Broadcasts))
	}
	pkt := sender.Broadcasts[0]
	if pkt.Type != protocol.SosBroadcast {
		t.Fatalf("expected SosBroadcast, got %v", pkt.Type)
	}
	if pkt.Ttl != constants.SosTtl {
		t.Fatalf("expected SosTtl, got %d", pkt.Ttl)
	}
	if pkt.Priority != constants.SosPriority {
		t.Fatalf("expected SosPriority, got %d", pkt.Priority)
	}
	if len(svc.GetActiveAlerts()) != 1 {
		t.Fatalf("expected 1 active alert")
	}
}

func TestBroadcast_RateLimitedAfterMax(t *testing.T) {
	svc, _ := newSosSvc(t)
	for i := int32(0); i < constants.MaxSosBroadcastsPerHour; i++ {
		ok, err := svc.Broadcast(context.Background(), "sos", "h", 0, 0, "")
		if err != nil {
			t.Fatalf("loop broadcast: %v", err)
		}
		if !ok {
			t.Fatalf("broadcast %d should succeed", i)
		}
	}

	ok, err := svc.Broadcast(context.Background(), "sos", "h", 0, 0, "")
	if err != nil {
		t.Fatalf("rate-limited broadcast err: %v", err)
	}
	if ok {
		t.Fatalf("expected rate-limited (false)")
	}
}

func TestBroadcast_RejectsEmptyType(t *testing.T) {
	svc, _ := newSosSvc(t)

	ok, err := svc.Broadcast(context.Background(), "", "help", 0, 0, "")
	if err == nil {
		t.Fatalf("expected error for empty broadcast type")
	}
	if ok {
		t.Fatalf("expected ok=false")
	}
}

// ─── Handle ───────────────────────────────────────────────

func TestHandle_DropsDuplicatePacketId(t *testing.T) {
	svc, sender := newSosSvc(t)
	pkt := newSosPacketFromOther("alice", constants.SosTtl)

	if err := svc.Handle(context.Background(), pkt); err != nil {
		t.Fatalf("handle: %v", err)
	}
	sender.Broadcasts = nil
	alertsAfter := len(svc.GetActiveAlerts())

	if err := svc.Handle(context.Background(), pkt); err != nil {
		t.Fatalf("handle 2: %v", err)
	}
	if len(sender.Broadcasts) != 0 {
		t.Fatalf("expected no rebroadcast on dup, got %d", len(sender.Broadcasts))
	}
	if len(svc.GetActiveAlerts()) != alertsAfter {
		t.Fatalf("expected no extra alert on dup")
	}
}

func TestHandle_IgnoresSelfOriginated(t *testing.T) {
	svc, sender := newSosSvc(t)
	pkt := newSosPacketFromOther(local, constants.SosTtl)

	if err := svc.Handle(context.Background(), pkt); err != nil {
		t.Fatalf("handle: %v", err)
	}
	if len(sender.Broadcasts) != 0 {
		t.Fatalf("expected no broadcast for self-source")
	}
}

func TestHandle_RaisesSosReceived(t *testing.T) {
	svc, _ := newSosSvc(t)
	var observed *models.SosAlert
	svc.OnSosReceived = func(a *models.SosAlert) { observed = a }

	pkt := newSosPacketFromOther("alice", constants.SosTtl)
	if err := svc.Handle(context.Background(), pkt); err != nil {
		t.Fatalf("handle: %v", err)
	}
	if observed == nil {
		t.Fatalf("expected callback to fire")
	}
	if observed.SenderUhid != "alice" {
		t.Fatalf("expected sender alice, got %s", observed.SenderUhid)
	}
}

func TestHandle_RebroadcastsWhenTtlAllows(t *testing.T) {
	svc, sender := newSosSvc(t)
	pkt := newSosPacketFromOther("alice", 5)

	if err := svc.Handle(context.Background(), pkt); err != nil {
		t.Fatalf("handle: %v", err)
	}
	if len(sender.Broadcasts) != 1 {
		t.Fatalf("expected 1 broadcast, got %d", len(sender.Broadcasts))
	}
	if sender.Broadcasts[0].Ttl != 4 {
		t.Fatalf("expected ttl=4, got %d", sender.Broadcasts[0].Ttl)
	}
}

func TestHandle_DoesNotRebroadcastWhenTtlExhausted(t *testing.T) {
	svc, sender := newSosSvc(t)
	pkt := newSosPacketFromOther("alice", 1)

	if err := svc.Handle(context.Background(), pkt); err != nil {
		t.Fatalf("handle: %v", err)
	}
	if len(sender.Broadcasts) != 0 {
		t.Fatalf("expected no rebroadcast for ttl=1")
	}
}

func TestHandle_RejectsWrongPacketType(t *testing.T) {
	svc, _ := newSosSvc(t)
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.Data
	pkt.SourceUhid = "alice"

	err := svc.Handle(context.Background(), pkt)
	if err == nil {
		t.Fatalf("expected error for non-SOS packet")
	}
}

// ─── Resolve ──────────────────────────────────────────────

func TestResolve_RemovesAlertAndFiresCallback(t *testing.T) {
	svc, _ := newSosSvc(t)
	var resolved string
	svc.OnSosResolved = func(id string) { resolved = id }

	_, err := svc.Broadcast(context.Background(), "sos", "h", 0, 0, "")
	if err != nil {
		t.Fatalf("broadcast: %v", err)
	}
	alerts := svc.GetActiveAlerts()
	if len(alerts) != 1 {
		t.Fatalf("expected 1 alert")
	}
	id := alerts[0].ID

	svc.Resolve(context.Background(), id)

	if len(svc.GetActiveAlerts()) != 0 {
		t.Fatalf("expected alert removed")
	}
	if resolved != id {
		t.Fatalf("expected callback with id=%s, got %s", id, resolved)
	}
}

func TestResolve_UnknownIdIsNoOp(t *testing.T) {
	svc, _ := newSosSvc(t)
	called := false
	svc.OnSosResolved = func(id string) { called = true }

	svc.Resolve(context.Background(), uuid.NewString())

	if called {
		t.Fatalf("expected callback not to fire for unknown id")
	}
}
