// SPDX-License-Identifier: MIT

package content_test

import (
	"context"
	"encoding/json"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"

	"github.com/bhengubv/aether-protocol/go/content"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

// ── fakeMeshSender (mirrors the dtn package's fakeSender, but in content_test) ──

type unicastRecord struct {
	Packet      *protocol.MeshPacket
	NextHopUhid string
}

type fakeMeshSender struct {
	uhid       string
	geohash    string
	peers      []models.PeerInfo
	mu         sync.Mutex
	Unicasts   []unicastRecord
	Broadcasts []*protocol.MeshPacket
}

func newFakeMeshSender(uhid string) *fakeMeshSender { return &fakeMeshSender{uhid: uhid} }

func (f *fakeMeshSender) LocalUhid() string                 { return f.uhid }
func (f *fakeMeshSender) LocalGeohash() string              { return f.geohash }
func (f *fakeMeshSender) ConnectedPeers() []models.PeerInfo { return f.peers }
func (f *fakeMeshSender) AddPeer(p models.PeerInfo)         { f.peers = append(f.peers, p) }

func (f *fakeMeshSender) Send(ctx context.Context, packet *protocol.MeshPacket, nextHopUhid string) (bool, error) {
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.mu.Lock()
	f.Unicasts = append(f.Unicasts, unicastRecord{Packet: &c, NextHopUhid: nextHopUhid})
	f.mu.Unlock()
	return true, nil
}

func (f *fakeMeshSender) Broadcast(ctx context.Context, packet *protocol.MeshPacket) (int, error) {
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.mu.Lock()
	f.Broadcasts = append(f.Broadcasts, &c)
	f.mu.Unlock()
	return len(f.peers), nil
}

func (f *fakeMeshSender) Clear() {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.Unicasts = nil
	f.Broadcasts = nil
}

func (f *fakeMeshSender) BroadcastsSnapshot() []*protocol.MeshPacket {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]*protocol.MeshPacket, len(f.Broadcasts))
	copy(out, f.Broadcasts)
	return out
}

func (f *fakeMeshSender) UnicastsSnapshot() []unicastRecord {
	f.mu.Lock()
	defer f.mu.Unlock()
	out := make([]unicastRecord, len(f.Unicasts))
	copy(out, f.Unicasts)
	return out
}

// ── helpers ───────────────────────────────────────────────────────────────────

func sampleDescriptor(rootHash string) content.ContentDescriptor {
	return content.ContentDescriptor{
		RootHash:       rootHash,
		Name:           "ignored-publisher-hint",
		TotalBytes:     1024,
		ChunkSizeBytes: 256,
		ChunkCount:     4,
		ChunkHashes:    []string{"h0", "h1", "h2", "h3"},
		ContentType:    "audio/flac",
	}
}

// ── PublishAsync ──────────────────────────────────────────────────────────────

func TestPublish_StoresLocallyAndBroadcastsNamePublish(t *testing.T) {
	sender := newFakeMeshSender("publisher")
	sender.AddPeer(models.PeerInfo{UHID: "peer-1"})
	sender.AddPeer(models.PeerInfo{UHID: "peer-2"})
	dir := content.NewDirectoryService(sender)

	if err := dir.Publish(context.Background(), "podcast:abc", sampleDescriptor("root-abc")); err != nil {
		t.Fatalf("publish: %v", err)
	}

	// Local resolve hits the catalogue immediately.
	hit, err := dir.Resolve(context.Background(), "podcast:abc", 0)
	if err != nil {
		t.Fatalf("resolve: %v", err)
	}
	if hit == nil {
		t.Fatal("expected resolve to return a descriptor, got nil")
	}
	if hit.RootHash != "root-abc" {
		t.Fatalf("expected root_hash=root-abc, got %q", hit.RootHash)
	}

	// One broadcast went out — the NamePublish.
	bcs := sender.BroadcastsSnapshot()
	if len(bcs) != 1 {
		t.Fatalf("expected 1 broadcast, got %d", len(bcs))
	}
	if bcs[0].Type != protocol.NamePublish {
		t.Fatalf("expected broadcast type NamePublish, got %v", bcs[0].Type)
	}
}

