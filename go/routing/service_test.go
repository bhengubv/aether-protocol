// SPDX-License-Identifier: MIT

package routing

import (
	"context"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/thegeeknetwork/aether-protocol-go/constants"
	"github.com/thegeeknetwork/aether-protocol-go/models"
	"github.com/thegeeknetwork/aether-protocol-go/protocol"
)

// FakeMeshSender is a test double that records every send and broadcast.
type FakeMeshSender struct {
	mu              sync.Mutex
	uhid            string
	geohash         string
	peers           []models.PeerInfo
	failPeers       map[string]bool
	Unicasts        []UnicastRecord
	Broadcasts      []*protocol.MeshPacket
}

type UnicastRecord struct {
	Packet      *protocol.MeshPacket
	NextHopUhid string
}

func NewFakeSender(uhid string) *FakeMeshSender {
	return &FakeMeshSender{uhid: uhid, failPeers: make(map[string]bool)}
}

func (f *FakeMeshSender) LocalUhid() string                        { return f.uhid }
func (f *FakeMeshSender) LocalGeohash() string                     { return f.geohash }
func (f *FakeMeshSender) ConnectedPeers() []models.PeerInfo        { return append([]models.PeerInfo{}, f.peers...) }
func (f *FakeMeshSender) AddPeer(p models.PeerInfo)                { f.peers = append(f.peers, p) }
func (f *FakeMeshSender) FailSendsTo(uhid string)                  { f.failPeers[uhid] = true }
func (f *FakeMeshSender) Reset() {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.Unicasts = nil
	f.Broadcasts = nil
}

func (f *FakeMeshSender) Send(ctx context.Context, packet *protocol.MeshPacket, nextHopUhid string) (bool, error) {
	if f.failPeers[nextHopUhid] {
		return false, nil
	}
	f.mu.Lock()
	defer f.mu.Unlock()
	f.Unicasts = append(f.Unicasts, UnicastRecord{Packet: clonePkt(packet), NextHopUhid: nextHopUhid})
	return true, nil
}

func (f *FakeMeshSender) Broadcast(ctx context.Context, packet *protocol.MeshPacket) (int, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.Broadcasts = append(f.Broadcasts, clonePkt(packet))
	return len(f.peers), nil
}

func clonePkt(p *protocol.MeshPacket) *protocol.MeshPacket {
	if p == nil {
		return nil
	}
	c := *p
	c.Payload = append([]byte(nil), p.Payload...)
	c.Signature = append([]byte(nil), p.Signature...)
	c.PacketNonce = append([]byte(nil), p.PacketNonce...)
	return &c
}

func newRreq(source, dest string, ttl int32) *protocol.MeshPacket {
	pkt := protocol.NewMeshPacket()
	pkt.ID = uuid.New()
	pkt.Type = protocol.RouteRequest
	pkt.SourceUhid = source
	pkt.DestinationUhid = dest
	pkt.Ttl = ttl
	return pkt
}

func newRrep(source, dest string, ttl int32) *protocol.MeshPacket {
	pkt := protocol.NewMeshPacket()
	pkt.ID = uuid.New()
	pkt.Type = protocol.RouteReply
	pkt.SourceUhid = source
	pkt.DestinationUhid = dest
	pkt.Ttl = ttl
	return pkt
}

func newSvc(t *testing.T, uhid string) (*Service, *FakeMeshSender, RouteStore) {
	t.Helper()
	sender := NewFakeSender(uhid)
	store := NewInMemoryRouteStore()
	svc := NewService(sender, store, nil, nil)
	return svc, sender, store
}

// ─── HandleRouteRequest ──────────────────────────────────────

func TestHandleRouteRequest_DropsDuplicateById(t *testing.T) {
	svc, sender, _ := newSvc(t, "local")
	rreq := newRreq("alice", "bob", constants.DefaultTtl)

	if err := svc.HandleRouteRequest(context.Background(), rreq); err != nil {
		t.Fatalf("first call: %v", err)
	}
	sender.Reset()
	if err := svc.HandleRouteRequest(context.Background(), rreq); err != nil {
		t.Fatalf("second call: %v", err)
	}
	if len(sender.Broadcasts) != 0 || len(sender.Unicasts) != 0 {
		t.Fatalf("expected dedup: got %d broadcasts, %d unicasts", len(sender.Broadcasts), len(sender.Unicasts))
	}
}

