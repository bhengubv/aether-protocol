// SPDX-License-Identifier: MIT

package heartbeat

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

const local = "aether:local:01"

// fakeSender is a routing.MeshSender that records broadcasts — no transport needed.
// Mirrors the C# FakeMeshSender in HeartbeatTests.cs (BroadcastAsync returns 1).
type fakeSender struct {
	uhid       string
	peers      []models.PeerInfo
	Broadcasts []*protocol.MeshPacket
}

func newFakeSender(uhid string) *fakeSender { return &fakeSender{uhid: uhid} }

func (f *fakeSender) LocalUhid() string                 { return f.uhid }
func (f *fakeSender) LocalGeohash() string              { return "" }
func (f *fakeSender) ConnectedPeers() []models.PeerInfo { return f.peers }

func (f *fakeSender) Send(ctx context.Context, packet *protocol.MeshPacket, nextHopUhid string) (bool, error) {
	return true, nil
}

func (f *fakeSender) Broadcast(ctx context.Context, packet *protocol.MeshPacket) (int, error) {
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.Broadcasts = append(f.Broadcasts, &c)
	return 1, nil
}

// heartbeatFrom builds a Heartbeat packet from source with a payload serialized
// exactly like the wire encoder does. Mirrors HeartbeatFrom in HeartbeatTests.cs.
func heartbeatFrom(t *testing.T, source string, sequence int32, sentAtMs int64) *protocol.MeshPacket {
	t.Helper()
	body, err := json.Marshal(heartbeatWire{Sequence: sequence, SentAtMs: sentAtMs})
	if err != nil {
		t.Fatalf("marshal heartbeat payload: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.Heartbeat
	pkt.SourceUhid = source
	pkt.DestinationUhid = "*"
	pkt.Payload = body
	return pkt
}

// ─── Byte-identity ────────────────────────────────────────
// Locks the HeartbeatPayload wire encoding to fixtures/heartbeat/vectors.json.

func TestHeartbeatPayload_SerializesToCanonicalBytes(t *testing.T) {
	cases := []struct {
		name     string
		sequence int32
		ms       int64
		expected string
	}{
		{
			name:     "basic",
			sequence: 1,
			ms:       1700000000000,
			expected: `{"sequence":1,"sent_at_ms":1700000000000}`,
		},
		{
			name:     "zero",
			sequence: 0,
			ms:       0,
			expected: `{"sequence":0,"sent_at_ms":0}`,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, err := json.Marshal(heartbeatWire{Sequence: tc.sequence, SentAtMs: tc.ms})
			if err != nil {
				t.Fatalf("marshal: %v", err)
			}
			if string(got) != tc.expected {
				t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", got, tc.expected)
			}
		})
	}
}

// ─── SendHeartbeat ────────────────────────────────────────

func TestSend_BroadcastsHeartbeat_WithIncrementingSequence(t *testing.T) {
	sender := newFakeSender(local)
	svc := NewService(sender)

	if _, err := svc.SendHeartbeat(context.Background()); err != nil {
		t.Fatalf("send 1: %v", err)
	}
	if _, err := svc.SendHeartbeat(context.Background()); err != nil {
		t.Fatalf("send 2: %v", err)
	}

	if len(sender.Broadcasts) != 2 {
		t.Fatalf("expected 2 broadcasts, got %d", len(sender.Broadcasts))
	}
	for i, p := range sender.Broadcasts {
		if p.Type != protocol.Heartbeat {
			t.Fatalf("broadcast %d: expected Heartbeat, got %v", i, p.Type)
		}
		if p.Ttl != 1 {
			t.Fatalf("broadcast %d: expected ttl=1, got %d", i, p.Ttl)
		}
	}

	var first, second heartbeatWire
	if err := json.Unmarshal(sender.Broadcasts[0].Payload, &first); err != nil {
		t.Fatalf("unmarshal first: %v", err)
	}
	if err := json.Unmarshal(sender.Broadcasts[1].Payload, &second); err != nil {
		t.Fatalf("unmarshal second: %v", err)
	}
	if first.Sequence != 1 {
		t.Fatalf("expected first sequence=1, got %d", first.Sequence)
	}
	if second.Sequence != 2 {
		t.Fatalf("expected second sequence=2, got %d", second.Sequence)
	}
}

func TestSend_ReturnsDeliveredCount(t *testing.T) {
	svc := NewService(newFakeSender(local))
	delivered, err := svc.SendHeartbeat(context.Background())
	if err != nil {
		t.Fatalf("send: %v", err)
	}
	if delivered != 1 {
		t.Fatalf("expected delivered=1, got %d", delivered)
	}
}

// ─── Handle ───────────────────────────────────────────────

func TestHandle_RecordsPeerAndRaisesEvent(t *testing.T) {
	svc := NewService(newFakeSender(local))
	var seen *PeerLiveness
	svc.OnPeerSeen = func(p PeerLiveness) { seen = &p }

	ok, err := svc.Handle(context.Background(), heartbeatFrom(t, "aether:peer:aa", 7, 1700000000000))
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if seen == nil {
		t.Fatalf("expected OnPeerSeen to fire")
	}
	if seen.Uhid != "aether:peer:aa" {
		t.Fatalf("expected uhid aether:peer:aa, got %s", seen.Uhid)
	}
	if seen.LastSequence != 7 {
		t.Fatalf("expected last sequence 7, got %d", seen.LastSequence)
	}
	if seen.LastSentAtMs != 1700000000000 {
		t.Fatalf("expected last sent_at_ms 1700000000000, got %d", seen.LastSentAtMs)
	}

	known := svc.GetKnownPeers()
	if len(known) != 1 {
		t.Fatalf("expected 1 known peer, got %d", len(known))
	}
	if known[0].Uhid != "aether:peer:aa" {
		t.Fatalf("expected known peer aether:peer:aa, got %s", known[0].Uhid)
	}
}

func TestHandle_RefreshesExistingPeer(t *testing.T) {
	svc := NewService(newFakeSender(local))

	if _, err := svc.Handle(context.Background(), heartbeatFrom(t, "aether:peer:aa", 1, 1000)); err != nil {
		t.Fatalf("handle 1: %v", err)
	}
	if _, err := svc.Handle(context.Background(), heartbeatFrom(t, "aether:peer:aa", 2, 2000)); err != nil {
		t.Fatalf("handle 2: %v", err)
	}

	known := svc.GetKnownPeers()
	if len(known) != 1 {
		t.Fatalf("expected 1 known peer, got %d", len(known))
	}
	if known[0].LastSequence != 2 {
		t.Fatalf("expected last sequence 2, got %d", known[0].LastSequence)
	}
}

func TestHandle_OwnHeartbeat_IsIgnored(t *testing.T) {
	svc := NewService(newFakeSender(local))

	ok, err := svc.Handle(context.Background(), heartbeatFrom(t, local, 1, 1000))
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for self-originated heartbeat")
	}
	if len(svc.GetKnownPeers()) != 0 {
		t.Fatalf("expected no known peers for self heartbeat")
	}
}

