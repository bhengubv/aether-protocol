// SPDX-License-Identifier: MIT

package webrtc

import (
	"fmt"
	"sync"
)

// SignalType is the kind of WebRTC signalling message exchanged while a direct link is set up.
type SignalType int

const (
	// SignalOffer is an SDP offer from the initiating peer.
	SignalOffer SignalType = iota
	// SignalAnswer is an SDP answer from the responding peer.
	SignalAnswer
	// SignalCandidate is a trickled ICE candidate.
	SignalCandidate
)

// Signal is a single WebRTC signalling message — an SDP offer/answer or an ICE candidate that two
// peers must exchange before a direct data channel can open. It is carried by a Signaling channel
// (the relay, the mesh, or the in-process bus), never a central signalling server.
type Signal struct {
	FromUhid      string     `json:"from"`
	ToUhid        string     `json:"to"`
	Type          SignalType `json:"type"`
	SDP           string     `json:"sdp,omitempty"`
	Candidate     string     `json:"candidate,omitempty"`
	SDPMid        string     `json:"mid,omitempty"`
	SDPMLineIndex uint16     `json:"mline,omitempty"`
}

// Signaling carries WebRTC SDP/ICE signalling between two peers by UHID.
type Signaling interface {
	// SendSignal delivers a signalling message to its addressee.
	SendSignal(peerUhid string, s Signal) error
	// OnSignal registers the handler invoked for signals addressed to the local node.
	OnSignal(handler func(s Signal))
}

// InMemorySignalingBus routes signals between endpoints by UHID, in process, with no server.
// It is the reference Signaling implementation — backing same-process simulations and tests. Each
// endpoint delivers inbound signals in send order on its own goroutine, so a signal never re-enters
// the sender's call stack.
type InMemorySignalingBus struct {
	mu        sync.Mutex
	endpoints map[string]*busEndpoint
}

// NewInMemorySignalingBus creates an empty bus.
func NewInMemorySignalingBus() *InMemorySignalingBus {
	return &InMemorySignalingBus{endpoints: make(map[string]*busEndpoint)}
}

// Endpoint returns (creating once) the Signaling endpoint for uhid.
func (b *InMemorySignalingBus) Endpoint(uhid string) Signaling {
	b.mu.Lock()
	defer b.mu.Unlock()
	if e, ok := b.endpoints[uhid]; ok {
		return e
	}
	e := newBusEndpoint(b)
	b.endpoints[uhid] = e
	return e
}

// Close stops all endpoint pumps.
func (b *InMemorySignalingBus) Close() {
	b.mu.Lock()
	defer b.mu.Unlock()
	for _, e := range b.endpoints {
		e.close()
	}
	b.endpoints = make(map[string]*busEndpoint)
}

func (b *InMemorySignalingBus) route(s Signal) error {
	b.mu.Lock()
	target, ok := b.endpoints[s.ToUhid]
	b.mu.Unlock()
	if !ok {
		return fmt.Errorf("webrtc signaling: no endpoint for %q", s.ToUhid)
	}
	target.deliver(s)
	return nil
}

type busEndpoint struct {
	bus    *InMemorySignalingBus
	queue  chan Signal
	mu     sync.Mutex
	h      func(Signal)
	closed bool
}

func newBusEndpoint(bus *InMemorySignalingBus) *busEndpoint {
	e := &busEndpoint{bus: bus, queue: make(chan Signal, 256)}
	go e.pump()
	return e
}

func (e *busEndpoint) SendSignal(_ string, s Signal) error {
	return e.bus.route(s)
}

func (e *busEndpoint) OnSignal(handler func(Signal)) {
	e.mu.Lock()
	e.h = handler
	e.mu.Unlock()
}

func (e *busEndpoint) deliver(s Signal) {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.closed {
		return
	}
	select {
	case e.queue <- s:
	default:
		// queue full — drop; ICE re-gathers on reconnect (best-effort signalling)
	}
}

func (e *busEndpoint) pump() {
	for s := range e.queue {
		e.mu.Lock()
		h := e.h
		e.mu.Unlock()
		if h != nil {
			h(s)
		}
	}
}

func (e *busEndpoint) close() {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.closed {
		return
	}
	e.closed = true
	close(e.queue)
}