func TestHandleRouteRequest_IgnoresSelfOriginated(t *testing.T) {
	svc, sender, store := newSvc(t, "local")
	rreq := newRreq("local", "bob", constants.DefaultTtl)

	if err := svc.HandleRouteRequest(context.Background(), rreq); err != nil {
		t.Fatalf("err: %v", err)
	}
	if len(sender.Broadcasts) != 0 || len(sender.Unicasts) != 0 {
		t.Fatalf("expected no traffic")
	}
	all, _ := store.GetAll(context.Background())
	if len(all) != 0 {
		t.Fatalf("expected no routes installed")
	}
}

func TestHandleRouteRequest_InstallsReverseRouteToSource(t *testing.T) {
	svc, _, store := newSvc(t, "local")
	rreq := newRreq("alice", "bob", constants.DefaultTtl)

	if err := svc.HandleRouteRequest(context.Background(), rreq); err != nil {
		t.Fatalf("err: %v", err)
	}
	r, _ := store.Get(context.Background(), "alice")
	if r == nil || r.NextHop != "alice" {
		t.Fatalf("reverse route not installed: %+v", r)
	}
	if r.IsStale() {
		t.Fatalf("reverse route already stale")
	}
}

func TestHandleRouteRequest_AsDestination_SendsRrepBack(t *testing.T) {
	svc, sender, _ := newSvc(t, "local")
	rreq := newRreq("alice", "local", constants.DefaultTtl)

	if err := svc.HandleRouteRequest(context.Background(), rreq); err != nil {
		t.Fatalf("err: %v", err)
	}
	if len(sender.Unicasts) != 1 {
		t.Fatalf("expected 1 unicast RREP, got %d", len(sender.Unicasts))
	}
	rec := sender.Unicasts[0]
	if rec.Packet.Type != protocol.RouteReply {
		t.Fatalf("expected RouteReply, got %v", rec.Packet.Type)
	}
	if rec.NextHopUhid != "alice" {
		t.Fatalf("expected next hop alice, got %s", rec.NextHopUhid)
	}
}

func TestHandleRouteRequest_WithCachedRoute_RepliesOnBehalf(t *testing.T) {
	svc, sender, store := newSvc(t, "local")
	_ = store.Save(context.Background(), &models.RouteEntry{
		DestinationUhid: "carol",
		NextHop:         "carol",
		HopCount:        1,
		ExpiresAt:       time.Now().Add(5 * time.Minute),
	})
	if _, err := svc.FindRoute(context.Background(), "carol"); err != nil {
		t.Fatalf("find: %v", err)
	}
	sender.Reset()

	rreq := newRreq("alice", "carol", constants.DefaultTtl)
	if err := svc.HandleRouteRequest(context.Background(), rreq); err != nil {
		t.Fatalf("err: %v", err)
	}

	var rrep *protocol.MeshPacket
	for _, u := range sender.Unicasts {
		if u.Packet.Type == protocol.RouteReply {
			rrep = u.Packet
			break
		}
	}
	if rrep == nil {
		for _, b := range sender.Broadcasts {
			if b.Type == protocol.RouteReply {
				rrep = b
				break
			}
		}
	}
	if rrep == nil {
		t.Fatalf("expected an RREP to be emitted")
	}
	if rrep.SourceUhid != "carol" {
		t.Fatalf("expected RREP source = carol, got %s", rrep.SourceUhid)
	}
}

func TestHandleRouteRequest_ForwardsWhenTtlAllows(t *testing.T) {
	svc, sender, _ := newSvc(t, "local")
	rreq := newRreq("alice", "carol", 5)

	if err := svc.HandleRouteRequest(context.Background(), rreq); err != nil {
		t.Fatalf("err: %v", err)
	}
	if len(sender.Broadcasts) != 1 {
		t.Fatalf("expected 1 broadcast, got %d", len(sender.Broadcasts))
	}
	if sender.Broadcasts[0].Ttl != 4 {
		t.Fatalf("expected ttl=4, got %d", sender.Broadcasts[0].Ttl)
	}
}

func TestHandleRouteRequest_DropsWhenTtlExhausted(t *testing.T) {
	svc, sender, _ := newSvc(t, "local")
	rreq := newRreq("alice", "carol", 1)

	if err := svc.HandleRouteRequest(context.Background(), rreq); err != nil {
		t.Fatalf("err: %v", err)
	}
	if len(sender.Broadcasts) != 0 {
		t.Fatalf("expected no broadcast, got %d", len(sender.Broadcasts))
	}
}

// ─── HandleRouteReply ──────────────────────────────────────

