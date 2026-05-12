// SPDX-License-Identifier: MIT

package reputation

import (
	"math"
	"sync"
)

const (
	deltaRreqFlood      = -0.05
	deltaReplayAttempt  = -0.15
	deltaSignatureFailure = -0.20
	deltaCustodyRefusal = -0.05
	deltaDeliveryFailure = -0.02
	deltaDeliverySuccess = +0.01

	defaultScore = 1.0
	epsilon      = 1e-12
)

// NodeReputationService aggregates per-UHID behavioural signals into a score
// in the range [0.0, 1.0]. Unknown peers start at 1.0 (benefit of the doubt).
// All methods are safe for concurrent use.
type NodeReputationService struct {
	mu     sync.RWMutex
	scores map[string]float64
}

// NewNodeReputationService returns a new, empty NodeReputationService.
func NewNodeReputationService() *NodeReputationService {
	return &NodeReputationService{
		scores: make(map[string]float64),
	}
}

// RecordRreqFloodAttempt applies the RREQ-flood penalty (-0.05) to uhid.
func (s *NodeReputationService) RecordRreqFloodAttempt(uhid string) {
	s.apply(uhid, deltaRreqFlood)
}

// RecordReplayAttempt applies the replay-attempt penalty (-0.15) to uhid.
func (s *NodeReputationService) RecordReplayAttempt(uhid string) {
	s.apply(uhid, deltaReplayAttempt)
}

// RecordSignatureFailure applies the signature-failure penalty (-0.20) to uhid.
func (s *NodeReputationService) RecordSignatureFailure(uhid string) {
	s.apply(uhid, deltaSignatureFailure)
}

// RecordCustodyRefusal applies the custody-refusal penalty (-0.05) to uhid.
func (s *NodeReputationService) RecordCustodyRefusal(uhid string) {
	s.apply(uhid, deltaCustodyRefusal)
}

// RecordDeliverySuccess applies the delivery-success reward (+0.01) to uhid.
// The roundTripMs parameter is accepted for future use but not currently used
// in score calculation.
func (s *NodeReputationService) RecordDeliverySuccess(uhid string, roundTripMs int) {
	s.apply(uhid, deltaDeliverySuccess)
}

// RecordDeliveryFailure applies the delivery-failure penalty (-0.02) to uhid.
func (s *NodeReputationService) RecordDeliveryFailure(uhid string) {
	s.apply(uhid, deltaDeliveryFailure)
}

// GetReputationScore returns the current score for uhid. Unknown peers return
// 1.0 (benefit of the doubt).
func (s *NodeReputationService) GetReputationScore(uhid string) float64 {
	s.mu.RLock()
	v, ok := s.scores[uhid]
	s.mu.RUnlock()
	if !ok {
		return defaultScore
	}
	return v
}

// ApplyWeightedDelta clamps weightedDelta to [-1, 1] then adds it to uhid's
// current score (seeded at 1.0 for unknown peers), storing the result clamped
// to [0, 1]. This is used by gossip propagation to blend remote signals with
// the reporter's own reputation weight.
func (s *NodeReputationService) ApplyWeightedDelta(uhid string, weightedDelta float64) {
	// Clamp the incoming delta to [-1, 1].
	d := math.Max(-1.0, math.Min(1.0, weightedDelta))
	s.mu.Lock()
	current, ok := s.scores[uhid]
	if !ok {
		current = defaultScore
	}
	s.scores[uhid] = math.Max(0, math.Min(1, current+d))
	s.mu.Unlock()
}

// GetAllScores returns a snapshot copy of all recorded scores.
func (s *NodeReputationService) GetAllScores() map[string]float64 {
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := make(map[string]float64, len(s.scores))
	for k, v := range s.scores {
		out[k] = v
	}
	return out
}

// apply adds delta to uhid's score (seeding from defaultScore if unknown) and
// stores the clamped result.
func (s *NodeReputationService) apply(uhid string, delta float64) {
	s.mu.Lock()
	current, ok := s.scores[uhid]
	if !ok {
		current = defaultScore
	}
	s.scores[uhid] = clampScore(current + delta)
	s.mu.Unlock()
}

// clampScore clamps v to [0.0, 1.0] with epsilon snap:
//   - if v < 1e-12  → 0.0
//   - if v > 1.0 - 1e-12 → 1.0
func clampScore(v float64) float64 {
	if v < epsilon {
		return 0.0
	}
	if v > 1.0-epsilon {
		return 1.0
	}
	return v
}
