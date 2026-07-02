// SPDX-License-Identifier: MIT

package presence

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/google/uuid"
)

// fakeSender is a routing.MeshSender that records broadcasts and directed sends — no
// transport needed. Mirrors the C# FakeMeshSender in PresenceEridAnnounceTests.cs
// (BroadcastAsync returns 4) and the fakeSender in go/heartbeat.
type fakeSender struct {
	uhid       string
	peers      []models.PeerInfo
	Broadcasts []*protocol.MeshPacket
	Sends      []sendRecord
}

type sendRecord struct {
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
	f.Sends = append(f.Sends, sendRecord{Packet: &c, NextHop: nextHopUhid})
	return true, nil
}

func (f *fakeSender) Broadcast(ctx context.Context, packet *protocol.MeshPacket) (int, error) {
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.Broadcasts = append(f.Broadcasts, &c)
	return 4, nil
}

// ─── Fixture loading ──────────────────────────────────────
// The presence wire vectors are SHARED across every language SDK. Assert against them
// directly so any drift from the canonical bytes fails here.

type presenceVectors struct {
	BeaconVectors []struct {
		Name         string `json:"name"`
		Erid         string `json:"erid"`
		Geohash      string `json:"geohash"`
		Capabilities int32  `json:"capabilities"`
		Status       int32  `json:"status"`
		SentAtMs     int64  `json:"sent_at_ms"`
		ExpectedJSON string `json:"expected_json"`
	} `json:"beacon_vectors"`
	QueryVectors []struct {
		Name         string `json:"name"`
		QueryID      string `json:"query_id"`
		Geohash      string `json:"geohash"`
		ExpectedJSON string `json:"expected_json"`
	} `json:"query_vectors"`
}

func loadPresenceVectors(t *testing.T) presenceVectors {
	t.Helper()
	// go/presence → repo root is two levels up; fixtures live at fixtures/presence.
	path := filepath.Join("..", "..", "fixtures", "presence", "vectors.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read presence vectors (%s): %v", path, err)
	}
	var v presenceVectors
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("unmarshal presence vectors: %v", err)
	}
	return v
}

// ─── Byte-identity ────────────────────────────────────────
// Locks the beacon/query wire encoding to fixtures/presence/vectors.json.

func TestBeacon_SerializesToCanonicalBytes(t *testing.T) {
	vectors := loadPresenceVectors(t)
	if len(vectors.BeaconVectors) != 2 {
		t.Fatalf("expected 2 beacon vectors, got %d", len(vectors.BeaconVectors))
	}
	for _, v := range vectors.BeaconVectors {
		t.Run(v.Name, func(t *testing.T) {
			got, err := json.Marshal(BeaconPayload{
				Erid:         v.Erid,
				Geohash:      v.Geohash,
				Capabilities: v.Capabilities,
				Status:       v.Status,
				SentAtMs:     v.SentAtMs,
			})
			if err != nil {
				t.Fatalf("marshal: %v", err)
			}
			if string(got) != v.ExpectedJSON {
				t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", got, v.ExpectedJSON)
			}
		})
	}
}

func TestQuery_SerializesToCanonicalBytes(t *testing.T) {
	vectors := loadPresenceVectors(t)
	if len(vectors.QueryVectors) != 1 {
		t.Fatalf("expected 1 query vector, got %d", len(vectors.QueryVectors))
	}
	v := vectors.QueryVectors[0]
	got, err := json.Marshal(QueryPayload{QueryID: v.QueryID, Geohash: v.Geohash})
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	if string(got) != v.ExpectedJSON {
		t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", got, v.ExpectedJSON)
	}
}

// ─── Presence behaviour ───────────────────────────────────

