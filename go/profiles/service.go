// SPDX-License-Identifier: MIT

// Package profiles exchanges peer profile metadata over the Aether mesh
// (PacketType.ProfileSync). A node sets its own profile and shares it DIRECTED —
// point-to-point to a specific peer, NOT broadcast — because broadcasting display
// names to every device in range is exactly the metadata leak the privacy roadmap
// forbids. Received profiles are cached (keyed by the sender's UHID) and surfaced
// via OnProfileUpdated. Mirrors the C# AetherNet.Profiles.ProfileService.
package profiles

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
)

// Profile is a node's profile: its UHID plus human-readable metadata. All string
// fields are always present (empty when unset) — no nulls — so the wire encoding
// cannot diverge across languages. Mirrors the C# ProfileSyncPayload.
type Profile struct {
	// Uhid this profile describes (the sender). Self-identifying so a cached
	// profile stays attributable.
	Uhid string `json:"uhid"`
	// DisplayName is the human-readable display name (empty if unset).
	DisplayName string `json:"display_name"`
	// AvatarRef is a content-addressed reference to an avatar (e.g. "blake3:…"),
	// empty if none.
	AvatarRef string `json:"avatar_ref"`
	// StatusMessage is a free-text status / presence message (empty if unset).
	StatusMessage string `json:"status_message"`
	// UpdatedAtMs is the Unix-ms timestamp when the profile was last updated by
	// its owner.
	UpdatedAtMs int64 `json:"updated_at_ms"`
}

// Service is the default profile service. It shares this node's profile directly
// with a chosen peer and caches profiles received from peers. Directed (not
// broadcast) to avoid leaking identity metadata to the whole mesh.
type Service struct {
	sender routing.MeshSender

	mu    sync.Mutex
	local Profile
	peers map[string]Profile

	// OnProfileUpdated fires when a peer's profile is received or refreshed. It
	// does NOT fire for this node's own profile echoed back. Mirrors the C#
	// ProfileUpdated event.
	OnProfileUpdated func(profile Profile)
}

// NewService constructs a Service. Panics if sender is nil. The local profile is
// seeded with the sender's UHID and empty metadata.
func NewService(sender routing.MeshSender) *Service {
	if sender == nil {
		panic("profiles: sender must not be nil")
	}
	return &Service{
		sender: sender,
		local:  Profile{Uhid: sender.LocalUhid()},
		peers:  make(map[string]Profile),
	}
}

// SetLocalProfile sets this node's own profile, stamping UpdatedAtMs to now.
func (s *Service) SetLocalProfile(displayName, avatarRef, statusMessage string) {
	s.mu.Lock()
	s.local = Profile{
		Uhid:          s.sender.LocalUhid(),
		DisplayName:   displayName,
		AvatarRef:     avatarRef,
		StatusMessage: statusMessage,
		UpdatedAtMs:   time.Now().UnixMilli(),
	}
	s.mu.Unlock()
}

// GetLocalProfile returns this node's current local profile.
func (s *Service) GetLocalProfile() Profile {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.local
}

// PublishProfileTo sends this node's local profile directly to peerUhid via the
// sender's directed Send (dest peerUhid, TTL constants.DefaultTtl). Best-effort;
// returns delivery success. Returns an error if peerUhid is empty.
func (s *Service) PublishProfileTo(ctx context.Context, peerUhid string) (bool, error) {
	if peerUhid == "" {
		return false, errors.New("profiles: peerUhid must not be empty")
	}

	s.mu.Lock()
	local := s.local
	s.mu.Unlock()

	body, err := json.Marshal(local)
	if err != nil {
		return false, fmt.Errorf("profiles: marshal payload: %w", err)
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.ProfileSync
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = peerUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body

	return s.sender.Send(ctx, pkt, peerUhid)
}

// Handle processes an inbound ProfileSync packet: cache the sender's profile (keyed
// by its uhid) and fire OnProfileUpdated. Returns false for the wrong packet type, a
// malformed payload, an empty uhid, or our own profile echoed back. Returns an error
// only if the packet is nil.
func (s *Service) Handle(ctx context.Context, packet *protocol.MeshPacket) (bool, error) {
	if packet == nil {
		return false, errors.New("profiles: packet must not be nil")
	}
	if packet.Type != protocol.ProfileSync {
		return false, nil
	}

	var body Profile
	if err := json.Unmarshal(packet.Payload, &body); err != nil {
		// Malformed payload: log-and-drop, not a caller error (mirrors C#).
		return false, nil
	}
	if body.Uhid == "" {
		return false, nil
	}
	// Ignore our own profile echoed back.
	if body.Uhid == s.sender.LocalUhid() {
		return false, nil
	}

	s.mu.Lock()
	s.peers[body.Uhid] = body
	s.mu.Unlock()

	if cb := s.OnProfileUpdated; cb != nil {
		cb(body)
	}
	return true, nil
}

// GetProfile returns the cached profile for uhid and true, or the zero Profile and
// false if none is known.
func (s *Service) GetProfile(uhid string) (Profile, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	p, ok := s.peers[uhid]
	return p, ok
}

// GetKnownProfiles returns a snapshot of every peer profile this node has cached.
func (s *Service) GetKnownProfiles() []Profile {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]Profile, 0, len(s.peers))
	for _, p := range s.peers {
		out = append(out, p)
	}
	return out
}
