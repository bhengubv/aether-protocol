// SPDX-License-Identifier: MIT

// Package handshake implements the protocol-version + capability
// negotiation surface (Hello / HelloAck) the Aether mesh runs on first
// contact with an unknown peer.
//
// Wire flow:
//
//	A → B   Hello       { min:1, max:2, caps:[X,Y,Z], impl:"…" }
//	A ← B   HelloAck    { min:1, max:2, caps:[X,Y],   impl:"…" }
//
// Negotiation rules:
//
//   - Negotiated version = min(ourMax, theirMax).
//   - If min(ourMax,theirMax) < max(ourMin,theirMin) the ranges do not
//     overlap → emit IncompatiblePeer, refuse to lock in.
//   - Locked-in capability set = ourCaps ∩ theirCaps.
//
// The handshake itself is unencrypted and unauthenticated — it runs before
// any Signal session exists. Peer identity is verified later via Ed25519
// packet signatures on the data packets the peer subsequently sends.
//
// Backward-compat: a peer that never replies with a HelloAck is assumed to
// be running protocol version 1 with no advertised capabilities. Hosts call
// AssumeLegacyV1 from their own timer / heartbeat loop after a timeout
// window. Idempotent — a real HelloAck arriving later still wins (the
// existing record is preserved).
//
// This package mirrors the C# Aether.Handshake.HandshakeService at
// commit 9380631; cross-language interop is asserted by C# peers consuming
// Go-emitted Hello packets and vice versa (the JSON payload uses snake_case
// keys identical to the C# HelloPayload).
package handshake

import (
	"context"
	"encoding/json"
	"fmt"
	"sync"
	"time"

	"github.com/thegeeknetwork/aether-protocol-go/constants"
	"github.com/thegeeknetwork/aether-protocol-go/protocol"
)

// MeshSender is the minimal sending abstraction the handshake service
// depends on. Hosts wire this up with a thin adapter over their transport
// manager so this package doesn't take a hard dependency on a specific
// transport implementation. Compatible with routing.MeshSender.
type MeshSender interface {
	// LocalUhid is the local node's UHID. Used as MeshPacket.SourceUhid
	// on outbound Hello / HelloAck packets.
	LocalUhid() string

	// Send forwards a packet to a single next-hop peer (already routed).
	// Returns true on successful delivery. Errors propagate.
	Send(ctx context.Context, packet *protocol.MeshPacket, nextHopUhid string) (bool, error)
}

// DefaultCapabilities is the default capability set advertised by this
// implementation. Mirrors C# HandshakeService.DefaultCapabilities.
var DefaultCapabilities = []string{
	"signal-x3dh",
	"double-ratchet",
	"dtn-custody",
	"sos",
	"voice",
	"stream",
}

// DefaultImplementation is the default implementation banner emitted in our
// Hello / HelloAck. Mirrors C#'s "aether-csharp/1.0.0" with the language tag
// switched.
const DefaultImplementation = "aether-go/1.0.0"

// Service is the concrete IHandshakeService implementation. Tracks the
// peers we've Hello'd, the peers we've finished negotiating with, and
// dispatches PeerNegotiated / IncompatiblePeer callbacks on completion or
// incompatibility.
type Service struct {
	sender         MeshSender
	ourMinVersion  byte
	ourMaxVersion  byte
	ourCaps        map[string]struct{}
	ourCapsList    []string // cached for payload emission
	ourImpl        string

	mu         sync.Mutex
	helloSent  map[string]struct{}      // peers we've already sent a Hello to
	negotiated map[string]*PeerCapabilities

	// Optional callbacks. Both run synchronously on the goroutine that
	// triggered them. Hosts that need async dispatch should marshal off
	// the callback themselves.
	onPeerNegotiated  func(caps *PeerCapabilities)
	onIncompatiblePeer func(evt IncompatiblePeerEvent)
}

// Option configures a Service at construction time.
type Option func(*Service)

// WithMinVersion overrides the lowest protocol version we accept. Defaults
// to 1.
func WithMinVersion(v byte) Option {
	return func(s *Service) { s.ourMinVersion = v }
}

// WithMaxVersion overrides the highest protocol version we speak. Defaults
// to constants.CurrentProtocolVersion.
func WithMaxVersion(v byte) Option {
	return func(s *Service) { s.ourMaxVersion = v }
}

