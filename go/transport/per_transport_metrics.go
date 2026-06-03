// SPDX-License-Identifier: MIT

package transport

import (
	"math"
	"sync"
)

const ewmaAlpha = 0.20 // EWMA smoothing factor — matches Python reference impl (α=0.2)

// PerTransportMetrics tracks real-time EWMA metrics for one transport link.
//
// Conservative initial priors ensure transports without observations still
// participate in ranking (via their declared max bandwidth) but rank below
// transports that have real measurements.
//
// Initial priors:
//
//	ewmaRttMs         = 200 ms  (unknown link — pessimistic)
//	ewmaLossRate      = 0.05    (5 % assumed until real data arrives)
//	ewmaThroughputBps = 0       (bootstrapped on first successful sample)
//
// Thread-safe: all reads and writes hold the embedded mutex.
type PerTransportMetrics struct {
	mu                sync.Mutex
	sampleCount       int64
	ewmaRttMs         float64
	ewmaLossRate      float64
	ewmaThroughputBps float64
}

// NewPerTransportMetrics creates a PerTransportMetrics with conservative priors.
func NewPerTransportMetrics() *PerTransportMetrics {
	return &PerTransportMetrics{
		ewmaRttMs:    200.0,
		ewmaLossRate: 0.05,
	}
}

// RecordSample updates EWMA state from one send observation.
//
// rttMs:            measured round-trip time in ms; ≤0 skips the RTT update.
// success:          whether the peer acknowledged receipt.
// bytesTransferred: payload bytes on wire; used for throughput EWMA.
func (m *PerTransportMetrics) RecordSample(rttMs int64, success bool, bytesTransferred int64) {
	m.mu.Lock()
	defer m.mu.Unlock()

	m.sampleCount++

	if rttMs > 0 {
		m.ewmaRttMs = ewmaAlpha*float64(rttMs) + (1.0-ewmaAlpha)*m.ewmaRttMs
	}

	lossObs := 0.0
	if !success {
		lossObs = 1.0
	}
	m.ewmaLossRate = ewmaAlpha*lossObs + (1.0-ewmaAlpha)*m.ewmaLossRate

	if success && rttMs > 0 && bytesTransferred > 0 {
		tputBps := float64(bytesTransferred) * 8.0 * 1_000.0 / float64(rttMs)
		if m.ewmaThroughputBps < 1.0 {
			// Bootstrap from zero: first successful sample sets the baseline.
			m.ewmaThroughputBps = tputBps
		} else {
			m.ewmaThroughputBps = ewmaAlpha*tputBps + (1.0-ewmaAlpha)*m.ewmaThroughputBps
		}
	}
}

// EwmaRttMs returns the EWMA round-trip time in milliseconds (lower = better).
func (m *PerTransportMetrics) EwmaRttMs() float64 {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.ewmaRttMs
}

// EwmaLossRate returns the EWMA packet-loss rate in [0, 1] (lower = better).
func (m *PerTransportMetrics) EwmaLossRate() float64 {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.ewmaLossRate
}

// EwmaThroughputBps returns the EWMA throughput in bits per second (higher = better).
func (m *PerTransportMetrics) EwmaThroughputBps() float64 {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.ewmaThroughputBps
}

// SampleCount returns the total number of samples recorded.
func (m *PerTransportMetrics) SampleCount() int64 {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.sampleCount
}

// CompositeScore returns a transport ranking score (higher = better).
//
// Formula:
//
//	(effectiveBps / powerCost) × (1 − lossRate) / max(rttMs, 1)
//
// where effectiveBps = max(ewmaThroughputBps, maxBandwidthBps × 0.1) so that
// transports with no throughput history still rank by their declared capacity.
func (m *PerTransportMetrics) CompositeScore(maxBandwidthBps int64, powerCostRelative int32) float64 {
	m.mu.Lock()
	defer m.mu.Unlock()

	power := math.Max(float64(powerCostRelative), 1.0)
	rtt := math.Max(m.ewmaRttMs, 1.0)
	loss := m.ewmaLossRate
	tput := m.ewmaThroughputBps

	effectiveBps := math.Max(tput, float64(maxBandwidthBps)*0.1)
	return (effectiveBps / power) * (1.0 - loss) / rtt
}
