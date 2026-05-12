// SPDX-License-Identifier: MIT

package streaming

import (
	"context"
	"encoding/json"
	"sync"
	"testing"

	"github.com/google/uuid"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

// ── fakeSender ────────────────────────────────────────────────────────────────

type unicastRecord struct {
	Packet      *protocol.MeshPacket
	NextHopUhid string
}

type fakeSender struct {
	mu         sync.Mutex
	uhid       string
	Broadcasts []*protocol.MeshPacket
	Unicasts   []unicastRecord
}

func newFakeSender(uhid string) *fakeSender { return &fakeSender{uhid: uhid} }

func (f *fakeSender) LocalUhid() string                 { return f.uhid }
func (f *fakeSender) LocalGeohash() string              { return "" }
func (f *fakeSender) ConnectedPeers() []models.PeerInfo { return nil }

func (f *fakeSender) Send(_ context.Context, packet *protocol.MeshPacket, nextHopUhid string) (bool, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.Unicasts = append(f.Unicasts, unicastRecord{Packet: &c, NextHopUhid: nextHopUhid})
	return true, nil
}

func (f *fakeSender) Broadcast(_ context.Context, packet *protocol.MeshPacket) (int, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.Broadcasts = append(f.Broadcasts, &c)
	return 1, nil
}

func (f *fakeSender) unicastsTo(uhid string) []*protocol.MeshPacket {
	f.mu.Lock()
	defer f.mu.Unlock()
	var out []*protocol.MeshPacket
	for _, r := range f.Unicasts {
		if r.NextHopUhid == uhid {
			out = append(out, r.Packet)
		}
	}
	return out
}

// ── Helpers ───────────────────────────────────────────────────────────────────

func buildSubscribePacket(t *testing.T, from, to string, streamID uuid.UUID) *protocol.MeshPacket {
	t.Helper()
	payload, err := json.Marshal(StreamSubscribePayload{StreamID: streamID.String(), LiveOnly: false})
	if err != nil {
		t.Fatalf("marshal subscribe: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.StreamSubscribe
	pkt.SourceUhid = from
	pkt.DestinationUhid = to
	pkt.Payload = payload
	return pkt
}

func buildUnsubscribePacket(t *testing.T, from, to string, streamID uuid.UUID) *protocol.MeshPacket {
	t.Helper()
	payload, err := json.Marshal(StreamUnsubscribePayload{StreamID: streamID.String()})
	if err != nil {
		t.Fatalf("marshal unsubscribe: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.StreamUnsubscribe
	pkt.SourceUhid = from
	pkt.DestinationUhid = to
	pkt.Payload = payload
	return pkt
}

func buildAnnouncePacket(t *testing.T, from string, streamID uuid.UUID, state string) *protocol.MeshPacket {
	t.Helper()
	payload, err := json.Marshal(StreamAnnouncePayload{
		StreamID: streamID.String(),
		State:    state,
		Title:    "test",
	})
	if err != nil {
		t.Fatalf("marshal announce: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.StreamAnnounce
	pkt.SourceUhid = from
	pkt.Payload = payload
	return pkt
}

// ── StartStream ───────────────────────────────────────────────────────────────

func TestStartStream_BroadcastsStreamAnnounce(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewStreamingService(sender)

	streamID, err := svc.StartStream(context.Background(), "My Stream", "video/h264", "h264", 2000)
	if err != nil {
		t.Fatalf("StartStream: %v", err)
	}
	if streamID == uuid.Nil {
		t.Fatal("expected non-nil stream ID")
	}
	if len(sender.Broadcasts) != 1 {
		t.Fatalf("expected 1 broadcast, got %d", len(sender.Broadcasts))
	}
	pkt := sender.Broadcasts[0]
	if pkt.Type != protocol.StreamAnnounce {
		t.Errorf("expected StreamAnnounce, got %s", pkt.Type)
	}
	var announce StreamAnnouncePayload
	if err := json.Unmarshal(pkt.Payload, &announce); err != nil {
		t.Fatalf("unmarshal announce: %v", err)
	}
	if announce.State != "live" {
		t.Errorf("expected state=live, got %q", announce.State)
	}
	if announce.Title != "My Stream" {
		t.Errorf("expected title=%q, got %q", "My Stream", announce.Title)
	}
}

func TestStartStream_EmptyTitle_ReturnsError(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewStreamingService(sender)

	_, err := svc.StartStream(context.Background(), "", "video/h264", "h264", 2000)
	if err == nil {
		t.Fatal("expected error for empty title")
	}
}

// ── EndStream ─────────────────────────────────────────────────────────────────

func TestEndStream_BroadcastsEndedAnnounce(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewStreamingService(sender)

	streamID, _ := svc.StartStream(context.Background(), "Test", "video/h264", "h264", 1000)
	sender.Broadcasts = nil // clear start announce

	if err := svc.EndStream(context.Background(), streamID); err != nil {
		t.Fatalf("EndStream: %v", err)
	}
	if len(sender.Broadcasts) != 1 {
		t.Fatalf("expected 1 broadcast on end, got %d", len(sender.Broadcasts))
	}
	var announce StreamAnnouncePayload
	_ = json.Unmarshal(sender.Broadcasts[0].Payload, &announce)
	if announce.State != "ended" {
		t.Errorf("expected state=ended, got %q", announce.State)
	}
}

func TestEndStream_UnknownStream_ReturnsError(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewStreamingService(sender)

	err := svc.EndStream(context.Background(), uuid.New())
	if err == nil {
		t.Fatal("expected error for unknown stream ID")
	}
}

// ── HandlePacket / Subscribe flow ─────────────────────────────────────────────

func TestHandlePacket_Subscribe_ThenPublishSegment_ReachesSubscriber(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewStreamingService(sender)

	streamID, _ := svc.StartStream(context.Background(), "Test", "video/h264", "h264", 1000)
	sender.Broadcasts = nil

	// Bob subscribes.
	subPkt := buildSubscribePacket(t, "bob", "alice", streamID)
	if err := svc.HandlePacket(context.Background(), subPkt); err != nil {
		t.Fatalf("HandlePacket subscribe: %v", err)
	}

	// Alice publishes a segment.
	data := []byte{0x01, 0x02, 0x03, 0x04}
	if err := svc.PublishSegment(context.Background(), streamID, data, true); err != nil {
		t.Fatalf("PublishSegment: %v", err)
	}

	// Bob must have received a StreamSegment unicast.
	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected StreamSegment unicast to bob")
	}
	if toBob[0].Type != protocol.StreamSegment {
		t.Errorf("expected StreamSegment, got %s", toBob[0].Type)
	}
	if len(toBob[0].Payload) == 0 {
		t.Error("expected non-empty segment payload")
	}
}

func TestHandlePacket_Unsubscribe_StopsSegmentDelivery(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewStreamingService(sender)

	streamID, _ := svc.StartStream(context.Background(), "Test", "video/h264", "h264", 1000)
	sender.Broadcasts = nil

	// Bob subscribes then immediately unsubscribes.
	_ = svc.HandlePacket(context.Background(), buildSubscribePacket(t, "bob", "alice", streamID))
	_ = svc.HandlePacket(context.Background(), buildUnsubscribePacket(t, "bob", "alice", streamID))

	sender.mu.Lock()
	sender.Unicasts = nil // clear any acks
	sender.mu.Unlock()

	// Publish — bob should receive nothing.
	_ = svc.PublishSegment(context.Background(), streamID, []byte{1, 2, 3}, false)

	if len(sender.unicastsTo("bob")) > 0 {
		t.Error("unsubscribed bob should not receive segments")
	}
}

func TestHandlePacket_Announce_FiresCallback(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewStreamingService(sender)

	var receivedTitle string
	var receivedFrom string
	svc.OnStreamAnnounced = func(p *StreamAnnouncePayload, fromUhid string) {
		receivedTitle = p.Title
		receivedFrom = fromUhid
	}

	remoteStreamID := uuid.New()
	announcePkt := buildAnnouncePacket(t, "charlie", remoteStreamID, "live")
	if err := svc.HandlePacket(context.Background(), announcePkt); err != nil {
		t.Fatalf("HandlePacket announce: %v", err)
	}

	if receivedTitle != "test" {
		t.Errorf("expected title=%q, got %q", "test", receivedTitle)
	}
	if receivedFrom != "charlie" {
		t.Errorf("expected from=charlie, got %q", receivedFrom)
	}
}

func TestHandlePacket_EndedAnnounce_FiresEndedCallback(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewStreamingService(sender)

	endedID := uuid.New()
	var gotEndedID uuid.UUID
	svc.OnStreamEnded = func(id uuid.UUID, _ string) { gotEndedID = id }

	_ = svc.HandlePacket(context.Background(), buildAnnouncePacket(t, "charlie", endedID, "ended"))
	if gotEndedID != endedID {
		t.Errorf("OnStreamEnded got wrong ID: %v", gotEndedID)
	}
}

// ── Subscribe (outbound) ──────────────────────────────────────────────────────

func TestSubscribe_SendsSubscribePacketToPublisher(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewStreamingService(sender)

	remoteStreamID := uuid.New()
	if err := svc.Subscribe(context.Background(), remoteStreamID, "bob", false); err != nil {
		t.Fatalf("Subscribe: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected Subscribe packet unicast to bob")
	}
	if toBob[0].Type != protocol.StreamSubscribe {
		t.Errorf("expected StreamSubscribe, got %s", toBob[0].Type)
	}
}

func TestUnsubscribe_SendsUnsubscribePacketToPublisher(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewStreamingService(sender)

	remoteStreamID := uuid.New()
	_ = svc.Subscribe(context.Background(), remoteStreamID, "bob", false)
	sender.mu.Lock()
	sender.Unicasts = nil
	sender.mu.Unlock()

	if err := svc.Unsubscribe(context.Background(), remoteStreamID, "bob"); err != nil {
		t.Fatalf("Unsubscribe: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected Unsubscribe packet unicast to bob")
	}
	if toBob[0].Type != protocol.StreamUnsubscribe {
		t.Errorf("expected StreamUnsubscribe, got %s", toBob[0].Type)
	}
}

// ── Multiple subscribers ──────────────────────────────────────────────────────

func TestPublishSegment_FansOutToMultipleSubscribers(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewStreamingService(sender)

	streamID, _ := svc.StartStream(context.Background(), "Test", "video/h264", "h264", 1000)

	_ = svc.HandlePacket(context.Background(), buildSubscribePacket(t, "bob", "alice", streamID))
	_ = svc.HandlePacket(context.Background(), buildSubscribePacket(t, "carol", "alice", streamID))
	sender.mu.Lock()
	sender.Unicasts = nil
	sender.mu.Unlock()

	_ = svc.PublishSegment(context.Background(), streamID, []byte{1, 2, 3}, false)

	toBob := sender.unicastsTo("bob")
	toCarol := sender.unicastsTo("carol")
	if len(toBob) == 0 {
		t.Error("bob should have received segment")
	}
	if len(toCarol) == 0 {
		t.Error("carol should have received segment")
	}
}
