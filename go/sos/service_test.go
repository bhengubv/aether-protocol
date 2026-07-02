// SPDX-License-Identifier: MIT

package sos

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/google/uuid"
	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
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

// ─── SosAck ───────────────────────────────────────────────
// Mirrors the C# SosAckTests suite: a receiving node sends a directed ack back to
// the originator; the originator tallies distinct reach and fires OnSosAcknowledged.

// originateSos originates a real SosBroadcast on a separate node and returns the
// broadcast packet plus its alert id.
func originateSos(t *testing.T, originUhid string) (*protocol.MeshPacket, string) {
	t.Helper()
	originSender := newFakeSender(originUhid)
	origin := NewService(originSender, nil, nil)
	ok, err := origin.Broadcast(context.Background(), "medical", "help", -26.20, 28.04, "ke7g")
	if err != nil {
		t.Fatalf("originate broadcast: %v", err)
	}
	if !ok {
		t.Fatalf("originate broadcast returned false")
	}
	if len(originSender.Broadcasts) != 1 {
		t.Fatalf("expected 1 origin broadcast, got %d", len(originSender.Broadcasts))
	}
	return originSender.Broadcasts[0], origin.GetActiveAlerts()[0].ID
}

// makeAck builds a SosAck packet from responderUhid acknowledging broadcastID,
// with a payload serialized exactly like the wire encoder does.
func makeAck(t *testing.T, broadcastID, responderUhid string) *protocol.MeshPacket {
	t.Helper()
	id, err := uuid.Parse(broadcastID)
	if err != nil {
		t.Fatalf("parse broadcast id %q: %v", broadcastID, err)
	}
	body, err := json.Marshal(sosAckWire{BroadcastID: id, ReceivedAtMs: 1_700_000_000_000})
	if err != nil {
		t.Fatalf("marshal ack payload: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.SosAck
	pkt.SourceUhid = responderUhid
	pkt.DestinationUhid = "aether:origin:aa"
	pkt.Payload = body
	return pkt
}

func TestSosAckPayload_SerializesToCanonicalBytes(t *testing.T) {
	cases := []struct {
		name     string
		guid     string
		ms       int64
		expected string
	}{
		{
			name:     "basic",
			guid:     "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f",
			ms:       1700000000000,
			expected: `{"broadcast_id":"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f","received_at_ms":1700000000000}`,
		},
		{
			name:     "zero",
			guid:     "00000000-0000-0000-0000-000000000000",
			ms:       0,
			expected: `{"broadcast_id":"00000000-0000-0000-0000-000000000000","received_at_ms":0}`,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			id, err := uuid.Parse(tc.guid)
			if err != nil {
				t.Fatalf("parse guid: %v", err)
			}
			got, err := json.Marshal(sosAckWire{BroadcastID: id, ReceivedAtMs: tc.ms})
			if err != nil {
				t.Fatalf("marshal: %v", err)
			}
			if string(got) != tc.expected {
				t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", got, tc.expected)
			}
		})
	}
}

func TestHandle_ReceivingSos_SendsDirectedAckToOriginator(t *testing.T) {
	sos, id := originateSos(t, "aether:origin:aa")

	receiver := newFakeSender("aether:receiver:bb")
	svc := NewService(receiver, nil, nil)
	if err := svc.Handle(context.Background(), sos); err != nil {
		t.Fatalf("handle: %v", err)
	}

	if len(receiver.Unicasts) != 1 {
		t.Fatalf("expected exactly 1 directed ack, got %d", len(receiver.Unicasts))
	}
	rec := receiver.Unicasts[0]
	if rec.Packet.Type != protocol.SosAck {
		t.Fatalf("expected SosAck, got %v", rec.Packet.Type)
	}
	if rec.NextHopUhid != "aether:origin:aa" {
		t.Fatalf("expected next hop aether:origin:aa, got %s", rec.NextHopUhid)
	}
	if rec.Packet.DestinationUhid != "aether:origin:aa" {
		t.Fatalf("expected dest aether:origin:aa, got %s", rec.Packet.DestinationUhid)
	}
	if rec.Packet.SourceUhid != "aether:receiver:bb" {
		t.Fatalf("expected source aether:receiver:bb, got %s", rec.Packet.SourceUhid)
	}

	var body sosAckWire
	if err := json.Unmarshal(rec.Packet.Payload, &body); err != nil {
		t.Fatalf("unmarshal ack payload: %v", err)
	}
	if body.BroadcastID.String() != id {
		t.Fatalf("expected broadcast_id %s, got %s", id, body.BroadcastID.String())
	}
}

func TestHandle_OwnSos_DoesNotAck(t *testing.T) {
	local := newFakeSender("aether:origin:aa")
	svc := NewService(local, nil, nil)
	if _, err := svc.Broadcast(context.Background(), "panic", "", 0, 0, ""); err != nil {
		t.Fatalf("broadcast: %v", err)
	}

	// Re-handling our own broadcast must not generate an ack.
	if err := svc.Handle(context.Background(), local.Broadcasts[0]); err != nil {
		t.Fatalf("handle: %v", err)
	}
	if len(local.Unicasts) != 0 {
		t.Fatalf("expected no directed ack for own SOS, got %d", len(local.Unicasts))
	}
}

func TestHandleAck_OnOriginator_RecordsResponderAndRaisesEvent(t *testing.T) {
	originSender := newFakeSender("aether:origin:aa")
	origin := NewService(originSender, nil, nil)
	if _, err := origin.Broadcast(context.Background(), "fire", "north wing", -26.1, 28.0, ""); err != nil {
		t.Fatalf("broadcast: %v", err)
	}
	id := origin.GetActiveAlerts()[0].ID

	var captured *models.SosAcknowledgement
	origin.OnSosAcknowledged = func(a models.SosAcknowledgement) { captured = &a }

	if err := origin.HandleAck(context.Background(), makeAck(t, id, "aether:responder:cc")); err != nil {
		t.Fatalf("handle ack: %v", err)
	}

	if captured == nil {
		t.Fatalf("expected OnSosAcknowledged to fire")
	}
	if captured.BroadcastID != id {
		t.Fatalf("expected broadcast id %s, got %s", id, captured.BroadcastID)
	}
	if captured.ResponderUhid != "aether:responder:cc" {
		t.Fatalf("expected responder aether:responder:cc, got %s", captured.ResponderUhid)
	}
	if captured.TotalAcknowledgements != 1 {
		t.Fatalf("expected total=1, got %d", captured.TotalAcknowledgements)
	}
	if _, ok := origin.GetActiveAlerts()[0].AcknowledgedBy["aether:responder:cc"]; !ok {
		t.Fatalf("expected responder recorded in AcknowledgedBy")
	}
}

func TestHandleAck_DuplicateResponder_CountedOnce(t *testing.T) {
	originSender := newFakeSender("aether:origin:aa")
	origin := NewService(originSender, nil, nil)
	if _, err := origin.Broadcast(context.Background(), "medical", "", 0, 0, ""); err != nil {
		t.Fatalf("broadcast: %v", err)
	}
	id := origin.GetActiveAlerts()[0].ID

	events := 0
	origin.OnSosAcknowledged = func(a models.SosAcknowledgement) { events++ }

	if err := origin.HandleAck(context.Background(), makeAck(t, id, "aether:responder:cc")); err != nil {
		t.Fatalf("handle ack 1: %v", err)
	}
	if err := origin.HandleAck(context.Background(), makeAck(t, id, "aether:responder:cc")); err != nil {
		t.Fatalf("handle ack 2: %v", err)
	}

	if events != 1 {
		t.Fatalf("expected exactly 1 event for duplicate responder, got %d", events)
	}
	if n := len(origin.GetActiveAlerts()[0].AcknowledgedBy); n != 1 {
		t.Fatalf("expected 1 distinct responder, got %d", n)
	}
}

func TestHandleAck_TwoDistinctResponders_CountsTwo(t *testing.T) {
	originSender := newFakeSender("aether:origin:aa")
	origin := NewService(originSender, nil, nil)
	if _, err := origin.Broadcast(context.Background(), "medical", "", 0, 0, ""); err != nil {
		t.Fatalf("broadcast: %v", err)
	}
	id := origin.GetActiveAlerts()[0].ID

	if err := origin.HandleAck(context.Background(), makeAck(t, id, "aether:responder:cc")); err != nil {
		t.Fatalf("handle ack 1: %v", err)
	}
	if err := origin.HandleAck(context.Background(), makeAck(t, id, "aether:responder:dd")); err != nil {
		t.Fatalf("handle ack 2: %v", err)
	}

	if n := len(origin.GetActiveAlerts()[0].AcknowledgedBy); n != 2 {
		t.Fatalf("expected 2 distinct responders, got %d", n)
	}
}

func TestHandleAck_UnknownBroadcast_IsNoOp(t *testing.T) {
	svc, _ := newSosSvc(t)
	raised := false
	svc.OnSosAcknowledged = func(a models.SosAcknowledgement) { raised = true }

	if err := svc.HandleAck(context.Background(), makeAck(t, uuid.NewString(), "aether:responder:cc")); err != nil {
		t.Fatalf("handle ack: %v", err)
	}
	if raised {
		t.Fatalf("expected no event for unknown broadcast")
	}
}

func TestHandleAck_IgnoresSelfResponder(t *testing.T) {
	originSender := newFakeSender("aether:origin:aa")
	origin := NewService(originSender, nil, nil)
	if _, err := origin.Broadcast(context.Background(), "medical", "", 0, 0, ""); err != nil {
		t.Fatalf("broadcast: %v", err)
	}
	id := origin.GetActiveAlerts()[0].ID

	raised := false
	origin.OnSosAcknowledged = func(a models.SosAcknowledgement) { raised = true }

	// An ack whose responder == self (our own ack echoed back) must be ignored.
	if err := origin.HandleAck(context.Background(), makeAck(t, id, "aether:origin:aa")); err != nil {
		t.Fatalf("handle ack: %v", err)
	}
	if raised {
		t.Fatalf("expected self-responder ack to be ignored")
	}
	if n := len(origin.GetActiveAlerts()[0].AcknowledgedBy); n != 0 {
		t.Fatalf("expected no responder recorded for self ack, got %d", n)
	}
}

func TestHandleAck_WrongPacketType_ReturnsError(t *testing.T) {
	svc, _ := newSosSvc(t)
	pkt := makeAck(t, uuid.NewString(), "aether:responder:cc")
	pkt.Type = protocol.Data

	if err := svc.HandleAck(context.Background(), pkt); err == nil {
		t.Fatalf("expected error for non-SosAck packet")
	}
}
