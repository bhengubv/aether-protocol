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
	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/extensibility"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
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

	// OnSosAcknowledged fires on the ORIGINATING node when a peer acknowledges
	// receiving one of our active SOS alerts — proof the emergency reached at
	// least one device. Carries the responder and the running distinct count.
	// Mirrors the C# SosAcknowledged event.
	OnSosAcknowledged func(ack models.SosAcknowledgement)
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
		ID:             alertID,
		SenderUhid:     s.sender.LocalUhid(),
		BroadcastType:  broadcastType,
		Message:        message,
		Latitude:       latitude,
		Longitude:      longitude,
		Geohash:        geohash,
		Timestamp:      time.Now(),
		ReceivedAt:     time.Now(),
		AcknowledgedBy: make(map[string]struct{}),
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
		ID:             body.BroadcastID,
		SenderUhid:     packet.SourceUhid,
		BroadcastType:  body.BroadcastType,
		Message:        body.Message,
		Latitude:       body.Latitude,
		Longitude:      body.Longitude,
		Geohash:        body.Geohash,
		Timestamp:      time.Now(),
		ReceivedAt:     time.Now(),
		AcknowledgedBy: make(map[string]struct{}),
	}
	s.mu.Lock()
	s.activeAlerts[alert.ID] = alert
	s.mu.Unlock()
	if cb := s.OnSosReceived; cb != nil {
		cb(alert)
	}

	// Acknowledge back to the originator so the sender learns their SOS reached a device.
	s.sendSosAck(ctx, body.BroadcastID, packet.SourceUhid)

	if packet.Ttl > 1 {
		packet.Ttl--
		_, _ = s.sender.Broadcast(ctx, packet)
		_ = s.incentives.RecordRelay(ctx, s.sender.LocalUhid(), packet)
	}
	return nil
}

// HandleAck processes an inbound SosAck packet. On the originating node it records
// the responder against the matching active alert (deduping by responder UHID) and
// fires OnSosAcknowledged. No-op if the ack references an SOS this node did not
// originate (or one it has already resolved), or if the responder is this node
// itself. Returns an error only if the packet is nil or not a SosAck.
func (s *Service) HandleAck(ctx context.Context, packet *protocol.MeshPacket) error {
	if packet == nil {
		return errors.New("sos: packet must not be nil")
	}
	if packet.Type != protocol.SosAck {
		return errors.New("sos: HandleAck expected PacketType.SosAck")
	}

	var body sosAckWire
	if err := json.Unmarshal(packet.Payload, &body); err != nil {
		// Malformed ack payload: log-and-drop, not a caller error (mirrors C#).
		return nil
	}

	// The ack payload carries a uuid.UUID; alerts are keyed by their string form.
	broadcastID := body.BroadcastID.String()

	responder := packet.SourceUhid
	if responder == "" {
		return nil
	}
	if responder == s.sender.LocalUhid() {
		return nil // our own ack echoed back — ignore
	}

	s.mu.Lock()
	// Only the ORIGINATOR holds this alert in activeAlerts; every other node ignores the ack.
	alert, ok := s.activeAlerts[broadcastID]
	if !ok {
		s.mu.Unlock()
		return nil
	}
	if alert.AcknowledgedBy == nil {
		alert.AcknowledgedBy = make(map[string]struct{})
	}
	if _, dup := alert.AcknowledgedBy[responder]; dup {
		s.mu.Unlock()
		return nil // already counted this responder — dedup
	}
	alert.AcknowledgedBy[responder] = struct{}{}
	total := len(alert.AcknowledgedBy)
	s.mu.Unlock()

	if cb := s.OnSosAcknowledged; cb != nil {
		cb(models.SosAcknowledgement{
			BroadcastID:           broadcastID,
			ResponderUhid:         responder,
			TotalAcknowledgements: total,
		})
	}
	return nil
}

// sendSosAck sends a directed SosAck back to the alert originator so the sender
// learns their emergency reached this device. Best-effort: delivers when the
// originator is reachable as a next hop.
func (s *Service) sendSosAck(ctx context.Context, broadcastID, originatorUhid string) {
	if originatorUhid == "" {
		return
	}
	if originatorUhid == s.sender.LocalUhid() {
		return
	}

	id, err := uuid.Parse(broadcastID)
	if err != nil {
		return
	}

	body, err := json.Marshal(sosAckWire{
		BroadcastID:  id,
		ReceivedAtMs: time.Now().UnixMilli(),
	})
	if err != nil {
		return
	}

	ack := protocol.NewMeshPacket()
	ack.Type = protocol.SosAck
	ack.SourceUhid = s.sender.LocalUhid()
	ack.DestinationUhid = originatorUhid
	ack.Ttl = constants.SosTtl
	ack.Priority = constants.SosPriority
	ack.Payload = body

	_, _ = s.sender.Send(ctx, ack, originatorUhid)
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

// sosAckWire is the JSON payload for PacketType.SosAck packets. Wire format:
// UTF-8 JSON, snake_case keys, field order broadcast_id then received_at_ms, no
// whitespace, UUID lowercase-dashed 36 chars, received_at_ms a bare integer.
// This is the byte-identity gate for the SOS acknowledgement path
// (fixtures/sos/vectors.json). BroadcastID is a uuid.UUID so it marshals to the
// canonical lowercase-dashed form across every language port. The acknowledging
// node's identity is carried by the enclosing packet's SourceUhid — it is NOT
// duplicated here. Mirrors the C# SosAckPayload.
type sosAckWire struct {
	BroadcastID  uuid.UUID `json:"broadcast_id"`
	ReceivedAtMs int64     `json:"received_at_ms"`
}