func TestResolve_LocalCatalogueHit_ReturnsImmediately_NoQueryBroadcast(t *testing.T) {
	sender := newFakeMeshSender("local")
	sender.AddPeer(models.PeerInfo{UHID: "peer-1"})
	dir := content.NewDirectoryService(sender)

	if err := dir.Publish(context.Background(), "track:xyz", sampleDescriptor("root-xyz")); err != nil {
		t.Fatalf("publish: %v", err)
	}
	sender.Clear()

	hit, err := dir.Resolve(context.Background(), "track:xyz", 0)
	if err != nil {
		t.Fatalf("resolve: %v", err)
	}
	if hit == nil || hit.RootHash != "root-xyz" {
		t.Fatalf("expected root-xyz, got %+v", hit)
	}

	// No NameQuery should have been broadcast — local hit.
	bcs := sender.BroadcastsSnapshot()
	if len(bcs) != 0 {
		t.Fatalf("expected zero broadcasts on local hit, got %d", len(bcs))
	}
}

// ── Inbound NamePublish ───────────────────────────────────────────────────────

func TestHandle_InboundNamePublish_PopulatesCatalogueAndFiresEvent(t *testing.T) {
	sender := newFakeMeshSender("local")
	dir := content.NewDirectoryService(sender)

	var captured *content.DirectoryEntryAnnouncedEvent
	dir.OnEntryAnnounced = func(e *content.DirectoryEntryAnnouncedEvent) {
		captured = e
	}

	// Build a NamePublish packet from a peer by publishing via a sibling service.
	peerSender := newFakeMeshSender("peer-publisher")
	peerSender.AddPeer(models.PeerInfo{UHID: "local"})
	peerDir := content.NewDirectoryService(peerSender)
	descriptor := sampleDescriptor("from-peer")
	if err := peerDir.Publish(context.Background(), "reel:hello", descriptor); err != nil {
		t.Fatalf("peer publish: %v", err)
	}

	// Take the broadcast and replay it into the local service.
	bcs := peerSender.BroadcastsSnapshot()
	if len(bcs) != 1 {
		t.Fatalf("expected 1 broadcast from peer, got %d", len(bcs))
	}
	broadcast := bcs[0]
	broadcast.SourceUhid = "peer-publisher"
	if err := dir.Handle(context.Background(), broadcast); err != nil {
		t.Fatalf("handle: %v", err)
	}

	// Local catalogue now has the entry.
	hit, err := dir.Resolve(context.Background(), "reel:hello", 0)
	if err != nil {
		t.Fatalf("resolve: %v", err)
	}
	if hit == nil || hit.RootHash != "from-peer" {
		t.Fatalf("expected from-peer, got %+v", hit)
	}

	// Event fired.
	if captured == nil {
		t.Fatal("expected OnEntryAnnounced callback to fire")
	}
	if captured.Name != "reel:hello" {
		t.Fatalf("expected name=reel:hello, got %q", captured.Name)
	}
	if captured.SourceUhid != "peer-publisher" {
		t.Fatalf("expected source=peer-publisher, got %q", captured.SourceUhid)
	}
	if captured.Descriptor.RootHash != "from-peer" {
		t.Fatalf("expected descriptor root=from-peer, got %q", captured.Descriptor.RootHash)
	}
}

// ── Query / Response roundtrip ────────────────────────────────────────────────

