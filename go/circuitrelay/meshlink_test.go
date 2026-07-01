// SPDX-License-Identifier: MIT

package circuitrelay

import (
	"bytes"
	"sync"
	"testing"
	"time"

	"github.com/bhengubv/aether-protocol/go/protocol"
)

// meshHub is an in-process mesh whose adjacency is A-R-B with NO direct A-B edge; it
// routes each MeshPacket one hop to the destination node's link. It stands in for the
// real radios (the sendOneHop func in production).
type meshHub struct {
	mu    sync.Mutex
	links map[string]*MeshRelayLink
	edges map[string]bool
}

func newMeshHub() *meshHub {
	return &meshHub{links: map[string]*MeshRelayLink{}, edges: map[string]bool{}}
}
func (h *meshHub) connect(x, y string) { h.edges[x+"|"+y] = true; h.edges[y+"|"+x] = true }
func (h *meshHub) adjacent(x, y string) bool { return h.edges[x+"|"+y] }
func (h *meshHub) register(node string, l *MeshRelayLink) {
	h.mu.Lock()
	h.links[node] = l
	h.mu.Unlock()
}
func (h *meshHub) sendFrom(node string) func(*protocol.MeshPacket) bool {
	return func(pkt *protocol.MeshPacket) bool {
		if !h.adjacent(node, pkt.DestinationUhid) {
			return false
		}
		h.mu.Lock()
		l, ok := h.links[pkt.DestinationUhid]
		h.mu.Unlock()
		if ok {
			go l.HandleIncomingPacket(pkt) // async one-hop delivery
		}
		return true
	}
}
func (h *meshHub) canReachFrom(node string) func(string) bool {
	return func(other string) bool { return h.adjacent(node, other) }
}

type recvMsg struct {
	sender string
	data   []byte
}

// TestRelayWorksAsMeshTransport proves the engine relays A->B through R over real
// MeshPacket frames (type CircuitRelayControl) with NO direct A-B link, surfacing at B
// via the transport's onData callback. Mirrors the C# CircuitRelayMeshIntegrationTests.
func TestRelayWorksAsMeshTransport(t *testing.T) {
	hub := newMeshHub()
	hub.connect("A", "R")
	hub.connect("R", "B") // deliberately NO A-B edge

	aL := NewMeshRelayLink("A", hub.sendFrom("A"), hub.canReachFrom("A"))
	rL := NewMeshRelayLink("R", hub.sendFrom("R"), hub.canReachFrom("R"))
	bL := NewMeshRelayLink("B", hub.sendFrom("B"), hub.canReachFrom("B"))
	hub.register("A", aL)
	hub.register("R", rL)
	hub.register("B", bL)

	aT := NewTransport("A", aL, DefaultOptions(), nil)
	rT := NewTransport("R", rL, DefaultOptions(), nil)
	bT := NewTransport("B", bL, DefaultOptions(), nil)

	recv := make(chan recvMsg, 1)
	bT.SetOnData(func(sender string, data []byte) { recv <- recvMsg{sender, data} })

	if aT.IsConnected("B") {
		t.Fatal("A should have no direct path to B")
	}
	if !bT.Reserve("R") {
		t.Fatal("B failed to reserve on R")
	}
	aT.SetRoute("B", "R")

	payload := []byte{0xDE, 0xAD, 0xBE, 0xEF}
	if !aT.Send("B", payload) {
		t.Fatal("A.Send to B failed")
	}

	select {
	case got := <-recv:
		if got.sender != "A" {
			t.Fatalf("sender = %q, want A", got.sender)
		}
		if !bytes.Equal(got.data, payload) {
			t.Fatalf("data = %v, want %v", got.data, payload)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("B never received the relayed message via the mesh link")
	}
	if n := rT.ActiveBridgeCount(); n != 1 {
		t.Fatalf("relay bridge count = %d, want 1", n)
	}
}
