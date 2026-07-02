// SPDX-License-Identifier: MIT

// Package webrtc is a direct peer-to-peer transport for AetherNet over a WebRTC data channel
// (pion, pure Go). NAT traversal is handled by ICE/STUN; the SDP/ICE handshake rides an injected
// Signaling channel, so no central signalling server is required. It gives Go its first real,
// internet-capable transport (the others are in-process simulations).
package webrtc

import (
	"context"
	"fmt"
	"sync"
	"time"

	pion "github.com/pion/webrtc/v4"

	"github.com/bhengubv/aether-protocol/go/transport"
)

const (
	dataChannelLabel = "aether"
	connectTimeout   = 20 * time.Second
)

// DefaultICEServers is the serverless default: NO ICE servers, so a node never contacts a
// STUN/TURN server. Direct links form on the same LAN or when a peer has a public address; for
// NAT traversal without a server, route through the circuit-relay-v2 transport (peers relay for
// peers). Callers opt into STUN/TURN by passing an explicit list.
func DefaultICEServers() []pion.ICEServer {
	return []pion.ICEServer{}
}

// WebRtcTransport implements transport.TransportService over a WebRTC data channel.
type WebRtcTransport struct {
	localUhid  string
	signaling  Signaling
	iceServers []pion.ICEServer
	metrics    *transport.PerTransportMetrics

	mu     sync.Mutex
	onData func(peerUhid string, data []byte)
	peers  map[string]*peerLink
	closed bool
}

// compile-time proof the transport satisfies the contract.
var _ transport.TransportService = (*WebRtcTransport)(nil)

// NewWebRtcTransport builds a transport for localUhid. With nil iceServers it uses the serverless
// default of NO ICE servers (host-candidate-only ICE) — it never contacts a STUN/TURN server, and
// links form on the same LAN or when a peer has a public address. For NAT traversal without a
// server, route through the circuit-relay-v2 transport (peers relay for peers). Pass an explicit
// list to opt into STUN/TURN; an explicit empty list keeps host-candidate-only ICE.
func NewWebRtcTransport(localUhid string, signaling Signaling, iceServers []pion.ICEServer) (*WebRtcTransport, error) {
	if localUhid == "" {
		return nil, fmt.Errorf("webrtc: localUhid required")
	}
	if signaling == nil {
		return nil, fmt.Errorf("webrtc: signaling required")
	}
	servers := iceServers
	if servers == nil {
		servers = DefaultICEServers()
	}
	t := &WebRtcTransport{
		localUhid:  localUhid,
		signaling:  signaling,
		iceServers: servers,
		metrics:    transport.NewPerTransportMetrics(),
		peers:      make(map[string]*peerLink),
	}
	signaling.OnSignal(t.handleSignal)
	return t, nil
}

// OnDataReceived registers the handler for inbound bytes (the receive surface, beyond the interface).
func (t *WebRtcTransport) OnDataReceived(h func(peerUhid string, data []byte)) {
	t.mu.Lock()
	t.onData = h
	t.mu.Unlock()
}

// --- transport.TransportService ---

func (t *WebRtcTransport) Name() string { return "WebRTC P2P" }

func (t *WebRtcTransport) IsAvailable() bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	return !t.closed
}

func (t *WebRtcTransport) MaxBandwidthBps() int64    { return 100_000_000 }
func (t *WebRtcTransport) MaxRangeMeters() int32     { return 0 } // internet — unbounded
func (t *WebRtcTransport) PowerCostRelative() int32  { return 5 } // dearer than local radio on the 1-10 scale
func (t *WebRtcTransport) MaxConcurrentPeers() int32 { return 256 }

func (t *WebRtcTransport) Metrics() *transport.PerTransportMetrics { return t.metrics }

func (t *WebRtcTransport) SendAsync(_ context.Context, peerUhid string, data []byte) (bool, error) {
	if peerUhid == "" {
		return false, fmt.Errorf("peer UHID cannot be empty")
	}
	if len(data) == 0 {
		return false, fmt.Errorf("data cannot be empty")
	}
	link, err := t.getOrCreateLink(peerUhid, true)
	if err != nil {
		return false, err
	}
	ok, err := link.send(data)
	t.metrics.RecordSample(0, ok, int64(len(data)))
	return ok, err
}