func TestHandle_QueryWithMatchingName_UnicastsNamePublishResponse(t *testing.T) {
	holderSender := newFakeMeshSender("holder")
	holderSender.AddPeer(models.PeerInfo{UHID: "asker"})
	holder := content.NewDirectoryService(holderSender)

	if err := holder.Publish(context.Background(), "album:test", sampleDescriptor("album-root")); err != nil {
		t.Fatalf("publish: %v", err)
	}
	holderSender.Clear()

	// Build a NameQuery as if from `asker`.
	queryID := uuid.New()
	queryPayload, err := json.Marshal(content.NameQueryPayload{
		Name:    "album:test",
		QueryID: queryID,
	})
	if err != nil {
		t.Fatalf("marshal query: %v", err)
	}
	queryPacket := protocol.NewMeshPacket()
	queryPacket.Type = protocol.NameQuery
	queryPacket.SourceUhid = "asker"
	queryPacket.Payload = queryPayload

	if err := holder.Handle(context.Background(), queryPacket); err != nil {
		t.Fatalf("handle: %v", err)
	}

	// Holder unicasts back a NamePublish with InResponseToQueryID set.
	unicasts := holderSender.UnicastsSnapshot()
	if len(unicasts) != 1 {
		t.Fatalf("expected 1 unicast, got %d", len(unicasts))
	}
	resp := unicasts[0]
	if resp.NextHopUhid != "asker" {
		t.Fatalf("expected unicast to asker, got %q", resp.NextHopUhid)
	}
	if resp.Packet.Type != protocol.NamePublish {
		t.Fatalf("expected NamePublish response, got %v", resp.Packet.Type)
	}

	var responseBody content.NamePublishPayload
	if err := json.Unmarshal(resp.Packet.Payload, &responseBody); err != nil {
		t.Fatalf("unmarshal response: %v", err)
	}
	if responseBody.Name != "album:test" {
		t.Fatalf("expected name=album:test, got %q", responseBody.Name)
	}
	if responseBody.Descriptor.RootHash != "album-root" {
		t.Fatalf("expected descriptor root=album-root, got %q", responseBody.Descriptor.RootHash)
	}
	if responseBody.InResponseToQueryID == nil {
		t.Fatal("expected InResponseToQueryID to be set on response")
	}
	if *responseBody.InResponseToQueryID != queryID {
		t.Fatalf("expected query_id=%v, got %v", queryID, *responseBody.InResponseToQueryID)
	}
}

func TestHandle_QueryForUnknownName_DoesNothing(t *testing.T) {
	sender := newFakeMeshSender("local")
	sender.AddPeer(models.PeerInfo{UHID: "asker"})
	dir := content.NewDirectoryService(sender)

	queryPayload, err := json.Marshal(content.NameQueryPayload{
		Name:    "nothing-here",
		QueryID: uuid.New(),
	})
	if err != nil {
		t.Fatalf("marshal query: %v", err)
	}
	queryPacket := protocol.NewMeshPacket()
	queryPacket.Type = protocol.NameQuery
	queryPacket.SourceUhid = "asker"
	queryPacket.Payload = queryPayload

	if err := dir.Handle(context.Background(), queryPacket); err != nil {
		t.Fatalf("handle: %v", err)
	}

	if u := sender.UnicastsSnapshot(); len(u) != 0 {
		t.Fatalf("expected zero unicasts, got %d", len(u))
	}
	if b := sender.BroadcastsSnapshot(); len(b) != 0 {
		t.Fatalf("expected zero broadcasts, got %d", len(b))
	}
}

func TestResolve_MissAndTimeout_ReturnsNil(t *testing.T) {
	sender := newFakeMeshSender("local")
	sender.AddPeer(models.PeerInfo{UHID: "peer-1"})
	dir := content.NewDirectoryService(sender)

	hit, err := dir.Resolve(context.Background(), "unknown-name", 50*time.Millisecond)
	if err != nil {
		t.Fatalf("resolve: %v", err)
	}
	if hit != nil {
		t.Fatalf("expected nil on timeout, got %+v", hit)
	}

	// A NameQuery was broadcast — we tried.
	bcs := sender.BroadcastsSnapshot()
	if len(bcs) != 1 {
		t.Fatalf("expected 1 broadcast (the NameQuery), got %d", len(bcs))
	}
	if bcs[0].Type != protocol.NameQuery {
		t.Fatalf("expected NameQuery broadcast, got %v", bcs[0].Type)
	}
}