func TestHandleRouteReply_InstallsForwardRoute(t *testing.T) {
	svc, _, store := newSvc(t, "local")
	rrep := newRrep("carol", "local", constants.DefaultTtl)

	if err := svc.HandleRouteReply(context.Background(), rrep); err != nil {
		t.Fatalf("err: %v", err)
	}
	r, _ := store.Get(context.Background(), "carol")
	if r == nil || r.NextHop != "carol" {
		t.Fatalf("forward route not installed: %+v", r)
	}
}

func TestHandleRouteReply_RejectsWhenVerifierFails(t *testing.T) {
	sender := NewFakeSender("local")
	store := NewInMemoryRouteStore()
	svc := NewService(sender, store, rejectingVerifier{}, nil)
	rrep := newRrep("carol", "local", constants.DefaultTtl)

	if err := svc.HandleRouteReply(context.Background(), rrep); err != nil {
		t.Fatalf("err: %v", err)
	}
	r, _ := store.Get(context.Background(), "carol")
	if r != nil {
		t.Fatalf("expected no route installed when verifier rejects")
	}
}

type rejectingVerifier struct{}

func (rejectingVerifier) Verify(ctx context.Context, rrep *protocol.MeshPacket) (bool, error) {
	return false, nil
}

func TestHandleRouteReply_ForwardsTowardOriginalRequester(t *testing.T) {
	svc, sender, store := newSvc(t, "local")
	_ = store.Save(context.Background(), &models.RouteEntry{
		DestinationUhid: "alice",
		NextHop:         "bob",
		HopCount:        2,
		ExpiresAt:       time.Now().Add(5 * time.Minute),
	})
	if _, err := svc.FindRoute(context.Background(), "alice"); err != nil {
		t.Fatalf("find: %v", err)
	}
	sender.Reset()

	rrep := newRrep("carol", "alice", 4)
	if err := svc.HandleRouteReply(context.Background(), rrep); err != nil {
		t.Fatalf("err: %v", err)
	}

	var found *UnicastRecord
	for i := range sender.Unicasts {
		if sender.Unicasts[i].Packet.Type == protocol.RouteReply &&
			sender.Unicasts[i].NextHopUhid == "bob" {
			found = &sender.Unicasts[i]
			break
		}
	}
	if found == nil {
		t.Fatalf("expected RREP forwarded via bob")
	}
	if found.Packet.Ttl != 3 {
		t.Fatalf("expected ttl=3 after decrement, got %d", found.Packet.Ttl)
	}
}

// ─── FindRoute / GetCachedRoute / Prune ─────────────────────

func TestFindRoute_ReturnsCachedRouteWithoutBroadcasting(t *testing.T) {
	svc, sender, store := newSvc(t, "local")
	_ = store.Save(context.Background(), &models.RouteEntry{
		DestinationUhid: "bob",
		NextHop:         "bob",
		HopCount:        1,
		ExpiresAt:       time.Now().Add(5 * time.Minute),
	})

	r, err := svc.FindRoute(context.Background(), "bob")
	if err != nil {
		t.Fatalf("err: %v", err)
	}
	if r == nil || r.NextHop != "bob" {
		t.Fatalf("unexpected route: %+v", r)
	}
	if len(sender.Broadcasts) != 0 {
		t.Fatalf("should not broadcast for cached route")
	}
}

func TestFindRoute_WithNoPeers_ReturnsNilImmediately(t *testing.T) {
	svc, _, _ := newSvc(t, "local")

	r, err := svc.FindRoute(context.Background(), "bob")
	if err != nil {
		t.Fatalf("err: %v", err)
	}
	if r != nil {
		t.Fatalf("expected nil route, got %+v", r)
	}
}

func TestPrune_RemovesExpiredRoutes(t *testing.T) {
	svc, _, store := newSvc(t, "local")
	_ = store.Save(context.Background(), &models.RouteEntry{
		DestinationUhid: "stale",
		NextHop:         "stale",
		ExpiresAt:       time.Now().Add(-1 * time.Minute),
	})
	_ = store.Save(context.Background(), &models.RouteEntry{
		DestinationUhid: "fresh",
		NextHop:         "fresh",
		ExpiresAt:       time.Now().Add(5 * time.Minute),
	})
	if _, err := svc.FindRoute(context.Background(), "fresh"); err != nil {
		t.Fatalf("find: %v", err)
	}

	if err := svc.Prune(context.Background()); err != nil {
		t.Fatalf("prune: %v", err)
	}

	stale, _ := store.Get(context.Background(), "stale")
	fresh, _ := store.Get(context.Background(), "fresh")
	if stale != nil {
		t.Fatalf("expected stale evicted")
	}
	if fresh == nil {
		t.Fatalf("expected fresh retained")
	}
}
