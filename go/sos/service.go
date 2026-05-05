// SPDX-License-Identifier: MIT

// Package sos implements SOS broadcast origination and re-flooding for the Aether mesh.
// SOS uses PacketType.SosBroadcast with extended TTL (constants.SosTtl) and maximum
// priority (constants.SosPriority). Flooding is the transport: every receiving node
// re-broadcasts until TTL is exhausted.
package sos

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/google/uuid"
	"github.com/thegeeknetwork/aether-protocol-go/constants"
	"github.com/thegeeknetwork/aether-protocol-go/extensibility"
	"github.com/thegeeknetwork/aether-protocol-go/models"
	"github.com/thegeeknetwork/aether-protocol-go/protocol"
	"github.com/thegeeknetwork/aether-protocol-go/routing"
)

// Service originates SOS broadcasts and re-floods inbound ones. Dedups by packet
// ID; rate-limited to constants.MaxSosBroadcastsPerHour originations per rolling hour.
type Service struct {
	sender     routing.MeshSender
	backend    extensibility.BackendClient
	incentives extensibility.IncentiveProvider

	mu                sync.Mutex
	recentOrigins     []time.Time
	seen              map[uuid.UUID]struct{}
	activeAlerts      map[string]*models.SosAlert

	OnSosReceived func(alert *models.SosAlert)
	OnSosResolved func(broadcastID string)
}

// NewService constructs a Service. Pass nil for backend / incentives to receive defaults.
func NewService(sender routing.MeshSender, backend extensibility.BackendClient, incentives extensibility.IncentiveProvider) *Service {
	if sender == nil {
		panic("sos: sender must not be nil")
	}
	if backend == nil {
		backend = extensibility.NoopBackendClient{}
	}
	if incentives == nil {
		incentives = extensibility.NoopIncentiveProvider{}
	}
	return &Service{
		sender:       sender,
		backend:      backend,
		incentives:   incentives,
		seen:         make(map[uuid.UUID]struct{}),
		activeAlerts: make(map[string]*models.SosAlert),
	}
}

// Broadcast originates an SOS. Floods the mesh and (if a backend client is wired up)
// mirrors the alert via cloud. Returns false if the rolling rate limit is exhausted.
func (s *Service) Broadcast(ctx context.Context, broadcastType, message string, latitude, longitude float64, geohash string) (bool, error) {
	if broadcastType == "" {
		return false, errors.New("sos: broadcastType must not be empty")
	}

	s.mu.Lock()
	s.pruneOldOrigins()
	if int32(len(s.recentOrigins)) >= constants.MaxSosBroadcastsPerHour {
		s.mu.Unlock()
		return false, nil
	}
	s.recentOrigins = append(s.recentOrigins, time.Now())
	s.mu.Unlock()

	alertID := uuid.NewString()
	alert := &models.SosAlert{
		ID:            alertID,
		SenderUhid:    s.sender.LocalUhid(),
		BroadcastType: broadcastType,
		Message:       message,
		Latitude:      latitude,
		Longitude:     longitude,
		Geohash:       geohash,
		Timestamp:     time.Now(),
		ReceivedAt:    time.Now(),
	}
	s.mu.Lock()
	s.activeAlerts[alertID] = alert
	s.mu.Unlock()

	body, err := json.Marshal(sosWire{
		BroadcastID:   alertID,
		BroadcastType: broadcastType,
		Message:       message,
		Latitude:      latitude,
		Longitude:     longitude,
		Geohash:       geohash,
	})
	if err != nil {
		return false, fmt.Errorf("sos: marshal payload: %w", err)
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.SosBroadcast
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = ""
	pkt.Ttl = constants.SosTtl
	pkt.Priority = constants.SosPriority
	pkt.Payload = body

	s.mu.Lock()
	s.seen[pkt.ID] = struct{}{}
	s.mu.Unlock()

	if _, err := s.sender.Broadcast(ctx, pkt); err != nil {
		return false, err
	}
	alertJSON, _ := json.Marshal(alert)
	_, _ = s.backend.SyncSos(ctx, alertJSON)
	return true, nil
}

// Resolve marks an SOS resolved locally and stops forwarding it.
func (s *Service) Resolve(ctx context.Context, broadcastID string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.activeAlerts[broadcastID]; ok {
		delete(s.activeAlerts, broadcastID)
		if cb := s.OnSosResolved; cb != nil {
			cb(broadcastID)
		}
	}
}

// GetActiveAlerts returns every SOS alert currently considered active on this node.
func (s *Service) GetActiveAlerts() []models.SosAlert {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]models.SosAlert, 0, len(s.activeAlerts))
	for _, a := range s.activeAlerts {
		out = append(out, *a)
	}
	return out
}

// Handle processes an inbound SOS packet. Dedups, raises OnSosReceived, re-broadcasts.
func (s *Service) Handle(ctx context.Context, packet *protocol.MeshPacket) error {
	if packet == nil {
		return errors.New("sos: packet must not be nil")
	}
	if packet.Type != protocol.SosBroadcast {
		return errors.New("sos: Handle expected PacketType.SosBroadcast")
	}

	s.mu.Lock()
	if _, dup := s.seen[packet.ID]; dup {
		s.mu.Unlock()
		return nil
	}
	s.seen[packet.ID] = struct{}{}
	s.mu.Unlock()

	var body sosWire
	if err := json.Unmarshal(packet.Payload, &body); err != nil {
		return fmt.Errorf("sos: deserialize payload: %w", err)
	}

	if packet.SourceUhid == s.sender.LocalUhid() {
		return nil
	}

	alert := &models.SosAlert{
		ID:            body.BroadcastID,
		SenderUhid:    packet.SourceUhid,
		BroadcastType: body.BroadcastType,
		Message:       body.Message,
		Latitude:      body.Latitude,
		Longitude:     body.Longitude,
		Geohash:       body.Geohash,
		Timestamp:     time.Now(),
		ReceivedAt:    time.Now(),
	}
	s.mu.Lock()
	s.activeAlerts[alert.ID] = alert
	s.mu.Unlock()
	if cb := s.OnSosReceived; cb != nil {
		cb(alert)
	}

	if packet.Ttl > 1 {
		packet.Ttl--
		_, _ = s.sender.Broadcast(ctx, packet)
		_ = s.incentives.RecordRelay(ctx, s.sender.LocalUhid(), packet)
	}
	return nil
}

func (s *Service) pruneOldOrigins() {
	cutoff := time.Now().Add(-time.Hour)
	pruned := s.recentOrigins[:0]
	for _, t := range s.recentOrigins {
		if t.After(cutoff) {
			pruned = append(pruned, t)
		}
	}
	s.recentOrigins = pruned
}

// sosWire is the snake_case JSON envelope (cross-language stable).
type sosWire struct {
	BroadcastID   string  `json:"broadcast_id"`
	BroadcastType string  `json:"broadcast_type"`
	Message       string  `json:"message"`
	Latitude      float64 `json:"latitude"`
	Longitude     float64 `json:"longitude"`
	Geohash       string  `json:"geohash"`
}
