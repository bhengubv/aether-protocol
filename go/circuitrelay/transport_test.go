// SPDX-License-Identifier: MIT

package circuitrelay

import (
	"sync"
	"testing"
	"time"
)

// Behavioural proof of the native circuit-relay-v2 engine: a three-node topology
// where A and B can each reach relay R but NOT each other directly. A message from
// A must traverse the relay bridge to reach B — server off, no libp2p. Mirrors the
// C# CircuitRelayBridgeTests.

// ── in-process one-hop mesh ──────────────────────────────────────────────────

type mesh struct {
	mu    sync.Mutex
	edges map[string]bool
	links map[string]*procLink
}

func newMesh() *mesh { return &mesh{edges: map[string]bool{}, links: map[string]*procLink{}} }

func (m *mesh) connect(x, y string) {
	m.mu.Lock()
	m.edges[x+"|"+y] = true
	m.edges[y+"|"+x] = true
	m.mu.Unlock()
}

func (m *mesh) adjacent(x, y string) bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.edges[x+"|"+y]
}

func (m *mesh) link(node string) *procLink {
	m.mu.Lock()
	defer m.mu.Unlock()
	l, ok := m.links[node]
	if !ok {
		l = &procLink{mesh: m, node: node}
		m.links[node] = l
	}
	return l
}

func (m *mesh) deliver(from, to string, frame []byte) {
	if !m.adjacent(from, to) {
		return
	}
	l := m.link(to)
	go func() { // async hop, like a real transport
		l.mu.Lock()
		h := l.handler
		l.mu.Unlock()
		if h != nil {
			h(from, frame)
		}
	}()
}

type procLink struct {
	mesh    *mesh
	node    string
	mu      sync.Mutex
	handler func(from string, frame []byte)
}

func (l *procLink) SendFrame(node string, frame []byte) bool {
	if !l.mesh.adjacent(l.node, node) {
		return false
	}
	l.mesh.deliver(l.node, node, frame)
	return true
}
func (l *procLink) CanReach(node string) bool { return l.mesh.adjacent(l.node, node) }
func (l *procLink) OnFrame(h func(from string, frame []byte)) {
	l.mu.Lock()
	l.handler = h
	l.mu.Unlock()
}

// ── controllable clock ───────────────────────────────────────────────────────

type testClock struct {
	mu sync.Mutex
	t  time.Time
}

func newClock() *testClock {
	return &testClock{t: time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)}
}
func (c *testClock) now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.t
}
func (c *testClock) advance(d time.Duration) {
	c.mu.Lock()
	c.t = c.t.Add(d)
	c.mu.Unlock()
}

type recv struct {
	sender string
	data   string
}

// buildLine wires A ── R ── B with NO A-B edge. relayOpts/relayClock configure R.
func buildLine(relayOpts Options, relayClock func() time.Time) (a, r, b *Transport, bRecv, aRecv chan recv) {
	m := newMesh()
	m.connect("A", "R")
	m.connect("R", "B")
	a = NewTransport("A", m.link("A"), DefaultOptions(), nil)
	r = NewTransport("R", m.link("R"), relayOpts, relayClock)
	b = NewTransport("B", m.link("B"), DefaultOptions(), nil)
	bRecv = make(chan recv, 8)
	aRecv = make(chan recv, 8)
	b.SetOnData(func(s string, d []byte) { bRecv <- recv{s, string(d)} })
	a.SetOnData(func(s string, d []byte) { aRecv <- recv{s, string(d)} })
	return
}

func waitRecv(t *testing.T, ch chan recv, what string) recv {
	t.Helper()
	select {
	case r := <-ch:
		return r
	case <-time.After(3 * time.Second):
		t.Fatalf("timeout waiting for %s", what)
		return recv{}
	}
}

