// SPDX-License-Identifier: MIT

package circuitrelay

import (
	"sync"
	"time"

	"github.com/google/uuid"
)

// RelayLink is the one-hop link a Transport uses to exchange raw relay frames with
// directly-reachable nodes — the seam between circuit-relay-v2 (transport-agnostic)
// and whatever real transport carries a frame one hop (BLE, Wi-Fi Direct, WebRTC,
// the HTTP relay, or an in-process link in tests). Mirrors the C# IRelayLink.
type RelayLink interface {
	// SendFrame sends a raw relay frame to a node reachable in one hop. Returns true
	// if the frame was handed to that node's link.
	SendFrame(node string, frame []byte) bool
	// CanReach reports whether this node currently has a direct one-hop link to node.
	CanReach(node string) bool
	// OnFrame registers the handler invoked when a raw frame arrives from a
	// directly-reachable node (sender node UHID, frame bytes).
	OnFrame(handler func(from string, frame []byte))
}

// Options tunes a Transport (mirrors C# CircuitRelayOptions).
type Options struct {
	ReservationTTL             time.Duration
	MaxReservations            int
	MaxBridges                 int
	BridgeDataLimitBytes       int64
	BridgeDurationLimitSeconds int32
	ConnectTimeout             time.Duration
	ReserveTimeout             time.Duration
	ActAsRelay                 bool
}

// DefaultOptions returns the same defaults as the C# reference.
func DefaultOptions() Options {
	return Options{
		ReservationTTL:  30 * time.Minute,
		MaxReservations: 128,
		MaxBridges:      128,
		ConnectTimeout:  10 * time.Second,
		ReserveTimeout:  10 * time.Second,
		ActAsRelay:      true,
	}
}

type relayBridge struct {
	a, b       string
	dataBudget int64
	deadline   time.Time // zero => no duration limit
	dataUsed   int64
	open       bool
}

type activeBridge struct {
	connID uuid.UUID
	relay  string
}

// Transport is the native circuit-relay-v2 engine: any node can act as target
// (Reserve), client (Send over a known relay route), and relay (grant reservations,
// bridge CONNECT→STOP, forward DATA under a budget). Faithful port of the C#
// CircuitRelayTransportService.
type Transport struct {
	localUhid string
	link      RelayLink
	opts      Options
	now       func() time.Time

	mu                  sync.Mutex
	reservations        map[string]time.Time      // relay role: client UHID -> expiry
	bridges             map[uuid.UUID]*relayBridge // relay role
	routes              map[string]string          // client: dest -> relay
	peerBridges         map[string]activeBridge    // endpoint: peer -> bridge
	pendingConnects     map[uuid.UUID]chan Status
	pendingReservations map[string]chan Status

	onData func(sender string, data []byte)
}

// NewTransport wires a Transport onto a link. now may be nil (defaults to time.Now).
func NewTransport(localUhid string, link RelayLink, opts Options, now func() time.Time) *Transport {
	if now == nil {
		now = time.Now
	}
	t := &Transport{
		localUhid:           localUhid,
		link:                link,
		opts:                opts,
		now:                 now,
		reservations:        map[string]time.Time{},
		bridges:             map[uuid.UUID]*relayBridge{},
		routes:              map[string]string{},
		peerBridges:         map[string]activeBridge{},
		pendingConnects:     map[uuid.UUID]chan Status{},
		pendingReservations: map[string]chan Status{},
	}
	link.OnFrame(t.onFrame)
	return t
}

// SetOnData registers the callback invoked when tunnelled data is delivered to this
// node as an endpoint (sender UHID, payload).
func (t *Transport) SetOnData(cb func(sender string, data []byte)) { t.onData = cb }

// SetRoute records that dest is reachable via relay (in production, from the
// directory / reservation gossip; tests set it directly).
func (t *Transport) SetRoute(dest, relay string) {
	t.mu.Lock()
	t.routes[dest] = relay
	t.mu.Unlock()
}

// ActiveBridgeCount / ActiveReservationCount are diagnostics for tests.
func (t *Transport) ActiveBridgeCount() int {
	t.mu.Lock()
	defer t.mu.Unlock()
	return len(t.bridges)
}
func (t *Transport) ActiveReservationCount() int {
	t.mu.Lock()
	defer t.mu.Unlock()
	return len(t.reservations)
}

// IsConnected reports whether a relay bridge to peer has been established.
func (t *Transport) IsConnected(peer string) bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	_, ok := t.peerBridges[peer]
	return ok
}

// Reserve reserves capacity on relay so peers can reach this node through it.
func (t *Transport) Reserve(relay string) bool {
	if !t.link.CanReach(relay) {
		return false
	}
	ch := make(chan Status, 1)
	t.mu.Lock()
	t.pendingReservations[relay] = ch
	t.mu.Unlock()
	defer func() {
		t.mu.Lock()
		delete(t.pendingReservations, relay)
		t.mu.Unlock()
	}()

	f := &RelayFrame{Type: MsgReserve, SourceUhid: t.localUhid, RelayUhid: relay}
	b, err := Serialize(f)
	if err != nil {
		return false
	}
	t.link.SendFrame(relay, b)
	return t.await(ch, t.opts.ReserveTimeout) == StatusOk
}

