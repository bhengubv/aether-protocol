// SPDX-License-Identifier: MIT
// Predictive transport selector — 2-state Kalman filter over PerTransportMetrics.
//
// Why Kalman over EWMA?
// ─────────────────────
// EWMA is a 1-pole IIR: it smooths past measurements but cannot predict future RTT
// when a link is actively degrading.  The Kalman filter models RTT as a
// constant-velocity process [rtt, drift]:
//
//	x_t = F * x_{t−1} + w   (F = [[1,1],[0,1]])
//	z_t = H * x_t   + v    (H = [1,0])
//
// Predicting rising RTT (positive drift) BEFORE it exceeds a threshold lets the
// selector switch to a calmer transport proactively.
//
// Score formula:
//
//	score = (effectiveBps / powerCost) × (1 − lossRate) / max(kalmanRtt, 1) × (1 / (1 + σ/100))
//
// where σ = sqrt(kalmanVariance) normalised to [0,1] by dividing by 100 ms.

package transport

import (
	"math"
	"sort"
	"sync"
)

// ── kalmanRttFilter ───────────────────────────────────────────────────────────

// kalmanRttFilter is a 2-state Kalman filter estimating RTT and drift for one
// transport link.  Not exported — used exclusively by PredictiveTransportSelector.
//
// NOT thread-safe; callers must hold the selector's write lock before calling update.
type kalmanRttFilter struct {
	// Tuning constants.
	qRtt   float64 // process noise for RTT (Q[0,0]), default 25 ms²
	qDrift float64 // process noise for drift (Q[1,1]), default 5 ms²
	r      float64 // observation noise variance R, default 100 ms²

	// State: x = [rtt; drift].
	rtt   float64 // estimated RTT in ms
	drift float64 // estimated RTT drift (ms per sample)

	// Covariance P (2×2 symmetric): stored as upper-triangle scalars.
	p00 float64
	p01 float64
	p11 float64
}

func newKalmanRttFilter(initialRttMs float64) *kalmanRttFilter {
	return &kalmanRttFilter{
		qRtt:   25.0,
		qDrift: 5.0,
		r:      100.0,
		rtt:    initialRttMs,
		drift:  0.0,
		p00:    400.0,
		p01:    0.0,
		p11:    100.0,
	}
}

// update incorporates a new RTT measurement and returns the updated RTT estimate.
func (f *kalmanRttFilter) update(measuredRttMs float64) float64 {
	// ── 1. Predict ────────────────────────────────────────────────────────────
	rttPred := f.rtt + f.drift
	driftPred := f.drift

	// P_pred = F * P * F^T + Q  (F = [[1,1],[0,1]])
	pp00 := f.p00 + 2.0*f.p01 + f.p11 + f.qRtt
	pp01 := f.p01 + f.p11
	pp11 := f.p11 + f.qDrift

	// ── 2. Kalman gain (H = [1, 0]) ──────────────────────────────────────────
	// S = H * P_pred * H^T + R = pp00 + R
	s := pp00 + f.r
	k0 := pp00 / s
	k1 := pp01 / s

	// ── 3. Update ─────────────────────────────────────────────────────────────
	innovation := measuredRttMs - rttPred
	f.rtt = rttPred + k0*innovation
	f.drift = driftPred + k1*innovation

	// P = (I − K*H) * P_pred
	f.p00 = (1.0 - k0) * pp00
	f.p01 = (1.0 - k0) * pp01
	f.p11 = -k1*pp01 + pp11

	// Clamp to prevent numerical drift below zero.
	if f.p00 < 1e-6 {
		f.p00 = 1e-6
	}
	if f.p11 < 1e-6 {
		f.p11 = 1e-6
	}

	return f.rtt
}

// ── PredictiveTransportSelector ───────────────────────────────────────────────

// RankedTransportPredictive pairs a transport with its Kalman-predictive score.
type RankedTransportPredictive struct {
	Transport      TransportService
	Score          float64
	PredictedRttMs float64
	RttVariance    float64
}

// PredictiveTransportSelector maintains a per-transport Kalman RTT filter and
// ranks transports using a composite score that penalises both high predicted
// RTT and high RTT uncertainty.
//
// Thread-safe: all exported methods acquire the internal RWMutex.
type PredictiveTransportSelector struct {
	mu      sync.RWMutex
	filters map[TransportService]*kalmanRttFilter
}

