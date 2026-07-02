// SPDX-License-Identifier: MIT

package channels

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

// fakeSender is a routing.MeshSender that records broadcasts — no transport needed.
// Mirrors the C# FakeMeshSender in ChannelMessageTests.cs (BroadcastAsync returns 1).
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

// channelPacket builds a ChannelMessage packet with a payload serialized exactly
// like the wire encoder does. Mirrors ChannelPacket in ChannelMessageTests.cs.
func channelPacket(t *testing.T, channelID string, messageID uuid.UUID, sender, content string, sentAtMs int64, ttl int32) *protocol.MeshPacket {
	t.Helper()
	body, err := json.Marshal(channelMessageWire{
		ChannelID:  channelID,
		MessageID:  messageID,
		SenderUhid: sender,
		Content:    content,
		SentAtMs:   sentAtMs,
	})
	if err != nil {
		t.Fatalf("marshal channel payload: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.ChannelMessage
	pkt.SourceUhid = sender
	pkt.DestinationUhid = "*"
	pkt.Ttl = ttl
	pkt.Payload = body
	return pkt
}

// ─── Byte-identity ────────────────────────────────────────
// Locks the ChannelMessagePayload wire encoding to fixtures/channels/vectors.json.

func TestChannelMessagePayload_SerializesToCanonicalBytes(t *testing.T) {
	cases := []struct {
		name      string
		channelID string
		messageID string
		sender    string
		content   string
		sentAtMs  int64
		expected  string
	}{
		{
			name:      "basic",
			channelID: "res-floor-3",
			messageID: "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f",
			sender:    "aether:alice:01",
			content:   "meeting at 6",
			sentAtMs:  1700000000000,
			expected:  `{"channel_id":"res-floor-3","message_id":"0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f","sender_uhid":"aether:alice:01","content":"meeting at 6","sent_at_ms":1700000000000}`,
		},
		{
			name:      "minimal",
			channelID: "g",
			messageID: "00000000-0000-0000-0000-000000000000",
			sender:    "n",
			content:   "",
			sentAtMs:  0,
			expected:  `{"channel_id":"g","message_id":"00000000-0000-0000-0000-000000000000","sender_uhid":"n","content":"","sent_at_ms":0}`,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			id, err := uuid.Parse(tc.messageID)
			if err != nil {
				t.Fatalf("parse message id: %v", err)
			}
			got, err := json.Marshal(channelMessageWire{
				ChannelID:  tc.channelID,
				MessageID:  id,
				SenderUhid: tc.sender,
				Content:    tc.content,
				SentAtMs:   tc.sentAtMs,
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

// ─── Publish ──────────────────────────────────────────────

func TestPublish_BroadcastsChannelMessage(t *testing.T) {
	sender := newFakeSender("aether:alice:01")
	svc := NewService(sender)

	if _, err := svc.Publish(context.Background(), "res-floor-3", "meeting at 6"); err != nil {
		t.Fatalf("publish: %v", err)
	}

	if len(sender.Broadcasts) != 1 {
		t.Fatalf("expected 1 broadcast, got %d", len(sender.Broadcasts))
	}
	pkt := sender.Broadcasts[0]
	if pkt.Type != protocol.ChannelMessage {
		t.Fatalf("expected ChannelMessage, got %v", pkt.Type)
	}
	if pkt.DestinationUhid != "*" {
		t.Fatalf("expected dest *, got %s", pkt.DestinationUhid)
	}
	if pkt.Ttl != constants.DefaultTtl {
		t.Fatalf("expected ttl=DefaultTtl (%d), got %d", constants.DefaultTtl, pkt.Ttl)
	}

	var body channelMessageWire
	if err := json.Unmarshal(pkt.Payload, &body); err != nil {
		t.Fatalf("unmarshal payload: %v", err)
	}
	if body.ChannelID != "res-floor-3" {
		t.Fatalf("expected channel res-floor-3, got %s", body.ChannelID)
	}
	if body.Content != "meeting at 6" {
		t.Fatalf("expected content 'meeting at 6', got %s", body.Content)
	}
	if body.SenderUhid != "aether:alice:01" {
		t.Fatalf("expected sender aether:alice:01, got %s", body.SenderUhid)
	}
}

func TestPublish_EmptyChannel_ReturnsError(t *testing.T) {
	svc := NewService(newFakeSender(local))
	if _, err := svc.Publish(context.Background(), "", "hi"); err == nil {
		t.Fatalf("expected error for empty channel id")
	}
}

func TestPublish_SeedsDedupSetWithOwnId(t *testing.T) {
	sender := newFakeSender("aether:alice:01")
	svc := NewService(sender)

	if _, err := svc.Publish(context.Background(), "res-floor-3", "meeting at 6"); err != nil {
		t.Fatalf("publish: %v", err)
	}
	published := sender.Broadcasts[0]

	// Re-handling our own published message (e.g. it floods back to us) must be a
	// no-op: dedup set already holds its id, so Handle returns false and does not
	// re-broadcast.
	sender.Broadcasts = nil
	ok, err := svc.Handle(context.Background(), published)
	if err != nil {
		t.Fatalf("handle own message: %v", err)
	}
	if ok {
		t.Fatalf("expected own published message to be de-duped (false)")
	}
	if len(sender.Broadcasts) != 0 {
		t.Fatalf("expected no re-broadcast of own message, got %d", len(sender.Broadcasts))
	}
}

// ─── Handle ───────────────────────────────────────────────

func TestHandle_SubscribedChannel_RaisesEvent(t *testing.T) {
	svc := NewService(newFakeSender(local))
	svc.Subscribe("res-floor-3")

	var got *MessageReceived
	svc.OnMessageReceived = func(m MessageReceived) { got = &m }

	ok, err := svc.Handle(context.Background(),
		channelPacket(t, "res-floor-3", uuid.New(), "aether:bob:02", "hello floor", 1700000000000, 7))
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if got == nil {
		t.Fatalf("expected OnMessageReceived to fire")
	}
	if got.ChannelId != "res-floor-3" {
		t.Fatalf("expected channel res-floor-3, got %s", got.ChannelId)
	}
	if got.Content != "hello floor" {
		t.Fatalf("expected content 'hello floor', got %s", got.Content)
	}
	if got.SenderUhid != "aether:bob:02" {
		t.Fatalf("expected sender aether:bob:02, got %s", got.SenderUhid)
	}
}

func TestHandle_UnsubscribedChannel_NoEventButProcessed(t *testing.T) {
	svc := NewService(newFakeSender(local))
	raised := false
	svc.OnMessageReceived = func(m MessageReceived) { raised = true }

	ok, err := svc.Handle(context.Background(),
		channelPacket(t, "society-x", uuid.New(), "aether:bob:02", "hi", 1, 7))
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true (processed + relayed)")
	}
	if raised {
		t.Fatalf("expected no event — we aren't subscribed")
	}
}

func TestHandle_DuplicateMessageId_ReturnsFalse(t *testing.T) {
	svc := NewService(newFakeSender(local))
	svc.Subscribe("res-floor-3")
	id := uuid.New()

	events := 0
	svc.OnMessageReceived = func(m MessageReceived) { events++ }

	ok1, err := svc.Handle(context.Background(),
		channelPacket(t, "res-floor-3", id, "aether:bob:02", "one", 1, 7))
	if err != nil {
		t.Fatalf("handle 1: %v", err)
	}
	if !ok1 {
		t.Fatalf("expected first handle ok=true")
	}
	ok2, err := svc.Handle(context.Background(),
		channelPacket(t, "res-floor-3", id, "aether:bob:02", "one", 1, 7))
	if err != nil {
		t.Fatalf("handle 2: %v", err)
	}
	if ok2 {
		t.Fatalf("expected duplicate handle ok=false")
	}
	if events != 1 {
		t.Fatalf("expected exactly 1 event, got %d", events)
	}
}

func TestHandle_WrongPacketType_ReturnsFalse(t *testing.T) {
	svc := NewService(newFakeSender(local))
	pkt := channelPacket(t, "res-floor-3", uuid.New(), "aether:bob:02", "x", 1, 7)
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
	pkt.Type = protocol.ChannelMessage
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

func TestHandle_NilPacket_ReturnsError(t *testing.T) {
	svc := NewService(newFakeSender(local))
	if _, err := svc.Handle(context.Background(), nil); err == nil {
		t.Fatalf("expected error for nil packet")
	}
}

func TestHandle_RelaysWhenTtlAllows(t *testing.T) {
	relaySender := newFakeSender("aether:relay:09")
	svc := NewService(relaySender) // not subscribed — pure relay

	if _, err := svc.Handle(context.Background(),
		channelPacket(t, "res-floor-3", uuid.New(), "aether:bob:02", "hop", 1, 5)); err != nil {
		t.Fatalf("handle: %v", err)
	}

	if len(relaySender.Broadcasts) != 1 {
		t.Fatalf("expected 1 relayed broadcast, got %d", len(relaySender.Broadcasts))
	}
	relayed := relaySender.Broadcasts[0]
	if relayed.Type != protocol.ChannelMessage {
		t.Fatalf("expected ChannelMessage, got %v", relayed.Type)
	}
	if relayed.Ttl != 4 {
		t.Fatalf("expected ttl=4, got %d", relayed.Ttl)
	}
}

func TestHandle_DoesNotRelayWhenTtlExhausted(t *testing.T) {
	relaySender := newFakeSender("aether:relay:09")
	svc := NewService(relaySender)

	if _, err := svc.Handle(context.Background(),
		channelPacket(t, "res-floor-3", uuid.New(), "aether:bob:02", "hop", 1, 1)); err != nil {
		t.Fatalf("handle: %v", err)
	}
	if len(relaySender.Broadcasts) != 0 {
		t.Fatalf("expected no relay for ttl=1, got %d", len(relaySender.Broadcasts))
	}
}

func TestHandle_OwnMessage_NotSurfacedAndNotRelayed(t *testing.T) {
	sender := newFakeSender("aether:alice:01")
	svc := NewService(sender)
	svc.Subscribe("res-floor-3")

	raised := false
	svc.OnMessageReceived = func(m MessageReceived) { raised = true }

	// A message whose sender is us (arriving via a relay hop) is de-duped only if
	// we already published it; here it's a fresh id but authored by us. It must not
	// surface (isOwn) and must not be relayed (isOwn).
	ok, err := svc.Handle(context.Background(),
		channelPacket(t, "res-floor-3", uuid.New(), "aether:alice:01", "mine", 1, 5))
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true (processed)")
	}
	if raised {
		t.Fatalf("expected own message not surfaced")
	}
	if len(sender.Broadcasts) != 0 {
		t.Fatalf("expected own message not relayed, got %d", len(sender.Broadcasts))
	}
}

// ─── Subscriptions ────────────────────────────────────────

func TestSubscriptions_AddRemoveList(t *testing.T) {
	svc := NewService(newFakeSender(local))
	svc.Subscribe("a")
	svc.Subscribe("b")
	svc.Subscribe("a") // idempotent

	subs := svc.GetSubscriptions()
	if len(subs) != 2 {
		t.Fatalf("expected 2 subscriptions, got %d: %v", len(subs), subs)
	}

	svc.Unsubscribe("a")
	subs = svc.GetSubscriptions()
	if len(subs) != 1 || subs[0] != "b" {
		t.Fatalf("expected only [b] after unsubscribe, got %v", subs)
	}
}