// WithCapabilities overrides the capability set we advertise. Defaults to
// DefaultCapabilities. The caller's slice is copied — later mutations do
// not affect the service.
func WithCapabilities(caps []string) Option {
	return func(s *Service) {
		s.ourCapsList = append([]string(nil), caps...)
		s.ourCaps = make(map[string]struct{}, len(caps))
		for _, c := range caps {
			s.ourCaps[c] = struct{}{}
		}
	}
}

// WithImplementation overrides the implementation banner string.
func WithImplementation(impl string) Option {
	return func(s *Service) { s.ourImpl = impl }
}

// WithPeerNegotiatedHandler registers a callback fired when negotiation
// completes (either via HelloAck receipt or via the backward-compat
// fallback). Replaces any previously registered handler.
func WithPeerNegotiatedHandler(fn func(caps *PeerCapabilities)) Option {
	return func(s *Service) { s.onPeerNegotiated = fn }
}

// WithIncompatiblePeerHandler registers a callback fired when a peer's
// announced version range does not overlap with ours. Replaces any
// previously registered handler.
func WithIncompatiblePeerHandler(fn func(evt IncompatiblePeerEvent)) Option {
	return func(s *Service) { s.onIncompatiblePeer = fn }
}

// NewService constructs a Service with the given mesh sender and options.
// Defaults: minVersion=1, maxVersion=constants.CurrentProtocolVersion,
// capabilities=DefaultCapabilities, implementation=DefaultImplementation.
func NewService(sender MeshSender, opts ...Option) (*Service, error) {
	if sender == nil {
		return nil, fmt.Errorf("handshake: sender must not be nil")
	}
	defaultCaps := make(map[string]struct{}, len(DefaultCapabilities))
	for _, c := range DefaultCapabilities {
		defaultCaps[c] = struct{}{}
	}

	s := &Service{
		sender:        sender,
		ourMinVersion: 1,
		ourMaxVersion: constants.CurrentProtocolVersion,
		ourCaps:       defaultCaps,
		ourCapsList:   append([]string(nil), DefaultCapabilities...),
		ourImpl:       DefaultImplementation,
		helloSent:     make(map[string]struct{}),
		negotiated:    make(map[string]*PeerCapabilities),
	}
	for _, opt := range opts {
		opt(s)
	}
	if s.ourMinVersion > s.ourMaxVersion {
		return nil, fmt.Errorf(
			"handshake: ourMinVersion (%d) cannot exceed ourMaxVersion (%d)",
			s.ourMinVersion, s.ourMaxVersion)
	}
	return s, nil
}

// Initiate sends a Hello to a freshly discovered peer. No-op if a Hello has
// already been sent to this peer in the current session (re-broadcasts can
// otherwise cause duplicate Hellos). Skips self.
func (s *Service) Initiate(ctx context.Context, peerUhid string) error {
	if peerUhid == "" {
		return fmt.Errorf("handshake: peerUhid must not be empty")
	}
	if peerUhid == s.sender.LocalUhid() {
		return nil
	}

	s.mu.Lock()
	if _, dup := s.helloSent[peerUhid]; dup {
		s.mu.Unlock()
		return nil
	}
	s.helloSent[peerUhid] = struct{}{}
	s.mu.Unlock()

	pkt, err := s.buildPacket(protocol.Hello, peerUhid)
	if err != nil {
		return fmt.Errorf("handshake: build Hello: %w", err)
	}
	if _, err := s.sender.Send(ctx, pkt, peerUhid); err != nil {
		return fmt.Errorf("handshake: send Hello: %w", err)
	}
	return nil
}

// Handle dispatches an inbound MeshPacket to the appropriate handler based
// on its PacketType. Non-handshake packet types are ignored (returns nil).
// Hosts that already discriminate packet types may call HandleHello /
// HandleHelloAck directly.
func (s *Service) Handle(ctx context.Context, pkt *protocol.MeshPacket) error {
	if pkt == nil {
		return fmt.Errorf("handshake: packet must not be nil")
	}
	switch pkt.Type {
	case protocol.Hello:
		return s.HandleHello(ctx, pkt)
	case protocol.HelloAck:
		return s.HandleHelloAck(ctx, pkt)
	default:
		return nil
	}
}

