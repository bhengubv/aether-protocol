// SPDX-License-Identifier: MIT

package vault

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

// vaultShardVectors mirrors fixtures/vaultshard/vectors.json — the canonical
// cross-language parity source generated from the C# reference. Every language
// port MUST reproduce expected_json byte-for-byte.
type vaultShardVectors struct {
	Vectors []struct {
		Name          string `json:"name"`
		ShardHash     string `json:"shard_hash"`
		RequesterUhid string `json:"requester_uhid"`
		ExpectedJSON  string `json:"expected_json"`
	} `json:"vectors"`
}

func loadVaultShardVectors(t *testing.T) vaultShardVectors {
	t.Helper()
	path := filepath.Join("..", "..", "fixtures", "vaultshard", "vectors.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read fixtures: %v", err)
	}
	var v vaultShardVectors
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("parse fixtures: %v", err)
	}
	return v
}

// ─── Byte-identity ────────────────────────────────────────
// Locks the ShardRequestPayload wire encoding to fixtures/vaultshard/vectors.json.

func TestShardRequestPayload_SerializesToCanonicalBytes(t *testing.T) {
	v := loadVaultShardVectors(t)
	if len(v.Vectors) == 0 {
		t.Fatalf("expected at least one vaultshard vector")
	}
	for _, tc := range v.Vectors {
		t.Run(tc.Name, func(t *testing.T) {
			p := ShardRequestPayload{
				ShardHash:     tc.ShardHash,
				RequesterUhid: tc.RequesterUhid,
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

// ─── RequestShard + Handle ────────────────────────────────

func TestVault_Request_EmitsShardRequestPacket_AndHandleRaisesEvent(t *testing.T) {
	sender := newFakeSender("aether:bob:02")
	svc := NewShardRequestService(sender)

	reached, err := svc.RequestShard(context.Background(), "QmShardHash789")
	if err != nil {
		t.Fatalf("request: %v", err)
	}
	if reached != 2 {
		t.Fatalf("expected reached=2, got %d", reached)
	}
	if len(sender.Broadcasts) != 1 {
		t.Fatalf("expected 1 broadcast, got %d", len(sender.Broadcasts))
	}
	sent := sender.Broadcasts[0]
	if sent.Type != protocol.VaultShardRequest {
		t.Fatalf("expected VaultShardRequest, got %v", sent.Type)
	}

	var body ShardRequestPayload
	if err := json.Unmarshal(sent.Payload, &body); err != nil {
		t.Fatalf("unmarshal payload: %v", err)
	}
	if body.ShardHash != "QmShardHash789" {
		t.Fatalf("expected shard_hash QmShardHash789, got %s", body.ShardHash)
	}
	// requester_uhid must be stamped with the local node's UHID.
	if body.RequesterUhid != "aether:bob:02" {
		t.Fatalf("expected requester_uhid aether:bob:02, got %s", body.RequesterUhid)
	}

	var got *ShardRequest
	svc.OnShardRequested = func(r ShardRequest) { got = &r }
	ok, err := svc.Handle(context.Background(), sent)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if got == nil {
		t.Fatalf("expected OnShardRequested to fire")
	}
	if got.ShardHash != "QmShardHash789" {
		t.Fatalf("expected shard_hash QmShardHash789, got %s", got.ShardHash)
	}
	if got.RequesterUhid != "aether:bob:02" {
		t.Fatalf("expected requester_uhid aether:bob:02, got %s", got.RequesterUhid)
	}
}

func TestVault_Handle_WrongType_ReturnsFalse(t *testing.T) {
	svc := NewShardRequestService(newFakeSender("aether:local:01"))
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
