// SPDX-License-Identifier: MIT

package space

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

// repeatByte returns a slice of n copies of b (test helper for signature bytes).
func repeatByte(b byte, n int) []byte {
	out := make([]byte, n)
	for i := range out {
		out[i] = b
	}
	return out
}

// fakeSender is a routing.MeshSender that records broadcasts — no transport
// needed. Mirrors the C# FakeMeshSender in WirePacketsTests.cs (BroadcastAsync
// returns 2).
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
	return 2, nil
}

// spaceVectors mirrors fixtures/space/vectors.json — the canonical cross-language
// parity source generated from the C# reference. Every language port MUST
// reproduce expected_json byte-for-byte.
type spaceVectors struct {
	Vectors []struct {
		Name         string `json:"name"`
		ContentHash  string `json:"content_hash"`
		GeoHash      string `json:"geo_hash"`
		AnchorUhid   string `json:"anchor_uhid"`
		CreatedAtMs  int64  `json:"created_at_ms"`
		TtlHours     int    `json:"ttl_hours"`
		Type         int    `json:"type"`
		Signature    string `json:"signature"`
		ExpectedJSON string `json:"expected_json"`
	} `json:"vectors"`
}

func loadSpaceVectors(t *testing.T) spaceVectors {
	t.Helper()
	path := filepath.Join("..", "..", "fixtures", "space", "vectors.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read fixtures: %v", err)
	}
	var v spaceVectors
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("parse fixtures: %v", err)
	}
	return v
}

// ─── Byte-identity ────────────────────────────────────────
// Locks the BreadcrumbPayload wire encoding to fixtures/space/vectors.json
// (2 vectors: emergency_signed, notice_unsigned).

func TestBreadcrumbPayload_SerializesToCanonicalBytes(t *testing.T) {
	v := loadSpaceVectors(t)
	if len(v.Vectors) != 2 {
		t.Fatalf("expected 2 space vectors, got %d", len(v.Vectors))
	}
	for _, tc := range v.Vectors {
		t.Run(tc.Name, func(t *testing.T) {
			// The fixture carries the signature as its base64 wire form; decode it
			// back to the raw bytes the payload holds so re-marshalling reproduces
			// the fixture exactly.
			sig, err := base64.StdEncoding.DecodeString(tc.Signature)
			if err != nil {
				t.Fatalf("decode signature: %v", err)
			}
			p := BreadcrumbPayload{
				ContentHash: tc.ContentHash,
				GeoHash:     tc.GeoHash,
				AnchorUhid:  tc.AnchorUhid,
				CreatedAtMs: tc.CreatedAtMs,
				TtlHours:    tc.TtlHours,
				Type:        tc.Type,
				Signature:   sig,
			}
			got, err := json.Marshal(p)
			if err != nil {
				t.Fatalf("marshal: %v", err)
			}
			if string(got) != tc.ExpectedJSON {
				t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", got, tc.ExpectedJSON)
			}
		})
	}
}

// ─── Broadcast + Handle ───────────────────────────────────

func TestSpace_Broadcast_EmitsBreadcrumbPacket_AndHandleRaisesEvent(t *testing.T) {
	sender := newFakeSender("aether:alice:01")
	svc := NewWireService(sender)

	crumb := &Breadcrumb{
		ContentHash: "QmX",
		GeoHash:     "u4pruy",
		AnchorUhid:  "aether:alice:01",
		CreatedAt:   time.UnixMilli(1700000000000).UTC(),
		TtlHours:    720,
		Type:        BreadcrumbTypeEmergency,
		Signature:   repeatByte(0x99, 64),
	}
	reached, err := svc.Broadcast(context.Background(), crumb)
	if err != nil {
		t.Fatalf("broadcast: %v", err)
	}
	if reached != 2 {
		t.Fatalf("expected reached=2, got %d", reached)
	}
	if len(sender.Broadcasts) != 1 {
		t.Fatalf("expected 1 broadcast, got %d", len(sender.Broadcasts))
	}
	sent := sender.Broadcasts[0]
	if sent.Type != protocol.SpaceBreadcrumb {
		t.Fatalf("expected SpaceBreadcrumb, got %v", sent.Type)
	}

	var got *Breadcrumb
	svc.OnBreadcrumbReceived = func(b *Breadcrumb) { got = b }
	ok, err := svc.Handle(context.Background(), sent)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if got == nil {
		t.Fatalf("expected OnBreadcrumbReceived to fire")
	}
	if got.GeoHash != "u4pruy" {
		t.Fatalf("expected geo_hash u4pruy, got %s", got.GeoHash)
	}
	if got.Type != BreadcrumbTypeEmergency {
		t.Fatalf("expected type Emergency, got %d", got.Type)
	}
	if got.TtlHours != 720 {
		t.Fatalf("expected ttl 720, got %d", got.TtlHours)
	}
	if len(got.Signature) != 64 {
		t.Fatalf("expected signature length 64, got %d", len(got.Signature))
	}
}

func TestSpace_Handle_WrongType_ReturnsFalse(t *testing.T) {
	svc := NewWireService(newFakeSender("aether:local:01"))
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