// Send delivers data to peer, establishing a relay bridge first if needed.
func (t *Transport) Send(peer string, data []byte) bool {
	t.mu.Lock()
	ab, ok := t.peerBridges[peer]
	t.mu.Unlock()
	if ok {
		return t.sendData(ab, peer, data)
	}

	t.mu.Lock()
	relay, hasRoute := t.routes[peer]
	t.mu.Unlock()
	if !hasRoute || !t.link.CanReach(relay) {
		return false
	}
	if t.connect(peer, relay) != StatusOk {
		return false
	}
	t.mu.Lock()
	ab, ok = t.peerBridges[peer]
	t.mu.Unlock()
	return ok && t.sendData(ab, peer, data)
}

func (t *Transport) connect(dest, relay string) Status {
	connID := uuid.New()
	ch := make(chan Status, 1)
	t.mu.Lock()
	t.pendingConnects[connID] = ch
	t.mu.Unlock()
	defer func() {
		t.mu.Lock()
		delete(t.pendingConnects, connID)
		t.mu.Unlock()
	}()

	f := &RelayFrame{
		Type:            MsgConnect,
		SourceUhid:      t.localUhid,
		DestinationUhid: dest,
		RelayUhid:       relay,
		ConnectionID:    connID.String(),
	}
	b, err := Serialize(f)
	if err != nil {
		return StatusConnectionFailed
	}
	if !t.link.SendFrame(relay, b) {
		return StatusConnectionFailed
	}
	return t.await(ch, t.opts.ConnectTimeout)
}

func (t *Transport) await(ch chan Status, timeout time.Duration) Status {
	select {
	case s := <-ch:
		return s
	case <-time.After(timeout):
		return StatusConnectionFailed
	}
}

func (t *Transport) sendData(ab activeBridge, peer string, data []byte) bool {
	f := &RelayFrame{
		Type:            MsgData,
		SourceUhid:      t.localUhid,
		DestinationUhid: peer,
		RelayUhid:       ab.relay,
		ConnectionID:    ab.connID.String(),
		Payload:         data,
	}
	b, err := Serialize(f)
	if err != nil {
		return false
	}
	return t.link.SendFrame(ab.relay, b)
}

// ── inbound dispatch ────────────────────────────────────────────────────────

func (t *Transport) onFrame(from string, frame []byte) {
	f, err := Deserialize(frame)
	if err != nil {
		return // drop malformed
	}
	switch f.Type {
	case MsgReserve:
		t.handleReserve(from, f)
	case MsgReserveResponse:
		t.handleReserveResponse(from, f)
	case MsgConnect:
		t.handleConnect(from, f)
	case MsgStop:
		t.handleStop(from, f)
	case MsgStopResponse:
		t.handleStopResponse(from, f)
	case MsgConnectResponse:
		t.handleConnectResponse(from, f)
	case MsgData:
		t.handleData(from, f)
	}
}

// Relay: grant/refuse a reservation.
func (t *Transport) handleReserve(from string, f *RelayFrame) {
	t.mu.Lock()
	if !t.opts.ActAsRelay || len(t.reservations) >= t.opts.MaxReservations {
		t.mu.Unlock()
		t.send(from, &RelayFrame{Type: MsgReserveResponse, SourceUhid: f.SourceUhid, RelayUhid: t.localUhid, Status: StatusReservationRefused})
		return
	}
	expiry := t.now().Add(t.opts.ReservationTTL)
	t.reservations[f.SourceUhid] = expiry
	t.mu.Unlock()
	t.send(from, &RelayFrame{
		Type: MsgReserveResponse, SourceUhid: f.SourceUhid, RelayUhid: t.localUhid,
		Status: StatusOk, ReservationExpiresAtMs: expiry.UnixMilli(),
	})
}

// Client: reservation confirmed/denied.
func (t *Transport) handleReserveResponse(from string, f *RelayFrame) {
	t.mu.Lock()
	ch, ok := t.pendingReservations[from]
	t.mu.Unlock()
	if ok {
		trySend(ch, f.Status)
	}
}