// NewPredictiveTransportSelector returns an empty selector ready for registration.
func NewPredictiveTransportSelector() *PredictiveTransportSelector {
	return &PredictiveTransportSelector{
		filters: make(map[TransportService]*kalmanRttFilter),
	}
}

// Register adds a transport with an initial RTT prior.
// Must be called once per transport before ObserveMetrics or Rank.
func (s *PredictiveTransportSelector) Register(t TransportService, initialRttMs float64) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.filters[t]; !ok {
		s.filters[t] = newKalmanRttFilter(initialRttMs)
	}
}

// Unregister removes a transport and discards its Kalman state.
func (s *PredictiveTransportSelector) Unregister(t TransportService) {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.filters, t)
}

// ObserveMetrics feeds a new RTT measurement into the Kalman filter for the
// transport and also records the sample in the transport's PerTransportMetrics.
//
// Both rttMs ≤ 0 and failed sends are excluded from the Kalman update (they
// carry no useful signal about propagation delay) but are still forwarded to
// PerTransportMetrics so the loss-rate EWMA stays accurate.
func (s *PredictiveTransportSelector) ObserveMetrics(
	t TransportService, rttMs int64, success bool, bytesTransferred int64,
) {
	// Forward to the transport's own EWMA store.
	if m := t.Metrics(); m != nil {
		m.RecordSample(rttMs, success, bytesTransferred)
	}

	if rttMs <= 0 || !success {
		return
	}

	// kalmanRttFilter.update is not itself guarded; acquire write lock.
	s.mu.Lock()
	if f, ok := s.filters[t]; ok {
		f.update(float64(rttMs))
	}
	s.mu.Unlock()
}

// Rank returns transports in descending predictive-score order.
//
// Only available transports are included.  payloadBytes is used to exclude
// transports whose max bandwidth would require > 30 s to serialise the payload.
func (s *PredictiveTransportSelector) Rank(payloadBytes int) []RankedTransportPredictive {
	s.mu.RLock()
	defer s.mu.RUnlock()

	result := make([]RankedTransportPredictive, 0, len(s.filters))

	for t, f := range s.filters {
		if !t.IsAvailable() {
			continue
		}

		// Exclude transports too slow for this payload (30 s ceiling).
		if bw := t.MaxBandwidthBps(); bw > 0 {
			serialSec := float64(payloadBytes) * 8.0 / float64(bw)
			if serialSec > 30.0 {
				continue
			}
		}

		kalmanRtt := math.Max(f.rtt, 1.0)
		variance := f.p00
		stddev := math.Sqrt(variance)
		power := math.Max(float64(t.PowerCostRelative()), 1.0)

		var lossRate, effectiveBps float64
		bw := t.MaxBandwidthBps()

		if m := t.Metrics(); m != nil {
			lossRate = m.EwmaLossRate()
			effectiveBps = math.Max(m.EwmaThroughputBps(), float64(bw)*0.1)
		} else {
			lossRate = 0.05
			effectiveBps = float64(bw) * 0.1
		}

		reliabilityFactor := 1.0 / (1.0 + stddev/100.0)
		score := (effectiveBps / power) * (1.0 - lossRate) / kalmanRtt * reliabilityFactor

		result = append(result, RankedTransportPredictive{
			Transport:      t,
			Score:          score,
			PredictedRttMs: kalmanRtt,
			RttVariance:    variance,
		})
	}

	sort.Slice(result, func(i, j int) bool {
		return result[i].Score > result[j].Score
	})
	return result
}

// SelectBest returns the highest-scoring available transport for payloadBytes,
// or nil if no transports are registered and available.
func (s *PredictiveTransportSelector) SelectBest(payloadBytes int) TransportService {
	ranked := s.Rank(payloadBytes)
	if len(ranked) == 0 {
		return nil
	}
	return ranked[0].Transport
}

// KalmanState returns the current (rttMs, driftMs, variance) tuple for a
// registered transport, plus ok=true.  Returns zeros and ok=false when the
// transport is not registered.
func (s *PredictiveTransportSelector) KalmanState(
	t TransportService,
) (rttMs, driftMs, variance float64, ok bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	f, found := s.filters[t]
	if !found {
		return 0, 0, 0, false
	}
	return f.rtt, f.drift, f.p00, true
}