func (t *WebRtcTransport) SendStreamAsync(ctx context.Context, peerUhid string, data []byte) (bool, error) {
	return t.SendAsync(ctx, peerUhid, data)
}

func (t *WebRtcTransport) IsConnected(peerUhid string) bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	link, ok := t.peers[peerUhid]
	return ok && link.isOpen()
}

// Close tears down all peer connections.
func (t *WebRtcTransport) Close() error {
	t.mu.Lock()
	t.closed = true
	peers := t.peers
	t.peers = make(map[string]*peerLink)
	t.mu.Unlock()
	for _, l := range peers {
		l.close()
	}
	return nil
}

// --- signalling inbound ---

func (t *WebRtcTransport) handleSignal(s Signal) {
	if s.ToUhid != t.localUhid {
		return
	}
	switch s.Type {
	case SignalOffer:
		if link, err := t.getOrCreateLink(s.FromUhid, false); err == nil {
			link.acceptOffer(s.SDP)
		}
	case SignalAnswer:
		t.mu.Lock()
		link, ok := t.peers[s.FromUhid]
		t.mu.Unlock()
		if ok {
			link.acceptAnswer(s.SDP)
		}
	case SignalCandidate:
		t.mu.Lock()
		link, ok := t.peers[s.FromUhid]
		t.mu.Unlock()
		if ok {
			link.addRemoteCandidate(s)
		}
	}
}

func (t *WebRtcTransport) getOrCreateLink(peerUhid string, initiator bool) (*peerLink, error) {
	t.mu.Lock()
	if t.closed {
		t.mu.Unlock()
		return nil, fmt.Errorf("webrtc: transport closed")
	}
	if link, ok := t.peers[peerUhid]; ok && !link.isClosed() {
		t.mu.Unlock()
		if initiator {
			link.waitOpen(connectTimeout)
		}
		return link, nil
	}
	onData := t.onData
	link, err := newPeerLink(t.localUhid, peerUhid, t.iceServers, t.signaling, onData)
	if err != nil {
		t.mu.Unlock()
		return nil, err
	}
	t.peers[peerUhid] = link
	t.mu.Unlock()

	if err := link.start(initiator); err != nil {
		return nil, err
	}
	if initiator {
		link.waitOpen(connectTimeout)
	}
	return link, nil
}

// --- one WebRTC connection to a single peer ---

type peerLink struct {
	localUhid string
	peerUhid  string
	signaling Signaling
	onData    func(string, []byte)
	pc        *pion.PeerConnection

	mu       sync.Mutex
	dc       *pion.DataChannel
	openCh   chan struct{}
	openOnce sync.Once
	closed   bool
}

func newPeerLink(localUhid, peerUhid string, iceServers []pion.ICEServer, sig Signaling, onData func(string, []byte)) (*peerLink, error) {
	pc, err := pion.NewPeerConnection(pion.Configuration{ICEServers: iceServers})
	if err != nil {
		return nil, err
	}
	l := &peerLink{
		localUhid: localUhid,
		peerUhid:  peerUhid,
		signaling: sig,
		onData:    onData,
		pc:        pc,
		openCh:    make(chan struct{}),
	}
	pc.OnICECandidate(func(c *pion.ICECandidate) {
		if c == nil {
			return // nil candidate signals end-of-gathering
		}
		ci := c.ToJSON()
		_ = sig.SendSignal(peerUhid, Signal{
			FromUhid:      localUhid,
			ToUhid:        peerUhid,
			Type:          SignalCandidate,
			Candidate:     ci.Candidate,
			SDPMid:        strOrEmpty(ci.SDPMid),
			SDPMLineIndex: u16OrZero(ci.SDPMLineIndex),
		})
	})
	pc.OnDataChannel(func(dc *pion.DataChannel) { l.attach(dc) }) // responder receives the channel
	pc.OnConnectionStateChange(func(st pion.PeerConnectionState) {
		switch st {
		case pion.PeerConnectionStateFailed, pion.PeerConnectionStateClosed, pion.PeerConnectionStateDisconnected:
			l.markClosed()
		}
	})
	return l, nil
}

