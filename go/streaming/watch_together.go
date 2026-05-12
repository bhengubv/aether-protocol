// SPDX-License-Identifier: MIT

package streaming

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/google/uuid"
	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
)

// ──────────────────────────────────────────────
// JSON wire types
// ──────────────────────────────────────────────

// WatchSyncPayload is the JSON body for WatchSync packets.
type WatchSyncPayload struct {
	SessionID     string  `json:"session_id"`
	Kind          string  `json:"kind"` // "play" | "pause" | "seek" | "speed" | "invite"
	PositionMs    int64   `json:"position_ms,omitempty"`
	PlaybackSpeed float64 `json:"playback_speed,omitempty"`
	SentAtMs      int64   `json:"sent_at_ms"`
	ContentID     string  `json:"content_id,omitempty"`
}

// WatchReactionPayload is the JSON body for WatchReaction packets.
type WatchReactionPayload struct {
	SessionID string `json:"session_id"`
	Reaction  string `json:"reaction"`
}

// ──────────────────────────────────────────────
// Session state
// ──────────────────────────────────────────────

// WatchSession holds runtime state for a co-watch session.
type WatchSession struct {
	SessionID     uuid.UUID
	ContentID     string
	Members       map[string]struct{}
	PlaybackSpeed float64
	mu            sync.Mutex
}

// ──────────────────────────────────────────────
// Service
// ──────────────────────────────────────────────

// WatchTogetherService synchronises media playback across mesh peers.
type WatchTogetherService struct {
	sender    routing.MeshSender
	localUhid string
	sessions  sync.Map // uuid.UUID -> *WatchSession

	// Event callbacks — optional.
	OnInviteReceived func(session *WatchSession, fromUhid string)
	OnSyncReceived   func(session *WatchSession, fromUhid string, positionMs int64, speed float64)
	OnReaction       func(session *WatchSession, fromUhid, reaction string)
}

// NewWatchTogetherService constructs a WatchTogetherService.
func NewWatchTogetherService(sender routing.MeshSender) *WatchTogetherService {
	if sender == nil {
		panic("streaming: watch sender must not be nil")
	}
	return &WatchTogetherService{
		sender:    sender,
		localUhid: sender.LocalUhid(),
	}
}

// InviteToSession invites members to a co-watch session for contentID.
func (s *WatchTogetherService) InviteToSession(ctx context.Context, sessionID uuid.UUID, contentID string, memberUhids []string) error {
	if len(memberUhids) == 0 {
		return errors.New("streaming: memberUhids must not be empty")
	}
	session := s.getOrCreateWatchSession(sessionID, contentID)

	session.mu.Lock()
	for _, m := range memberUhids {
		session.Members[m] = struct{}{}
	}
	session.mu.Unlock()

	payload := WatchSyncPayload{
		SessionID: sessionID.String(),
		Kind:      "invite",
		ContentID: contentID,
		SentAtMs:  time.Now().UnixMilli(),
	}
	for _, m := range memberUhids {
		if err := s.sendWatchSync(ctx, m, payload); err != nil {
			return err
		}
	}
	return nil
}

// Play sends a play-sync to all session members.
func (s *WatchTogetherService) Play(ctx context.Context, sessionID uuid.UUID, positionMs int64) error {
	return s.sendSyncToAll(ctx, sessionID, "play", positionMs, 0)
}

// Pause sends a pause-sync to all session members.
func (s *WatchTogetherService) Pause(ctx context.Context, sessionID uuid.UUID, positionMs int64) error {
	return s.sendSyncToAll(ctx, sessionID, "pause", positionMs, 0)
}

// Seek sends a seek-sync to all session members.
func (s *WatchTogetherService) Seek(ctx context.Context, sessionID uuid.UUID, positionMs int64) error {
	return s.sendSyncToAll(ctx, sessionID, "seek", positionMs, 0)
}

// SetSpeed sends a speed-change sync to all session members.
func (s *WatchTogetherService) SetSpeed(ctx context.Context, sessionID uuid.UUID, playbackSpeed float64) error {
	session, err := s.getWatchSession(sessionID)
	if err != nil {
		return err
	}
	session.mu.Lock()
	session.PlaybackSpeed = playbackSpeed
	session.mu.Unlock()
	return s.sendSyncToAll(ctx, sessionID, "speed", 0, playbackSpeed)
}

// SendReaction broadcasts a reaction emoji/string to all session members.
func (s *WatchTogetherService) SendReaction(ctx context.Context, sessionID uuid.UUID, reaction string) error {
	if reaction == "" {
		return errors.New("streaming: reaction must not be empty")
	}
	session, err := s.getWatchSession(sessionID)
	if err != nil {
		return err
	}
	session.mu.Lock()
	members := s.watchMemberSnapshot(session)
	session.mu.Unlock()

	body, err := json.Marshal(WatchReactionPayload{
		SessionID: sessionID.String(),
		Reaction:  reaction,
	})
	if err != nil {
		return fmt.Errorf("streaming: marshal reaction: %w", err)
	}

	for _, m := range members {
		if m == s.localUhid {
			continue
		}
		pkt := protocol.NewMeshPacket()
		pkt.Type = protocol.WatchReaction
		pkt.SourceUhid = s.localUhid
		pkt.DestinationUhid = m
		pkt.Ttl = constants.DefaultTtl
		pkt.Payload = body
		_, _ = s.sender.Send(ctx, pkt, m)
	}
	return nil
}

