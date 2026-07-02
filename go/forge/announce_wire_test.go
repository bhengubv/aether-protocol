// SPDX-License-Identifier: MIT

package forge

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

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

// forgeVectors mirrors fixtures/forge/vectors.json — the canonical cross-language
// parity source generated from the C# reference. Every language port MUST
// reproduce expected_json byte-for-byte.
type forgeVectors struct {
	Vectors []struct {
		Name          string `json:"name"`
		PackageID     string `json:"package_id"`
		ContentHash   string `json:"content_hash"`
		SizeBytes     int64  `json:"size_bytes"`
		AnnouncedAtMs int64  `json:"announced_at_ms"`
		ExpectedJSON  string `json:"expected_json"`
	} `json:"vectors"`
}

func loadForgeVectors(t *testing.T) forgeVectors {
	t.Helper()
	path := filepath.Join("..", "..", "fixtures", "forge", "vectors.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read fixtures: %v", err)
	}
	var v forgeVectors
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("parse fixtures: %v", err)
	}
	return v
}

// ─── Byte-identity ────────────────────────────────────────
// Locks the AnnouncePayload wire encoding to fixtures/forge/vectors.json.

func TestAnnouncePayload_SerializesToCanonicalBytes(t *testing.T) {
	v := loadForgeVectors(t)
	if len(v.Vectors) == 0 {
		t.Fatalf("expected at least one forge vector")
	}
	for _, tc := range v.Vectors {
		t.Run(tc.Name, func(t *testing.T) {
			p := AnnouncePayload{
				PackageID:     tc.PackageID,
				ContentHash:   tc.ContentHash,
				SizeBytes:     tc.SizeBytes,
				AnnouncedAtMs: tc.AnnouncedAtMs,
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

func TestForge_Broadcast_EmitsAnnouncePacket_AndHandleRaisesEvent(t *testing.T) {
	sender := newFakeSender("aether:alice:01")
	svc := NewWireService(sender)

	reached, err := svc.Broadcast(context.Background(), "npm:react@18.2.0", "QmForgeHash456", 294912, 1700000000000)
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
	if sent.Type != protocol.ForgeAnnounce {
		t.Fatalf("expected ForgeAnnounce, got %v", sent.Type)
	}

	var got *AnnouncePayload
	svc.OnAnnounceReceived = func(a AnnouncePayload) { got = &a }
	ok, err := svc.Handle(context.Background(), sent)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if got == nil {
		t.Fatalf("expected OnAnnounceReceived to fire")
	}
	if got.PackageID != "npm:react@18.2.0" {
		t.Fatalf("expected package_id npm:react@18.2.0, got %s", got.PackageID)
	}
	if got.SizeBytes != 294912 {
		t.Fatalf("expected size_bytes 294912, got %d", got.SizeBytes)
	}
}

func TestForge_Handle_WrongType_ReturnsFalse(t *testing.T) {
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