// HandleHello processes an inbound Hello: locks in their announced
// capabilities and replies with a HelloAck. If the version ranges don't
// overlap, fires IncompatiblePeer and skips the ack.
func (s *Service) HandleHello(ctx context.Context, pkt *protocol.MeshPacket) error {
	if pkt == nil {
		return fmt.Errorf("handshake: packet must not be nil")
	}
	if pkt.Type != protocol.Hello {
		return fmt.Errorf("handshake: expected Hello, got %s", pkt.Type)
	}
	if pkt.SourceUhid == "" {
		return nil
	}
	if pkt.SourceUhid == s.sender.LocalUhid() {
		return nil
	}

	theirs, ok := s.tryDeserialize(pkt)
	if !ok {
		return nil
	}

	caps, ok := s.tryNegotiate(pkt.SourceUhid, theirs)
	if !ok {
		return nil
	}

	s.mu.Lock()
	s.negotiated[pkt.SourceUhid] = caps
	cb := s.onPeerNegotiated
	s.mu.Unlock()

	if cb != nil {
		cb(caps)
	}

	// Reply with HelloAck — even if we already sent them an unprompted
	// Hello, the spec is symmetric and the ack carries our own range/caps.
	ack, err := s.buildPacket(protocol.HelloAck, pkt.SourceUhid)
	if err != nil {
		return fmt.Errorf("handshake: build HelloAck: %w", err)
	}
	if _, err := s.sender.Send(ctx, ack, pkt.SourceUhid); err != nil {
		return fmt.Errorf("handshake: send HelloAck: %w", err)
	}
	return nil
}

// HandleHelloAck processes an inbound HelloAck: locks in the negotiated
// capabilities for the replying peer.
func (s *Service) HandleHelloAck(ctx context.Context, pkt *protocol.MeshPacket) error {
	_ = ctx
	if pkt == nil {
		return fmt.Errorf("handshake: packet must not be nil")
	}
	if pkt.Type != protocol.HelloAck {
		return fmt.Errorf("handshake: expected HelloAck, got %s", pkt.Type)
	}
	if pkt.SourceUhid == "" {
		return nil
	}
	if pkt.SourceUhid == s.sender.LocalUhid() {
		return nil
	}

	theirs, ok := s.tryDeserialize(pkt)
	if !ok {
		return nil
	}

	caps, ok := s.tryNegotiate(pkt.SourceUhid, theirs)
	if !ok {
		return nil
	}

	s.mu.Lock()
	s.negotiated[pkt.SourceUhid] = caps
	cb := s.onPeerNegotiated
	s.mu.Unlock()

	if cb != nil {
		cb(caps)
	}
	return nil
}

// GetPeerCapabilities looks up the locked-in capabilities for a peer.
// Returns (nil, false) if the handshake has not yet completed — callers can
// either wait for the PeerNegotiated callback or proceed with caution.
func (s *Service) GetPeerCapabilities(peerUhid string) (*PeerCapabilities, bool) {
	if peerUhid == "" {
		return nil, false
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	caps, ok := s.negotiated[peerUhid]
	return caps, ok
}

// Renegotiate drops a peer's cached capabilities and re-issues a Hello on
// the next outbound contact. Used when version-mismatch is detected in
// subsequent traffic.
func (s *Service) Renegotiate(peerUhid string) {
	if peerUhid == "" {
		return
	}
	s.mu.Lock()
	delete(s.negotiated, peerUhid)
	delete(s.helloSent, peerUhid)
	s.mu.Unlock()
}

// GetAllNegotiated returns a snapshot of every peer that has finished
// negotiating, for diagnostics / health-check use.
func (s *Service) GetAllNegotiated() []*PeerCapabilities {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]*PeerCapabilities, 0, len(s.negotiated))
	for _, c := range s.negotiated {
		out = append(out, c)
	}
	return out
}

