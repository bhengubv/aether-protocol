// SPDX-License-Identifier: MIT

package reputation

import (
	"context"
	"encoding/json"
	"math"
	"time"

	"github.com/google/uuid"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

// ─────────────────────────────────────────────────────────────────────────────
// Interfaces
// ─────────────────────────────────────────────────────────────────────────────

// MeshSender is the minimal mesh transport surface needed by
// ReputationGossipService.
type MeshSender interface {
	// LocalUhid returns the UHID of the local node.
	LocalUhid() string
	// BroadcastAsync sends pkt to every directly-connected peer and returns
	// the fan-out count (0 = no peers).
	BroadcastAsync(pkt *protocol.MeshPacket) (int, error)
}

// PacketSigner handles signing and verification of MeshPackets.
type PacketSigner interface {
	// SignPacket returns a copy of pkt with the Signature field populated.
	SignPacket(pkt *protocol.MeshPacket) (*protocol.MeshPacket, error)
	// VerifyPacket verifies the Signature on pkt against senderPublicKey.
	VerifyPacket(pkt *protocol.MeshPacket, senderPublicKey []byte) (bool, error)
}

// Logger is an optional sink for diagnostic messages.
type Logger interface {
	Printf(format string, args ...interface{})
}

// ─────────────────────────────────────────────────────────────────────────────
// Payload
// ─────────────────────────────────────────────────────────────────────────────

// ReputationUpdatePayload is the JSON-encoded body of a
// PacketTypeReputationUpdate packet.
type ReputationUpdatePayload struct {
	ReporterUhid string  `json:"reporter_uhid"`
	TargetUhid   string  `json:"target_uhid"`
	ScoreDelta   float64 `json:"score_delta"`
	TimestampMs  int64   `json:"timestamp_ms"`
	Reason       string  `json:"reason"`
}

// ─────────────────────────────────────────────────────────────────────────────
// Service
// ─────────────────────────────────────────────────────────────────────────────

// ReputationGossipService broadcasts signed reputation updates across the mesh
// and applies incoming gossip to the local NodeReputationService, weighted by
// the reporter's own reputation score.
type ReputationGossipService struct {
	sender     MeshSender
	signing    PacketSigner
	reputation *NodeReputationService
	logger     Logger
}

// NewReputationGossipService creates a new ReputationGossipService.
// logger may be nil; a no-op logger will be used in that case.
func NewReputationGossipService(
	sender MeshSender,
	signing PacketSigner,
	rep *NodeReputationService,
) *ReputationGossipService {
	return &ReputationGossipService{
		sender:     sender,
		signing:    signing,
		reputation: rep,
	}
}

// WithLogger attaches a logger to the service (call after construction if
// needed).
func (s *ReputationGossipService) WithLogger(l Logger) *ReputationGossipService {
	s.logger = l
	return s
}

func (s *ReputationGossipService) log(format string, args ...interface{}) {
	if s.logger != nil {
		s.logger.Printf(format, args...)
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// BroadcastReputationUpdate
// ─────────────────────────────────────────────────────────────────────────────

// BroadcastReputationUpdate builds, signs, and broadcasts a reputation-update
// gossip packet for targetUhid. scoreDelta is clamped to [-1, 1] before
// serialisation.
func (s *ReputationGossipService) BroadcastReputationUpdate(
	ctx context.Context,
	targetUhid string,
	scoreDelta float64,
	reason string,
) error {
	localUhid := s.sender.LocalUhid()
	clamped := clampDelta(scoreDelta)

	payload := ReputationUpdatePayload{
		ReporterUhid: localUhid,
		TargetUhid:   targetUhid,
		ScoreDelta:   clamped,
		TimestampMs:  time.Now().UnixMilli(),
		Reason:       reason,
	}

	jsonBytes, err := json.Marshal(payload)
	if err != nil {
		return err
	}

	pkt := &protocol.MeshPacket{
		ID:              uuid.New(),
		Type:            protocol.PacketTypeReputationUpdate,
		SourceUhid:      localUhid,
		DestinationUhid: "*",
		Ttl:             3,
		Payload:         jsonBytes,
		TimestampMs:     payload.TimestampMs,
	}

	signed, err := s.signing.SignPacket(pkt)
	if err != nil {
		return err
	}

	delivered, err := s.sender.BroadcastAsync(signed)
	if err != nil {
		s.log("ReputationGossip: BroadcastAsync error: %v", err)
		return err
	}

	s.log("ReputationGossip: broadcast sent to %d peer(s) for target=%s delta=%.4f",
		delivered, targetUhid, clamped)
	return nil
}

// ─────────────────────────────────────────────────────────────────────────────
// HandleGossipPacket
// ─────────────────────────────────────────────────────────────────────────────

// HandleGossipPacket processes an inbound reputation gossip packet.
//
// Returns (true, nil) when the packet was accepted and applied.
// Returns (false, nil) when the packet should be silently discarded (wrong
// type, bad signature, stale, invalid fields, own echo).
// Returns (false, error) only on internal errors.
func (s *ReputationGossipService) HandleGossipPacket(
	ctx context.Context,
	pkt *protocol.MeshPacket,
	senderPublicKey []byte,
) (bool, error) {
	// 1. Filter by type.
	if pkt.Type != protocol.PacketTypeReputationUpdate {
		return false, nil
	}

	// 2. Verify signature.
	ok, err := s.signing.VerifyPacket(pkt, senderPublicKey)
	if err != nil || !ok {
		s.log("ReputationGossip: signature verification failed (ok=%v err=%v)", ok, err)
		return false, nil
	}

	// 3. Deserialise payload.
	var payload ReputationUpdatePayload
	if err := json.Unmarshal(pkt.Payload, &payload); err != nil {
		s.log("ReputationGossip: json unmarshal error: %v", err)
		return false, nil
	}

	// 4. Freshness check: reject packets older or newer than 5 minutes.
	nowMs := time.Now().UnixMilli()
	const stalenessWindowMs int64 = 5 * 60 * 1000
	if abs64(nowMs-payload.TimestampMs) > stalenessWindowMs {
		s.log("ReputationGossip: stale packet from %s (age=%dms)", payload.ReporterUhid, nowMs-payload.TimestampMs)
		return false, nil
	}

	// 5. Validate required fields.
	if payload.ReporterUhid == "" || payload.TargetUhid == "" {
		s.log("ReputationGossip: missing reporter or target uhid")
		return false, nil
	}

	// 6. Ignore own-echo.
	if payload.ReporterUhid == s.sender.LocalUhid() {
		return false, nil
	}

	// 7. Clamp delta from the gossip payload.
	clamped := clampDelta(payload.ScoreDelta)

	// 8. Weight by reporter's own reputation (defaults to 1.0 for unknown).
	reporterRep := s.reputation.GetReputationScore(payload.ReporterUhid)

	// 9. Effective delta.
	effectiveDelta := clamped * reporterRep

	// 10. Apply to the target.
	s.reputation.ApplyWeightedDelta(payload.TargetUhid, effectiveDelta)

	s.log("ReputationGossip: applied delta=%.4f (effective=%.4f) to target=%s from reporter=%s",
		clamped, effectiveDelta, payload.TargetUhid, payload.ReporterUhid)

	return true, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

// clampDelta clamps v to the closed interval [-1, 1].
func clampDelta(v float64) float64 {
	return math.Max(-1.0, math.Min(1.0, v))
}

// abs64 returns the absolute value of a int64.
func abs64(v int64) int64 {
	if v < 0 {
		return -v
	}
	return v
}