// HandlePacket processes inbound WatchSync or WatchReaction packets.
func (s *WatchTogetherService) HandlePacket(_ context.Context, packet *protocol.MeshPacket) error {
	if packet == nil {
		return errors.New("streaming: watch packet must not be nil")
	}
	switch packet.Type {
	case protocol.WatchSync:
		return s.handleWatchSync(packet)
	case protocol.WatchReaction:
		return s.handleWatchReaction(packet)
	default:
		return fmt.Errorf("streaming: unexpected watch packet type %s", packet.Type)
	}
}

// ──────────────────────────────────────────────
// Internal helpers
// ──────────────────────────────────────────────

func (s *WatchTogetherService) handleWatchSync(packet *protocol.MeshPacket) error {
	var payload WatchSyncPayload
	if err := json.Unmarshal(packet.Payload, &payload); err != nil {
		return fmt.Errorf("streaming: unmarshal watch sync: %w", err)
	}
	sessionID, err := uuid.Parse(payload.SessionID)
	if err != nil {
		return fmt.Errorf("streaming: invalid session_id: %w", err)
	}

	switch payload.Kind {
	case "invite":
		session := s.getOrCreateWatchSession(sessionID, payload.ContentID)
		session.mu.Lock()
		session.Members[packet.SourceUhid] = struct{}{}
		session.mu.Unlock()
		if cb := s.OnInviteReceived; cb != nil {
			cb(session, packet.SourceUhid)
		}

	case "play", "pause", "seek", "speed":
		session, err := s.getWatchSession(sessionID)
		if err != nil {
			return nil // unknown session — drop
		}
		// RTT compensation: adjust position for network latency.
		// compensated = positionMs + elapsed_since_send * playbackSpeed
		speed := payload.PlaybackSpeed
		if speed == 0 {
			session.mu.Lock()
			speed = session.PlaybackSpeed
			if speed == 0 {
				speed = 1.0
			}
			session.mu.Unlock()
		}
		elapsedMs := time.Now().UnixMilli() - payload.SentAtMs
		compensatedPos := payload.PositionMs + int64(float64(elapsedMs)*speed)

		if cb := s.OnSyncReceived; cb != nil {
			cb(session, packet.SourceUhid, compensatedPos, speed)
		}
	}
	return nil
}

func (s *WatchTogetherService) handleWatchReaction(packet *protocol.MeshPacket) error {
	var payload WatchReactionPayload
	if err := json.Unmarshal(packet.Payload, &payload); err != nil {
		return fmt.Errorf("streaming: unmarshal watch reaction: %w", err)
	}
	sessionID, err := uuid.Parse(payload.SessionID)
	if err != nil {
		return fmt.Errorf("streaming: invalid session_id: %w", err)
	}
	session, err := s.getWatchSession(sessionID)
	if err != nil {
		return nil // unknown session — drop
	}
	if cb := s.OnReaction; cb != nil {
		cb(session, packet.SourceUhid, payload.Reaction)
	}
	return nil
}

func (s *WatchTogetherService) sendSyncToAll(ctx context.Context, sessionID uuid.UUID, kind string, positionMs int64, speed float64) error {
	session, err := s.getWatchSession(sessionID)
	if err != nil {
		return err
	}
	session.mu.Lock()
	members := s.watchMemberSnapshot(session)
	session.mu.Unlock()

	payload := WatchSyncPayload{
		SessionID:     sessionID.String(),
		Kind:          kind,
		PositionMs:    positionMs,
		PlaybackSpeed: speed,
		SentAtMs:      time.Now().UnixMilli(),
	}
	for _, m := range members {
		if m == s.localUhid {
			continue
		}
		if err := s.sendWatchSync(ctx, m, payload); err != nil {
			return err
		}
	}
	return nil
}

func (s *WatchTogetherService) sendWatchSync(ctx context.Context, toUhid string, payload WatchSyncPayload) error {
	body, err := json.Marshal(payload)
	if err != nil {
		return fmt.Errorf("streaming: marshal watch sync: %w", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.WatchSync
	pkt.SourceUhid = s.localUhid
	pkt.DestinationUhid = toUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body
	_, err = s.sender.Send(ctx, pkt, toUhid)
	return err
}

func (s *WatchTogetherService) getWatchSession(sessionID uuid.UUID) (*WatchSession, error) {
	v, ok := s.sessions.Load(sessionID)
	if !ok {
		return nil, fmt.Errorf("streaming: unknown watch session %s", sessionID)
	}
	return v.(*WatchSession), nil
}

func (s *WatchTogetherService) getOrCreateWatchSession(sessionID uuid.UUID, contentID string) *WatchSession {
	v, _ := s.sessions.LoadOrStore(sessionID, &WatchSession{
		SessionID:     sessionID,
		ContentID:     contentID,
		Members:       make(map[string]struct{}),
		PlaybackSpeed: 1.0,
	})
	return v.(*WatchSession)
}

func (s *WatchTogetherService) watchMemberSnapshot(session *WatchSession) []string {
	out := make([]string, 0, len(session.Members))
	for m := range session.Members {
		out = append(out, m)
	}
	return out
}