// AssumeLegacyV1 installs a "v1, no caps" record for a peer that never
// replied to our Hello within the timeout window. Hosts call this from
// their own timer / heartbeat loop. Idempotent — if the peer has since
// replied with a HelloAck, the existing record wins.
func (s *Service) AssumeLegacyV1(peerUhid string) {
	if peerUhid == "" || peerUhid == s.sender.LocalUhid() {
		return
	}

	fallback := &PeerCapabilities{
		PeerUhid:              peerUhid,
		NegotiatedVersion:     1,
		Capabilities:          map[string]struct{}{},
		ImplementationVersion: "",
		NegotiatedAt:          time.Now().UTC(),
	}

	s.mu.Lock()
	if _, exists := s.negotiated[peerUhid]; exists {
		s.mu.Unlock()
		return
	}
	s.negotiated[peerUhid] = fallback
	cb := s.onPeerNegotiated
	s.mu.Unlock()

	if cb != nil {
		cb(fallback)
	}
}

// buildPacket constructs a Hello / HelloAck MeshPacket carrying our
// announced range, capability set, and implementation banner.
func (s *Service) buildPacket(t protocol.PacketType, dest string) (*protocol.MeshPacket, error) {
	payload := HelloPayload{
		MinVersion:     s.ourMinVersion,
		MaxVersion:     s.ourMaxVersion,
		Capabilities:   append([]string(nil), s.ourCapsList...),
		Implementation: s.ourImpl,
	}
	body, err := json.Marshal(payload)
	if err != nil {
		return nil, fmt.Errorf("marshal HelloPayload: %w", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = t
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = dest
	pkt.Ttl = 1 // direct hop only — handshake never relays
	pkt.Priority = 0
	pkt.ProtocolVersion = s.ourMaxVersion
	pkt.Payload = body
	return pkt, nil
}

// tryDeserialize parses the JSON HelloPayload from the packet's body.
// Returns (nil, false) on any parse error or empty payload — callers
// silently ignore the packet rather than crashing on malicious input.
func (s *Service) tryDeserialize(pkt *protocol.MeshPacket) (*HelloPayload, bool) {
	if len(pkt.Payload) == 0 {
		return nil, false
	}
	var p HelloPayload
	if err := json.Unmarshal(pkt.Payload, &p); err != nil {
		return nil, false
	}
	return &p, true
}

// tryNegotiate runs the version-overlap + capability-intersection algorithm.
// On success returns (caps, true). On no-overlap fires IncompatiblePeer and
// returns (nil, false).
func (s *Service) tryNegotiate(peerUhid string, theirs *HelloPayload) (*PeerCapabilities, bool) {
	if theirs.MinVersion > theirs.MaxVersion {
		s.fireIncompatible(peerUhid, theirs, "inverted version range")
		return nil, false
	}

	overlapMin := s.ourMinVersion
	if theirs.MinVersion > overlapMin {
		overlapMin = theirs.MinVersion
	}
	overlapMax := s.ourMaxVersion
	if theirs.MaxVersion < overlapMax {
		overlapMax = theirs.MaxVersion
	}
	if overlapMin > overlapMax {
		reason := fmt.Sprintf(
			"no version overlap (ours=%d..%d, theirs=%d..%d)",
			s.ourMinVersion, s.ourMaxVersion, theirs.MinVersion, theirs.MaxVersion)
		s.fireIncompatible(peerUhid, theirs, reason)
		return nil, false
	}

	intersection := make(map[string]struct{})
	for _, c := range theirs.Capabilities {
		if c == "" {
			continue
		}
		if _, ok := s.ourCaps[c]; ok {
			intersection[c] = struct{}{}
		}
	}

	return &PeerCapabilities{
		PeerUhid:              peerUhid,
		NegotiatedVersion:     overlapMax,
		Capabilities:          intersection,
		ImplementationVersion: theirs.Implementation,
		NegotiatedAt:          time.Now().UTC(),
	}, true
}

func (s *Service) fireIncompatible(peerUhid string, theirs *HelloPayload, reason string) {
	s.mu.Lock()
	cb := s.onIncompatiblePeer
	s.mu.Unlock()

	if cb == nil {
		return
	}
	cb(IncompatiblePeerEvent{
		PeerUhid:        peerUhid,
		TheirMinVersion: theirs.MinVersion,
		TheirMaxVersion: theirs.MaxVersion,
		OurMinVersion:   s.ourMinVersion,
		OurMaxVersion:   s.ourMaxVersion,
		Reason:          reason,
	})
}