func (l *peerLink) start(initiator bool) error {
	if !initiator {
		return nil // responder waits for the inbound offer (acceptOffer)
	}
	dc, err := l.pc.CreateDataChannel(dataChannelLabel, nil)
	if err != nil {
		return err
	}
	l.attach(dc)
	offer, err := l.pc.CreateOffer(nil)
	if err != nil {
		return err
	}
	if err := l.pc.SetLocalDescription(offer); err != nil {
		return err
	}
	return l.signaling.SendSignal(l.peerUhid, Signal{
		FromUhid: l.localUhid, ToUhid: l.peerUhid, Type: SignalOffer, SDP: offer.SDP,
	})
}

func (l *peerLink) acceptOffer(sdp string) {
	if err := l.pc.SetRemoteDescription(pion.SessionDescription{Type: pion.SDPTypeOffer, SDP: sdp}); err != nil {
		return
	}
	answer, err := l.pc.CreateAnswer(nil)
	if err != nil {
		return
	}
	if err := l.pc.SetLocalDescription(answer); err != nil {
		return
	}
	_ = l.signaling.SendSignal(l.peerUhid, Signal{
		FromUhid: l.localUhid, ToUhid: l.peerUhid, Type: SignalAnswer, SDP: answer.SDP,
	})
}

func (l *peerLink) acceptAnswer(sdp string) {
	_ = l.pc.SetRemoteDescription(pion.SessionDescription{Type: pion.SDPTypeAnswer, SDP: sdp})
}

func (l *peerLink) addRemoteCandidate(s Signal) {
	if s.Candidate == "" {
		return
	}
	mid := s.SDPMid
	idx := s.SDPMLineIndex
	_ = l.pc.AddICECandidate(pion.ICECandidateInit{Candidate: s.Candidate, SDPMid: &mid, SDPMLineIndex: &idx})
}

func (l *peerLink) attach(dc *pion.DataChannel) {
	l.mu.Lock()
	l.dc = dc
	l.mu.Unlock()
	dc.OnOpen(func() { l.openOnce.Do(func() { close(l.openCh) }) })
	dc.OnMessage(func(msg pion.DataChannelMessage) {
		if l.onData != nil {
			l.onData(l.peerUhid, msg.Data)
		}
	})
}

func (l *peerLink) isOpen() bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.dc != nil && l.dc.ReadyState() == pion.DataChannelStateOpen
}

func (l *peerLink) isClosed() bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.closed
}

func (l *peerLink) markClosed() {
	l.mu.Lock()
	if l.closed {
		l.mu.Unlock()
		return
	}
	l.closed = true
	l.mu.Unlock()
	l.openOnce.Do(func() { close(l.openCh) })
}

func (l *peerLink) waitOpen(timeout time.Duration) bool {
	if l.isOpen() {
		return true
	}
	select {
	case <-l.openCh:
		return l.isOpen()
	case <-time.After(timeout):
		return false
	}
}

func (l *peerLink) send(data []byte) (bool, error) {
	if !l.waitOpen(connectTimeout) {
		return false, fmt.Errorf("webrtc: data channel to %q not open", l.peerUhid)
	}
	l.mu.Lock()
	dc := l.dc
	l.mu.Unlock()
	if dc == nil {
		return false, fmt.Errorf("webrtc: no data channel to %q", l.peerUhid)
	}
	if err := dc.Send(data); err != nil {
		return false, err
	}
	return true, nil
}

func (l *peerLink) close() {
	l.markClosed()
	l.mu.Lock()
	dc := l.dc
	l.mu.Unlock()
	if dc != nil {
		_ = dc.Close()
	}
	_ = l.pc.Close()
}

func strOrEmpty(p *string) string {
	if p == nil {
		return ""
	}
	return *p
}

func u16OrZero(p *uint16) uint16 {
	if p == nil {
		return 0
	}
	return *p
}