func TestEngine_Message_Traverses_Relay_No_Direct_Link(t *testing.T) {
	a, r, b, bRecv, _ := buildLine(DefaultOptions(), nil)

	if a.IsConnected("B") {
		t.Fatal("A should not be directly connected to B")
	}
	if !b.Reserve("R") {
		t.Fatal("B.Reserve(R) failed")
	}
	a.SetRoute("B", "R")

	if !a.Send("B", []byte("deadbeef")) {
		t.Fatal("A.Send returned false")
	}
	got := waitRecv(t, bRecv, "B receiving relayed message")
	if got.sender != "A" || got.data != "deadbeef" {
		t.Fatalf("B got %+v, want {A deadbeef}", got)
	}
	if r.ActiveBridgeCount() != 1 {
		t.Fatalf("relay bridge count = %d, want 1", r.ActiveBridgeCount())
	}
}

func TestEngine_Bridge_Is_Bidirectional(t *testing.T) {
	a, _, b, bRecv, aRecv := buildLine(DefaultOptions(), nil)
	if !b.Reserve("R") {
		t.Fatal("reserve failed")
	}
	a.SetRoute("B", "R")
	if !a.Send("B", []byte("hi")) {
		t.Fatal("A.Send failed")
	}
	waitRecv(t, bRecv, "B receiving")

	if !b.Send("A", []byte("reply")) {
		t.Fatal("B.Send(A) failed")
	}
	got := waitRecv(t, aRecv, "A receiving B's reply")
	if got.sender != "B" || got.data != "reply" {
		t.Fatalf("A got %+v, want {B reply}", got)
	}
}

func TestEngine_Connect_Refused_Without_Reservation(t *testing.T) {
	a, r, _, bRecv, _ := buildLine(DefaultOptions(), nil)
	a.SetRoute("B", "R") // route known, but B never reserved
	if a.Send("B", []byte("x")) {
		t.Fatal("A.Send should fail without a reservation")
	}
	select {
	case got := <-bRecv:
		t.Fatalf("B should not have received %+v", got)
	case <-time.After(200 * time.Millisecond):
	}
	if r.ActiveBridgeCount() != 0 {
		t.Fatalf("relay bridge count = %d, want 0", r.ActiveBridgeCount())
	}
}

func TestEngine_Send_Fails_Without_Route(t *testing.T) {
	a, _, b, _, _ := buildLine(DefaultOptions(), nil)
	if !b.Reserve("R") {
		t.Fatal("reserve failed")
	}
	// no SetRoute
	if a.Send("B", []byte("x")) {
		t.Fatal("A.Send should fail with no relay route known")
	}
}

func TestEngine_Relay_Enforces_Data_Budget(t *testing.T) {
	opts := DefaultOptions()
	opts.BridgeDataLimitBytes = 10
	a, r, b, bRecv, _ := buildLine(opts, nil)
	if !b.Reserve("R") {
		t.Fatal("reserve failed")
	}
	a.SetRoute("B", "R")

	if !a.Send("B", []byte{1, 2, 3, 4, 5}) { // 5 bytes, within 10
		t.Fatal("first send failed")
	}
	waitRecv(t, bRecv, "first (in-budget) message")

	a.Send("B", []byte{6, 7, 8, 9, 10, 11, 12, 13}) // 8 more -> 13 > 10 -> torn down
	select {
	case got := <-bRecv:
		t.Fatalf("over-budget message should not arrive, got %+v", got)
	case <-time.After(300 * time.Millisecond):
	}
	if r.ActiveBridgeCount() != 0 {
		t.Fatalf("bridge should be torn down on budget breach, count = %d", r.ActiveBridgeCount())
	}
}

func TestEngine_Reservation_Expiry_Refuses_Connect(t *testing.T) {
	clk := newClock()
	opts := DefaultOptions()
	opts.ReservationTTL = 30 * time.Minute
	a, _, b, bRecv, _ := buildLine(opts, clk.now)

	if !b.Reserve("R") {
		t.Fatal("reserve failed")
	}
	a.SetRoute("B", "R")

	clk.advance(31 * time.Minute) // past the reservation TTL on R's clock

	if a.Send("B", []byte("x")) {
		t.Fatal("A.Send should fail after reservation expiry")
	}
	select {
	case got := <-bRecv:
		t.Fatalf("B should not receive after expiry, got %+v", got)
	case <-time.After(200 * time.Millisecond):
	}
}