func TestHandle_WrongPacketType_ReturnsFalse(t *testing.T) {
	svc := NewService(newFakeSender(local))
	pkt := heartbeatFrom(t, "aether:peer:aa", 1, 1000)
	pkt.Type = protocol.Data

	ok, err := svc.Handle(context.Background(), pkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for wrong packet type")
	}
	if len(svc.GetKnownPeers()) != 0 {
		t.Fatalf("expected no known peers for wrong packet type")
	}
}

func TestHandle_MalformedPayload_ReturnsFalse(t *testing.T) {
	svc := NewService(newFakeSender(local))
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.Heartbeat
	pkt.SourceUhid = "aether:peer:aa"
	pkt.Payload = []byte("{not json")

	ok, err := svc.Handle(context.Background(), pkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for malformed payload")
	}
	if len(svc.GetKnownPeers()) != 0 {
		t.Fatalf("expected no known peers for malformed payload")
	}
}

func TestHandle_NilPacket_ReturnsError(t *testing.T) {
	svc := NewService(newFakeSender(local))
	if _, err := svc.Handle(context.Background(), nil); err == nil {
		t.Fatalf("expected error for nil packet")
	}
}

// ─── GetLivePeers ─────────────────────────────────────────

func TestGetLivePeers_IncludesRecentlySeenPeer(t *testing.T) {
	svc := NewService(newFakeSender(local))
	if _, err := svc.Handle(context.Background(), heartbeatFrom(t, "aether:peer:aa", 1, 1000)); err != nil {
		t.Fatalf("handle: %v", err)
	}

	// A just-received heartbeat is live within any generous window.
	live := svc.GetLivePeers(3600)
	if len(live) != 1 {
		t.Fatalf("expected 1 live peer, got %d", len(live))
	}
	if live[0].Uhid != "aether:peer:aa" {
		t.Fatalf("expected live peer aether:peer:aa, got %s", live[0].Uhid)
	}

	// A negative window pushes the recency horizon into the future, so it excludes even a
	// just-seen peer — a deterministic proof the filter filters (no wall-clock race).
	if n := len(svc.GetLivePeers(-1)); n != 0 {
		t.Fatalf("expected 0 live peers for negative window, got %d", n)
	}
}