func TestResolve_QueryAndAnswerArrives_ReturnsDescriptor(t *testing.T) {
	sender := newFakeMeshSender("local")
	sender.AddPeer(models.PeerInfo{UHID: "peer-1"})
	dir := content.NewDirectoryService(sender)

	// Start a resolve in the background.
	type result struct {
		desc *content.ContentDescriptor
		err  error
	}
	resultCh := make(chan result, 1)
	go func() {
		d, e := dir.Resolve(context.Background(), "podcast:remote", 2*time.Second)
		resultCh <- result{desc: d, err: e}
	}()

	// Wait briefly for the NameQuery to be broadcast.
	var queryBroadcast *protocol.MeshPacket
	for i := 0; i < 50; i++ {
		bcs := sender.BroadcastsSnapshot()
		if len(bcs) == 1 {
			queryBroadcast = bcs[0]
			break
		}
		time.Sleep(10 * time.Millisecond)
	}
	if queryBroadcast == nil {
		t.Fatal("expected NameQuery broadcast within 500ms")
	}
	if queryBroadcast.Type != protocol.NameQuery {
		t.Fatalf("expected NameQuery, got %v", queryBroadcast.Type)
	}

	var query content.NameQueryPayload
	if err := json.Unmarshal(queryBroadcast.Payload, &query); err != nil {
		t.Fatalf("unmarshal query: %v", err)
	}

	// Simulate a peer responding with a NamePublish carrying InResponseToQueryID.
	descriptor := sampleDescriptor("remote-root")
	queryID := query.QueryID
	responsePayload, err := json.Marshal(content.NamePublishPayload{
		Name:                "podcast:remote",
		Descriptor:          descriptor,
		InResponseToQueryID: &queryID,
	})
	if err != nil {
		t.Fatalf("marshal response: %v", err)
	}
	responsePacket := protocol.NewMeshPacket()
	responsePacket.Type = protocol.NamePublish
	responsePacket.SourceUhid = "peer-1"
	responsePacket.Payload = responsePayload
	if err := dir.Handle(context.Background(), responsePacket); err != nil {
		t.Fatalf("handle: %v", err)
	}

	select {
	case r := <-resultCh:
		if r.err != nil {
			t.Fatalf("resolve: %v", r.err)
		}
		if r.desc == nil {
			t.Fatal("expected descriptor, got nil")
		}
		if r.desc.RootHash != "remote-root" {
			t.Fatalf("expected remote-root, got %q", r.desc.RootHash)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("resolve did not complete in 3s")
	}
}

// ── Listing ───────────────────────────────────────────────────────────────────

func TestListNames_ReturnsCatalogueSnapshot(t *testing.T) {
	sender := newFakeMeshSender("local")
	dir := content.NewDirectoryService(sender)

	if err := dir.Publish(context.Background(), "a", sampleDescriptor("hash-a")); err != nil {
		t.Fatalf("publish a: %v", err)
	}
	if err := dir.Publish(context.Background(), "b", sampleDescriptor("hash-b")); err != nil {
		t.Fatalf("publish b: %v", err)
	}
	if err := dir.Publish(context.Background(), "c", sampleDescriptor("hash-c")); err != nil {
		t.Fatalf("publish c: %v", err)
	}

	names, err := dir.ListNames(context.Background())
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if len(names) != 3 {
		t.Fatalf("expected 3 names, got %d (%v)", len(names), names)
	}
	got := map[string]bool{}
	for _, n := range names {
		got[n] = true
	}
	for _, want := range []string{"a", "b", "c"} {
		if !got[want] {
			t.Errorf("expected name %q in snapshot, missing", want)
		}
	}
}