// Relay: A wants B. Validate B's reservation + reachability, open a STOP to B.
func (t *Transport) handleConnect(from string, f *RelayFrame) {
	a, b := f.SourceUhid, f.DestinationUhid
	connID, err := uuid.Parse(f.ConnectionID)
	if err != nil {
		return
	}
	if !t.opts.ActAsRelay {
		t.replyConnect(a, f, StatusConnectionFailed)
		return
	}
	t.mu.Lock()
	exp, has := t.reservations[b]
	if !has || !t.now().Before(exp) {
		delete(t.reservations, b)
		t.mu.Unlock()
		t.replyConnect(a, f, StatusNoReservation)
		return
	}
	if !t.link.CanReach(b) {
		t.mu.Unlock()
		t.replyConnect(a, f, StatusConnectionFailed)
		return
	}
	if len(t.bridges) >= t.opts.MaxBridges {
		t.mu.Unlock()
		t.replyConnect(a, f, StatusResourceLimitExceeded)
		return
	}
	var deadline time.Time
	if t.opts.BridgeDurationLimitSeconds > 0 {
		deadline = t.now().Add(time.Duration(t.opts.BridgeDurationLimitSeconds) * time.Second)
	}
	t.bridges[connID] = &relayBridge{a: a, b: b, dataBudget: t.opts.BridgeDataLimitBytes, deadline: deadline}
	t.mu.Unlock()

	t.send(b, &RelayFrame{
		Type: MsgStop, SourceUhid: a, DestinationUhid: b, RelayUhid: t.localUhid,
		ConnectionID: f.ConnectionID, LimitDataBytes: t.opts.BridgeDataLimitBytes,
		LimitDurationSeconds: t.opts.BridgeDurationLimitSeconds,
	})
}

// Target: relay says A wants us. Accept and record a return route to A.
func (t *Transport) handleStop(from string, f *RelayFrame) {
	connID, err := uuid.Parse(f.ConnectionID)
	if err != nil {
		return
	}
	t.mu.Lock()
	t.peerBridges[f.SourceUhid] = activeBridge{connID: connID, relay: from}
	t.mu.Unlock()
	t.send(from, &RelayFrame{
		Type: MsgStopResponse, SourceUhid: f.SourceUhid, DestinationUhid: t.localUhid,
		RelayUhid: from, ConnectionID: f.ConnectionID, Status: StatusOk,
	})
}

// Relay: target accepted/refused. Finalise the bridge and answer the client.
func (t *Transport) handleStopResponse(from string, f *RelayFrame) {
	connID, err := uuid.Parse(f.ConnectionID)
	if err != nil {
		return
	}
	t.mu.Lock()
	br, ok := t.bridges[connID]
	if !ok {
		t.mu.Unlock()
		return
	}
	if f.Status != StatusOk {
		aUhid := br.a
		delete(t.bridges, connID)
		t.mu.Unlock()
		t.replyConnect(aUhid, f, StatusConnectionFailed)
		return
	}
	br.open = true
	aUhid, bUhid, budget := br.a, br.b, br.dataBudget
	t.mu.Unlock()

	t.send(aUhid, &RelayFrame{
		Type: MsgConnectResponse, SourceUhid: aUhid, DestinationUhid: bUhid, RelayUhid: t.localUhid,
		ConnectionID: f.ConnectionID, Status: StatusOk, LimitDataBytes: budget,
	})
}

// Client: bridge established/refused.
func (t *Transport) handleConnectResponse(from string, f *RelayFrame) {
	connID, err := uuid.Parse(f.ConnectionID)
	if err != nil {
		return
	}
	if f.Status == StatusOk {
		t.mu.Lock()
		t.peerBridges[f.DestinationUhid] = activeBridge{connID: connID, relay: from}
		t.mu.Unlock()
	}
	t.mu.Lock()
	ch, ok := t.pendingConnects[connID]
	t.mu.Unlock()
	if ok {
		trySend(ch, f.Status)
	}
}

// Data: endpoint delivery, or relay forward (under budget).
func (t *Transport) handleData(from string, f *RelayFrame) {
	if f.DestinationUhid == t.localUhid {
		if t.onData != nil {
			t.onData(f.SourceUhid, f.Payload)
		}
		return
	}
	connID, err := uuid.Parse(f.ConnectionID)
	if err != nil {
		return
	}
	t.mu.Lock()
	br, ok := t.bridges[connID]
	if !ok || !br.open || (from != br.a && from != br.b) {
		t.mu.Unlock()
		return
	}
	if !br.deadline.IsZero() && !t.now().Before(br.deadline) {
		delete(t.bridges, connID)
		t.mu.Unlock()
		return
	}
	br.dataUsed += int64(len(f.Payload))
	over := br.dataBudget > 0 && br.dataUsed > br.dataBudget
	if over {
		delete(t.bridges, connID)
		t.mu.Unlock()
		return
	}
	t.mu.Unlock()

	if b, err := Serialize(f); err == nil {
		t.link.SendFrame(f.DestinationUhid, b) // forward unchanged to the other endpoint (its dst)
	}
}

// ── helpers ─────────────────────────────────────────────────────────────────

func (t *Transport) send(to string, f *RelayFrame) {
	if b, err := Serialize(f); err == nil {
		t.link.SendFrame(to, b)
	}
}

func (t *Transport) replyConnect(client string, connect *RelayFrame, status Status) {
	t.send(client, &RelayFrame{
		Type: MsgConnectResponse, SourceUhid: connect.SourceUhid, DestinationUhid: connect.DestinationUhid,
		RelayUhid: t.localUhid, ConnectionID: connect.ConnectionID, Status: status,
	})
}

func trySend(ch chan Status, s Status) {
	select {
	case ch <- s:
	default:
	}
}
