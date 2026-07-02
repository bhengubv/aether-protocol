// SPDX-License-Identifier: MIT

// Package heartbeat implements liveness beacons for the Aether mesh. A node
// periodically broadcasts a Heartbeat packet (PacketType.Heartbeat, TTL 1 — direct
// neighbours only) so peers can track its liveness. Receivers maintain a per-peer
// PeerLiveness table keyed by the originating node's UHID and can query which peers
// are currently live. Unauthenticated by design — like SOS, a heartbeat is a
// low-stakes liveness hint, not a security assertion. Mirrors the C#
// AetherNet.Heartbeat.HeartbeatService.
package heartbeat

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
)

// PeerLiveness is a peer's last observed liveness, maintained on the receiving node.
// Mirrors the C# AetherNet.Heartbeat.PeerLiveness.
type PeerLiveness struct {
	// Uhid of the peer this liveness record describes.
	Uhid string
	// LastSequence is the Sequence of the most recent heartbeat seen from the peer.
	LastSequence int32
	// LastSentAtMs is the peer-stamped SentAtMs of the most recent heartbeat.
	LastSentAtMs int64
	// ReceivedAtMs is the local Unix-ms timestamp when the most recent heartbeat was received.
	ReceivedAtMs int64
}

// Service broadcasts Heartbeat beacons (TTL 1, one hop) and tracks the liveness of
// peers from the heartbeats they broadcast. The sequence number increments on every
// SendHeartbeat call; receivers key liveness by the enclosing packet's SourceUhid.
type Service struct {
	sender routing.MeshSender

	mu       sync.Mutex
	sequence int32
	peers    map[string]*PeerLiveness

	// OnPeerSeen fires when a heartbeat is received from a peer (new or refreshed
	// liveness). Mirrors the C# PeerSeen event.
	OnPeerSeen func(peer PeerLiveness)
}

// NewService constructs a Service. Panics if sender is nil.
func NewService(sender routing.MeshSender) *Service {
	if sender == nil {
		panic("heartbeat: sender must not be nil")
	}
	return &Service{
		sender: sender,
		peers:  make(map[string]*PeerLiveness),
	}
}

// SendHeartbeat broadcasts a single heartbeat to all directly connected peers (TTL 1).
// The sequence number increments on every call. Returns the number of peers the beacon
// was delivered to.
func (s *Service) SendHeartbeat(ctx context.Context) (int, error) {
	s.mu.Lock()
	s.sequence++
	seq := s.sequence
	s.mu.Unlock()

	body, err := json.Marshal(heartbeatWire{
		Sequence: seq,
		SentAtMs: time.Now().UnixMilli(),
	})
	if err != nil {
		return 0, fmt.Errorf("heartbeat: marshal payload: %w", err)
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.Heartbeat
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = "*"
	pkt.Ttl = 1 // heartbeats are single-hop: liveness of DIRECT neighbours only
	pkt.Payload = body

	delivered, err := s.sender.Broadcast(ctx, pkt)
	if err != nil {
		return 0, err
	}
	return delivered, nil
}

// Handle processes an inbound Heartbeat packet: refresh the sender's liveness record
// and fire OnPeerSeen. Returns false (no error) for self-originated heartbeats, the
// wrong packet type, or a malformed payload. Returns an error only if the packet is nil.
func (s *Service) Handle(ctx context.Context, packet *protocol.MeshPacket) (bool, error) {
	if packet == nil {
		return false, errors.New("heartbeat: packet must not be nil")
	}
	if packet.Type != protocol.Heartbeat {
		return false, nil
	}
	// Ignore our own heartbeat echoed back.
	if packet.SourceUhid == s.sender.LocalUhid() {
		return false, nil
	}

	var body heartbeatWire
	if err := json.Unmarshal(packet.Payload, &body); err != nil {
		// Malformed payload: log-and-drop, not a caller error (mirrors C#).
		return false, nil
	}

	liveness := PeerLiveness{
		Uhid:         packet.SourceUhid,
		LastSequence: body.Sequence,
		LastSentAtMs: body.SentAtMs,
		ReceivedAtMs: time.Now().UnixMilli(),
	}
	s.mu.Lock()
	stored := liveness
	s.peers[packet.SourceUhid] = &stored
	s.mu.Unlock()

	if cb := s.OnPeerSeen; cb != nil {
		cb(liveness)
	}
	return true, nil
}

// GetKnownPeers returns a snapshot of every peer this node has ever seen a heartbeat from.
func (s *Service) GetKnownPeers() []PeerLiveness {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]PeerLiveness, 0, len(s.peers))
	for _, p := range s.peers {
		out = append(out, *p)
	}
	return out
}

// GetLivePeers returns peers whose most recent heartbeat was received within the last
// withinSeconds seconds (ReceivedAtMs >= now - withinSeconds*1000).
func (s *Service) GetLivePeers(withinSeconds int32) []PeerLiveness {
	cutoff := time.Now().UnixMilli() - int64(withinSeconds)*1000
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]PeerLiveness, 0, len(s.peers))
	for _, p := range s.peers {
		if p.ReceivedAtMs >= cutoff {
			out = append(out, *p)
		}
	}
	return out
}

// heartbeatWire is the snake_case JSON payload for PacketType.Heartbeat packets. Wire
// format: UTF-8 JSON, snake_case keys, field order sequence then sent_at_ms, no
// whitespace, both values bare integers. This is the byte-identity gate for the
// liveness beacon (fixtures/heartbeat/vectors.json). The heartbeat's originator is
// carried by the enclosing packet's SourceUhid — it is NOT duplicated here. Mirrors
// the C# HeartbeatPayload.
type heartbeatWire struct {
	Sequence int32 `json:"sequence"`
	SentAtMs int64 `json:"sent_at_ms"`
}
