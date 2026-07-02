// SPDX-License-Identifier: MIT

package profiles

import (
	"context"
	"encoding/json"
	"testing"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

const local = "aether:local:01"

type sendRecord struct {
	Packet      *protocol.MeshPacket
	NextHopUhid string
}

// fakeSender is a routing.MeshSender that records directed sends — no transport
// needed. Mirrors the C# FakeMeshSender in ProfileSyncTests.cs (SendAsync returns
// true, records the packet + next hop).
type fakeSender struct {
	uhid  string
	peers []models.PeerInfo
	Sends []sendRecord
}

func newFakeSender(uhid string) *fakeSender { return &fakeSender{uhid: uhid} }

func (f *fakeSender) LocalUhid() string                 { return f.uhid }
func (f *fakeSender) LocalGeohash() string              { return "" }
func (f *fakeSender) ConnectedPeers() []models.PeerInfo { return f.peers }

func (f *fakeSender) Send(ctx context.Context, packet *protocol.MeshPacket, nextHopUhid string) (bool, error) {
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.Sends = append(f.Sends, sendRecord{Packet: &c, NextHopUhid: nextHopUhid})
	return true, nil
}

func (f *fakeSender) Broadcast(ctx context.Context, packet *protocol.MeshPacket) (int, error) {
	return 0, nil
}

// profilePacket builds a ProfileSync packet with a payload serialized exactly like
// the wire encoder does. Mirrors ProfilePacket in ProfileSyncTests.cs.
func profilePacket(t *testing.T, uhid, name, avatar, status string, updatedAtMs int64) *protocol.MeshPacket {
	t.Helper()
	body, err := json.Marshal(Profile{
		Uhid:          uhid,
		DisplayName:   name,
		AvatarRef:     avatar,
		StatusMessage: status,
		UpdatedAtMs:   updatedAtMs,
	})
	if err != nil {
		t.Fatalf("marshal profile payload: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.ProfileSync
	pkt.SourceUhid = uhid
	pkt.DestinationUhid = local
	pkt.Payload = body
	return pkt
}

// ─── Byte-identity ────────────────────────────────────────
// Locks the ProfileSyncPayload wire encoding to fixtures/profiles/vectors.json.

func TestProfileSyncPayload_SerializesToCanonicalBytes(t *testing.T) {
	cases := []struct {
		name        string
		uhid        string
		displayName string
		avatarRef   string
		status      string
		updatedAtMs int64
		expected    string
	}{
		{
			name:        "basic",
			uhid:        "aether:alice:01",
			displayName: "Alice",
			avatarRef:   "blake3:abc",
			status:      "available",
			updatedAtMs: 1700000000000,
			expected:    `{"uhid":"aether:alice:01","display_name":"Alice","avatar_ref":"blake3:abc","status_message":"available","updated_at_ms":1700000000000}`,
		},
		{
			name:        "minimal",
			uhid:        "n",
			displayName: "",
			avatarRef:   "",
			status:      "",
			updatedAtMs: 0,
			expected:    `{"uhid":"n","display_name":"","avatar_ref":"","status_message":"","updated_at_ms":0}`,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got, err := json.Marshal(Profile{
				Uhid:          tc.uhid,
				DisplayName:   tc.displayName,
				AvatarRef:     tc.avatarRef,
				StatusMessage: tc.status,
				UpdatedAtMs:   tc.updatedAtMs,
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

// ─── PublishProfileTo ─────────────────────────────────────

func TestPublishProfileTo_SendsDirectedProfileToPeer(t *testing.T) {
	sender := newFakeSender("aether:alice:01")
	svc := NewService(sender)
	svc.SetLocalProfile("Alice", "blake3:abc", "available")

	ok, err := svc.PublishProfileTo(context.Background(), "aether:bob:02")
	if err != nil {
		t.Fatalf("publish: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}

	if len(sender.Sends) != 1 {
		t.Fatalf("expected 1 directed send, got %d", len(sender.Sends))
	}
	sent := sender.Sends[0]
	if sent.Packet.Type != protocol.ProfileSync {
		t.Fatalf("expected ProfileSync, got %v", sent.Packet.Type)
	}
	if sent.NextHopUhid != "aether:bob:02" {
		t.Fatalf("expected next hop aether:bob:02, got %s", sent.NextHopUhid)
	}
	if sent.Packet.DestinationUhid != "aether:bob:02" {
		t.Fatalf("expected dest aether:bob:02, got %s", sent.Packet.DestinationUhid)
	}
	if sent.Packet.Ttl != constants.DefaultTtl {
		t.Fatalf("expected ttl=DefaultTtl (%d), got %d", constants.DefaultTtl, sent.Packet.Ttl)
	}

	var body Profile
	if err := json.Unmarshal(sent.Packet.Payload, &body); err != nil {
		t.Fatalf("unmarshal payload: %v", err)
	}
	if body.Uhid != "aether:alice:01" {
		t.Fatalf("expected uhid aether:alice:01, got %s", body.Uhid)
	}
	if body.DisplayName != "Alice" {
		t.Fatalf("expected display name Alice, got %s", body.DisplayName)
	}
}

func TestPublishProfileTo_EmptyPeer_ReturnsError(t *testing.T) {
	svc := NewService(newFakeSender(local))
	if _, err := svc.PublishProfileTo(context.Background(), ""); err == nil {
		t.Fatalf("expected error for empty peer uhid")
	}
}

// ─── Handle ───────────────────────────────────────────────

func TestHandle_CachesPeerProfileAndRaisesEvent(t *testing.T) {
	svc := NewService(newFakeSender(local))
	var updated *Profile
	svc.OnProfileUpdated = func(p Profile) { updated = &p }

	ok, err := svc.Handle(context.Background(),
		profilePacket(t, "aether:bob:02", "Bob", "blake3:xyz", "busy", 1700000000000))
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if updated == nil {
		t.Fatalf("expected OnProfileUpdated to fire")
	}
	if updated.DisplayName != "Bob" {
		t.Fatalf("expected display name Bob, got %s", updated.DisplayName)
	}

	cached, ok := svc.GetProfile("aether:bob:02")
	if !ok {
		t.Fatalf("expected cached profile for aether:bob:02")
	}
	if cached.StatusMessage != "busy" {
		t.Fatalf("expected status busy, got %s", cached.StatusMessage)
	}
	if len(svc.GetKnownProfiles()) != 1 {
		t.Fatalf("expected 1 known profile, got %d", len(svc.GetKnownProfiles()))
	}
}

func TestHandle_RefreshesExistingProfile(t *testing.T) {
	svc := NewService(newFakeSender(local))

	if _, err := svc.Handle(context.Background(),
		profilePacket(t, "aether:bob:02", "Bob", "", "here", 1000)); err != nil {
		t.Fatalf("handle 1: %v", err)
	}
	if _, err := svc.Handle(context.Background(),
		profilePacket(t, "aether:bob:02", "Bob", "", "away", 2000)); err != nil {
		t.Fatalf("handle 2: %v", err)
	}

	cached, ok := svc.GetProfile("aether:bob:02")
	if !ok {
		t.Fatalf("expected cached profile")
	}
	if cached.StatusMessage != "away" {
		t.Fatalf("expected status away, got %s", cached.StatusMessage)
	}
	if len(svc.GetKnownProfiles()) != 1 {
		t.Fatalf("expected 1 known profile, got %d", len(svc.GetKnownProfiles()))
	}
}

func TestHandle_OwnProfile_IsIgnored(t *testing.T) {
	svc := NewService(newFakeSender(local))
	ok, err := svc.Handle(context.Background(),
		profilePacket(t, local, "Me", "", "", 1))
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for own profile")
	}
	if len(svc.GetKnownProfiles()) != 0 {
		t.Fatalf("expected no known profiles for own profile")
	}
}

func TestHandle_WrongPacketType_ReturnsFalse(t *testing.T) {
	svc := NewService(newFakeSender(local))
	pkt := profilePacket(t, "aether:bob:02", "Bob", "", "", 1)
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
	pkt.Type = protocol.ProfileSync
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

func TestHandle_EmptyUhid_ReturnsFalse(t *testing.T) {
	svc := NewService(newFakeSender(local))
	pkt := profilePacket(t, "", "Bob", "", "", 1)

	ok, err := svc.Handle(context.Background(), pkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for empty uhid")
	}
	if len(svc.GetKnownProfiles()) != 0 {
		t.Fatalf("expected no known profiles for empty uhid")
	}
}

func TestHandle_NilPacket_ReturnsError(t *testing.T) {
	svc := NewService(newFakeSender(local))
	if _, err := svc.Handle(context.Background(), nil); err == nil {
		t.Fatalf("expected error for nil packet")
	}
}

// ─── Local profile ────────────────────────────────────────

func TestSetLocalProfile_StampsUhidAndFields(t *testing.T) {
	svc := NewService(newFakeSender("aether:alice:01"))
	svc.SetLocalProfile("Alice", "blake3:abc", "available")

	local := svc.GetLocalProfile()
	if local.Uhid != "aether:alice:01" {
		t.Fatalf("expected uhid aether:alice:01, got %s", local.Uhid)
	}
	if local.DisplayName != "Alice" {
		t.Fatalf("expected display name Alice, got %s", local.DisplayName)
	}
	if local.AvatarRef != "blake3:abc" {
		t.Fatalf("expected avatar blake3:abc, got %s", local.AvatarRef)
	}
	if local.StatusMessage != "available" {
		t.Fatalf("expected status available, got %s", local.StatusMessage)
	}
	if local.UpdatedAtMs == 0 {
		t.Fatalf("expected UpdatedAtMs to be stamped")
	}
}

func TestGetProfile_UnknownUhid_ReturnsFalse(t *testing.T) {
	svc := NewService(newFakeSender(local))
	if _, ok := svc.GetProfile("aether:nobody:00"); ok {
		t.Fatalf("expected ok=false for unknown uhid")
	}
}
