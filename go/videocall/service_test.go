// SPDX-License-Identifier: MIT

package videocall

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/google/uuid"
	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

const local = "aether:local:01"

// fakeSender is a routing.MeshSender that records directed sends — no transport
// needed. Mirrors the C# FakeMeshSender in VideoCallControlTests.cs (SendAsync
// captures the packet + next hop and returns true).
type fakeSender struct {
	uhid  string
	peers []models.PeerInfo
	Sends []sentPacket
}

type sentPacket struct {
	Packet  *protocol.MeshPacket
	NextHop string
}

func newFakeSender(uhid string) *fakeSender { return &fakeSender{uhid: uhid} }

func (f *fakeSender) LocalUhid() string                 { return f.uhid }
func (f *fakeSender) LocalGeohash() string              { return "" }
func (f *fakeSender) ConnectedPeers() []models.PeerInfo { return f.peers }

func (f *fakeSender) Send(ctx context.Context, packet *protocol.MeshPacket, nextHopUhid string) (bool, error) {
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.Sends = append(f.Sends, sentPacket{Packet: &c, NextHop: nextHopUhid})
	return true, nil
}

func (f *fakeSender) Broadcast(ctx context.Context, packet *protocol.MeshPacket) (int, error) {
	return 0, nil
}

// controlPacket builds a VideoCall packet with a payload serialized exactly like
// the wire encoder does. Mirrors ControlPacket in VideoCallControlTests.cs.
func controlPacket(t *testing.T, callID uuid.UUID, action, fromUhid string) *protocol.MeshPacket {
	t.Helper()
	body, err := json.Marshal(videoCallControlWire{
		CallID:   callID,
		Action:   action,
		SentAtMs: 1,
	})
	if err != nil {
		t.Fatalf("marshal videocall payload: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VideoCallPkt
	pkt.SourceUhid = fromUhid
	pkt.DestinationUhid = local
	pkt.Payload = body
	return pkt
}

// ─── Byte-identity ────────────────────────────────────────
// Locks the VideoCallControlPayload wire encoding to fixtures/videocall/vectors.json.

func TestVideoCallControlPayload_SerializesToCanonicalBytes(t *testing.T) {
	cases := []struct {
		name     string
		callID   string
		action   string
		sentAtMs int64
		expected string
	}{
		{
			name:     "ring",
			callID:   "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f",
			action:   "ring",
			sentAtMs: 1700000000000,
			expected: `{"call_id":"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f","action":"ring","sent_at_ms":1700000000000}`,
		},
		{
			name:     "hangup",
			callID:   "00000000-0000-0000-0000-000000000000",
			action:   "hangup",
			sentAtMs: 0,
			expected: `{"call_id":"00000000-0000-0000-0000-000000000000","action":"hangup","sent_at_ms":0}`,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			id, err := uuid.Parse(tc.callID)
			if err != nil {
				t.Fatalf("parse call id: %v", err)
			}
			got, err := json.Marshal(videoCallControlWire{
				CallID:   id,
				Action:   tc.action,
				SentAtMs: tc.sentAtMs,
			})
			if err != nil {
				t.Fatalf("marshal: %v", err)
			}
			if string(got) != tc.expected {
				t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", got, tc.expected)
			}
		})
	}
}

// ─── Ring ─────────────────────────────────────────────────

func TestRing_SendsDirectedRingToPeer_AndReturnsCallId(t *testing.T) {
	sender := newFakeSender("aether:alice:01")
	svc := NewService(sender)

	callID, err := svc.Ring(context.Background(), "aether:bob:02")
	if err != nil {
		t.Fatalf("ring: %v", err)
	}
	if callID == uuid.Nil {
		t.Fatalf("expected a non-nil call id")
	}

	if len(sender.Sends) != 1 {
		t.Fatalf("expected 1 directed send, got %d", len(sender.Sends))
	}
	sent := sender.Sends[0]
	if sent.Packet.Type != protocol.VideoCallPkt {
		t.Fatalf("expected VideoCall, got %v", sent.Packet.Type)
	}
	if sent.NextHop != "aether:bob:02" {
		t.Fatalf("expected next hop aether:bob:02, got %s", sent.NextHop)
	}
	if sent.Packet.DestinationUhid != "aether:bob:02" {
		t.Fatalf("expected dest aether:bob:02, got %s", sent.Packet.DestinationUhid)
	}
	if sent.Packet.SourceUhid != "aether:alice:01" {
		t.Fatalf("expected source aether:alice:01, got %s", sent.Packet.SourceUhid)
	}
	if sent.Packet.Ttl != constants.DefaultTtl {
		t.Fatalf("expected ttl=DefaultTtl (%d), got %d", constants.DefaultTtl, sent.Packet.Ttl)
	}

	var body videoCallControlWire
	if err := json.Unmarshal(sent.Packet.Payload, &body); err != nil {
		t.Fatalf("unmarshal payload: %v", err)
	}
	if body.Action != "ring" {
		t.Fatalf("expected action ring, got %s", body.Action)
	}
	if body.CallID != callID {
		t.Fatalf("expected call id %s, got %s", callID, body.CallID)
	}
}

func TestRing_EmptyPeer_ReturnsError(t *testing.T) {
	svc := NewService(newFakeSender(local))
	if _, err := svc.Ring(context.Background(), ""); err == nil {
		t.Fatalf("expected error for empty peer uhid")
	}
}

// ─── Accept / Decline / Hangup ────────────────────────────

func TestRespond_SendsDirectedActionToPeer(t *testing.T) {
	for _, action := range []string{"accept", "decline", "hangup"} {
		t.Run(action, func(t *testing.T) {
			sender := newFakeSender(local)
			svc := NewService(sender)
			callID := uuid.New()

			var (
				ok  bool
				err error
			)
			switch action {
			case "accept":
				ok, err = svc.Accept(context.Background(), callID, "aether:bob:02")
			case "decline":
				ok, err = svc.Decline(context.Background(), callID, "aether:bob:02")
			default:
				ok, err = svc.Hangup(context.Background(), callID, "aether:bob:02")
			}
			if err != nil {
				t.Fatalf("%s: %v", action, err)
			}
			if !ok {
				t.Fatalf("expected %s delivery ok=true", action)
			}

			if len(sender.Sends) != 1 {
				t.Fatalf("expected 1 directed send, got %d", len(sender.Sends))
			}
			sent := sender.Sends[0]
			if sent.NextHop != "aether:bob:02" {
				t.Fatalf("expected next hop aether:bob:02, got %s", sent.NextHop)
			}
			var body videoCallControlWire
			if err := json.Unmarshal(sent.Packet.Payload, &body); err != nil {
				t.Fatalf("unmarshal payload: %v", err)
			}
			if body.Action != action {
				t.Fatalf("expected action %s, got %s", action, body.Action)
			}
			if body.CallID != callID {
				t.Fatalf("expected call id %s, got %s", callID, body.CallID)
			}
		})
	}
}

// ─── Handle ───────────────────────────────────────────────

func TestHandle_RaisesCallStateChanged(t *testing.T) {
	svc := NewService(newFakeSender(local))

	var got *CallStateChanged
	svc.OnCallStateChanged = func(e CallStateChanged) { got = &e }

	callID := uuid.New()
	ok, err := svc.Handle(context.Background(), controlPacket(t, callID, "ring", "aether:bob:02"))
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if got == nil {
		t.Fatalf("expected OnCallStateChanged to fire")
	}
	if got.CallId != callID {
		t.Fatalf("expected call id %s, got %s", callID, got.CallId)
	}
	if got.Action != "ring" {
		t.Fatalf("expected action ring, got %s", got.Action)
	}
	if got.FromUhid != "aether:bob:02" {
		t.Fatalf("expected from aether:bob:02, got %s", got.FromUhid)
	}
}

func TestHandle_WrongPacketType_ReturnsFalse(t *testing.T) {
	svc := NewService(newFakeSender(local))
	pkt := controlPacket(t, uuid.New(), "ring", "aether:bob:02")
	pkt.Type = protocol.Data

	ok, err := svc.Handle(context.Background(), pkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for wrong packet type")
	}
}

func TestHandle_MalformedPayload_ReturnsFalse(t *testing.T) {
	svc := NewService(newFakeSender(local))
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VideoCallPkt
	pkt.SourceUhid = "aether:bob:02"
	pkt.Payload = []byte("{not json")

	ok, err := svc.Handle(context.Background(), pkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for malformed payload")
	}
}

func TestHandle_EmptyAction_ReturnsFalse(t *testing.T) {
	svc := NewService(newFakeSender(local))
	raised := false
	svc.OnCallStateChanged = func(e CallStateChanged) { raised = true }

	ok, err := svc.Handle(context.Background(), controlPacket(t, uuid.New(), "", "aether:bob:02"))
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for empty action")
	}
	if raised {
		t.Fatalf("expected no event for empty action")
	}
}

func TestHandle_NilPacket_ReturnsError(t *testing.T) {
	svc := NewService(newFakeSender(local))
	if _, err := svc.Handle(context.Background(), nil); err == nil {
		t.Fatalf("expected error for nil packet")
	}
}
