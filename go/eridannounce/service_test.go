// SPDX-License-Identifier: MIT

package eridannounce_test

import (
	"bytes"
	"context"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/bhengubv/aether-protocol/go/eridannounce"
	"github.com/bhengubv/aether-protocol/go/identity"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

// fakeSender is a routing.MeshSender that records directed sends and broadcasts — no
// transport needed. Mirrors the C# FakeMeshSender in PresenceEridAnnounceTests.cs and
// the fakeSender in go/heartbeat.
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

// ─── EridAnnounce(56) transport ───────────────────────────

func TestEridAnnounce_Send_EmitsDirectedPacket_AndHandleRaisesEvent(t *testing.T) {
	sender := newFakeSender("aether:alice:01")
	svc := eridannounce.NewService(sender)
	enc := []byte{1, 2, 3, 4, 5} // opaque Signal-encrypted announcement

	ok, err := svc.SendAnnounce(context.Background(), "aether:bob:02", enc)
	if err != nil {
		t.Fatalf("send announce: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if len(sender.Sends) != 1 {
		t.Fatalf("expected 1 directed send, got %d", len(sender.Sends))
	}
	sent := sender.Sends[0]
	if sent.Packet.Type != protocol.EridAnnounce {
		t.Fatalf("expected EridAnnounce, got %v", sent.Packet.Type)
	}
	if sent.NextHop != "aether:bob:02" {
		t.Fatalf("expected next hop aether:bob:02, got %s", sent.NextHop)
	}

	var gotBytes []byte
	var gotFrom string
	svc.OnAnnounceReceived = func(encrypted []byte, from string) {
		gotBytes = encrypted
		gotFrom = from
	}
	sent.Packet.SourceUhid = "aether:bob:02"
	ok, err = svc.Handle(context.Background(), sent.Packet)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if !bytes.Equal(gotBytes, enc) {
		t.Fatalf("expected announcement %v, got %v", enc, gotBytes)
	}
	if gotFrom != "aether:bob:02" {
		t.Fatalf("expected fromUhid aether:bob:02, got %s", gotFrom)
	}
}

func TestEridAnnounce_Handle_WrongTypeOrEmpty_ReturnsFalse(t *testing.T) {
	svc := eridannounce.NewService(newFakeSender("aether:local:01"))

	// Wrong packet type.
	wrong := protocol.NewMeshPacket()
	wrong.Type = protocol.Data
	wrong.Payload = []byte{1}
	ok, err := svc.Handle(context.Background(), wrong)
	if err != nil {
		t.Fatalf("handle wrong type: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for wrong packet type")
	}

	// Right type, empty payload.
	empty := protocol.NewMeshPacket()
	empty.Type = protocol.EridAnnounce
	empty.Payload = []byte{}
	ok, err = svc.Handle(context.Background(), empty)
	if err != nil {
		t.Fatalf("handle empty payload: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for empty payload")
	}
}

// TestEridAnnouncementCodec_MatchesCanonicalFrame re-pins the shared ERID-announcement
// frame byte-identity (existing codec) against fixtures/erid/vectors.json. The directed
// EridAnnounce transport carries this frame (encrypted) opaquely, so re-locking the
// frame here proves the two halves stay in sync. Mirrors the C#
// EridAnnouncementCodec_MatchesCanonicalFrame.
func TestEridAnnouncementCodec_MatchesCanonicalFrame(t *testing.T) {
	routingKeyHex, wantHex := loadEridAnnounceVector(t)
	routingKey, err := hex.DecodeString(routingKeyHex)
	if err != nil {
		t.Fatalf("decode routing key hex: %v", err)
	}
	frame, err := identity.EncodeEridAnnouncement(routingKey, 900, 16)
	if err != nil {
		t.Fatalf("EncodeEridAnnouncement: %v", err)
	}
	got := hex.EncodeToString(frame)
	if got != wantHex {
		t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", got, wantHex)
	}
}

// loadEridAnnounceVector returns (routing_key_hex, announcement_encode_hex) from the
// SHARED fixtures/erid/vectors.json.
func loadEridAnnounceVector(t *testing.T) (routingKeyHex, announcementEncodeHex string) {
	t.Helper()
	path := filepath.Join("..", "..", "fixtures", "erid", "vectors.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read erid vectors (%s): %v", path, err)
	}
	var v struct {
		RoutingKeyHex         string `json:"routing_key_hex"`
		AnnouncementEncodeHex string `json:"announcement_encode_hex"`
	}
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("unmarshal erid vectors: %v", err)
	}
	return v.RoutingKeyHex, v.AnnouncementEncodeHex
}