func TestBroadcastBeacon_EmitsBeaconPacket_AndHandleRaisesEvent(t *testing.T) {
	sender := newFakeSender("aether:alice:01")
	svc := NewService(sender)
	beacon := BeaconPayload{Erid: "3B38HPPFG9JXE37Q", Geohash: "u4pru", Capabilities: 73, Status: 1, SentAtMs: 1700000000000}

	delivered, err := svc.BroadcastBeacon(context.Background(), beacon)
	if err != nil {
		t.Fatalf("broadcast: %v", err)
	}
	if delivered != 4 {
		t.Fatalf("expected delivered=4, got %d", delivered)
	}
	if len(sender.Broadcasts) != 1 {
		t.Fatalf("expected 1 broadcast, got %d", len(sender.Broadcasts))
	}
	sent := sender.Broadcasts[0]
	if sent.Type != protocol.PresenceBeacon {
		t.Fatalf("expected PresenceBeacon, got %v", sent.Type)
	}

	var got *PresenceBeaconReceivedArgs
	svc.OnBeaconReceived = func(b BeaconPayload, from string) {
		got = &PresenceBeaconReceivedArgs{Beacon: b, FromUhid: from}
	}
	sent.SourceUhid = "aether:alice:01"
	ok, err := svc.Handle(context.Background(), sent)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if got == nil {
		t.Fatalf("expected OnBeaconReceived to fire")
	}
	if got.Beacon.Erid != "3B38HPPFG9JXE37Q" {
		t.Fatalf("expected erid 3B38HPPFG9JXE37Q, got %s", got.Beacon.Erid)
	}
	if got.FromUhid != "aether:alice:01" {
		t.Fatalf("expected fromUhid aether:alice:01, got %s", got.FromUhid)
	}
}

func TestQuery_EmitsQueryPacket_AndHandleRaisesEvent(t *testing.T) {
	sender := newFakeSender("aether:bob:02")
	svc := NewService(sender)

	qid, err := svc.Query(context.Background(), "u4pru")
	if err != nil {
		t.Fatalf("query: %v", err)
	}
	if qid == uuid.Nil {
		t.Fatalf("expected non-nil query id")
	}
	if len(sender.Broadcasts) != 1 {
		t.Fatalf("expected 1 broadcast, got %d", len(sender.Broadcasts))
	}
	sent := sender.Broadcasts[0]
	if sent.Type != protocol.PresenceQuery {
		t.Fatalf("expected PresenceQuery, got %v", sent.Type)
	}
	var body QueryPayload
	if err := json.Unmarshal(sent.Payload, &body); err != nil {
		t.Fatalf("unmarshal query payload: %v", err)
	}
	if body.QueryID != qid.String() {
		t.Fatalf("expected query_id %s, got %s", qid.String(), body.QueryID)
	}
	if body.Geohash != "u4pru" {
		t.Fatalf("expected geohash u4pru, got %s", body.Geohash)
	}

	var got *PresenceQueryReceivedArgs
	svc.OnQueryReceived = func(q QueryPayload, from string) {
		got = &PresenceQueryReceivedArgs{Query: q, FromUhid: from}
	}
	ok, err := svc.Handle(context.Background(), sent)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if got == nil {
		t.Fatalf("expected OnQueryReceived to fire")
	}
	if got.Query.QueryID != qid.String() {
		t.Fatalf("expected query_id %s, got %s", qid.String(), got.Query.QueryID)
	}
}

func TestPresence_Handle_WrongType_ReturnsFalse(t *testing.T) {
	svc := NewService(newFakeSender("aether:local:01"))
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.Data
	pkt.Payload = []byte{}
	ok, err := svc.Handle(context.Background(), pkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for wrong packet type")
	}
}

func TestPresence_Handle_BeaconWithEmptyErid_ReturnsFalse(t *testing.T) {
	svc := NewService(newFakeSender("aether:local:01"))
	body, err := json.Marshal(BeaconPayload{Erid: ""})
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.PresenceBeacon
	pkt.SourceUhid = "aether:x:01"
	pkt.Payload = body
	ok, err := svc.Handle(context.Background(), pkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for beacon with empty erid")
	}
}

// PresenceBeaconReceivedArgs / PresenceQueryReceivedArgs mirror the C# event-args
// records; here they just capture the callback arguments for assertions.
type PresenceBeaconReceivedArgs struct {
	Beacon   BeaconPayload
	FromUhid string
}

type PresenceQueryReceivedArgs struct {
	Query    QueryPayload
	FromUhid string
}
