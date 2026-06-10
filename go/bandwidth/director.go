// SPDX-License-Identifier: MIT

package bandwidth

import (
	"sort"
	"strings"
	"sync"
	"time"
)

// BandwidthDirector is the cross-transport bandwidth synthesis and gossip coordinator.
//
// It maintains a matrix of (peerUhid × transportName) → BandwidthSample estimates
// and provides transport recommendations based on payload size, BDP, and power cost.
//
// Transport selection algorithm:
//  1. Score = AvailableBps / PowerCostRelative (higher is better).
//  2. If payload > BDP: prefer the transport with the largest BDP (reduces round-trips).
//  3. Penalise transports with ConfidenceNone by 50 % (untrusted estimate).
//
// All methods are safe for concurrent use.
type BandwidthDirector struct {
	mu         sync.RWMutex
	matrix     map[matrixKey]BandwidthSample
	estimators map[string]*BandwidthEstimator // keyed by lower-case transport name
}

type matrixKey struct {
	peer      string
	transport string
}

// defaultPowerCosts mirrors ITransportService conventions (lower = preferred).
var defaultPowerCosts = map[string]int{
	"nearlink":     1,
	"ble":          2,
	"wi-fi direct": 3,
	"circlelink":   3,
	"quic relay":   10,
	"http relay":   10,
}

// NewBandwidthDirector returns a ready-to-use BandwidthDirector.
func NewBandwidthDirector() *BandwidthDirector {
	return &BandwidthDirector{
		matrix:     make(map[matrixKey]BandwidthSample),
		estimators: make(map[string]*BandwidthEstimator),
	}
}

// Register wires an estimator into the director.
// When the estimator fires a SampleImproved callback the director updates every
// known peer's entry for that transport in the matrix.
func (d *BandwidthDirector) Register(estimator *BandwidthEstimator) {
	key := strings.ToLower(estimator.TransportName())

	d.mu.Lock()
	d.estimators[key] = estimator
	d.mu.Unlock()

	estimator.OnSampleImproved(func(s BandwidthSample) {
		d.mu.Lock()
		defer d.mu.Unlock()
		tLower := strings.ToLower(s.TransportName)
		for k := range d.matrix {
			if k.transport == tLower {
				d.matrix[k] = s
			}
		}
	})
}

// GetEstimate returns the bandwidth estimate for a specific peer on a specific transport.
// Returns nil if no estimate exists yet.
func (d *BandwidthDirector) GetEstimate(peerUhid, transportName string) *BandwidthSample {
	key := matrixKey{
		peer:      peerUhid,
		transport: strings.ToLower(transportName),
	}
	d.mu.RLock()
	s, ok := d.matrix[key]
	d.mu.RUnlock()
	if !ok {
		return nil
	}
	return &s
}

// GetEstimates returns all current estimates for a peer across all transports,
// ranked by AvailableBps descending.
func (d *BandwidthDirector) GetEstimates(peerUhid string) []BandwidthSample {
	d.mu.RLock()
	var out []BandwidthSample
	for k, s := range d.matrix {
		if k.peer == peerUhid {
			out = append(out, s)
		}
	}
	d.mu.RUnlock()

	sort.Slice(out, func(i, j int) bool {
		return out[i].AvailableBps > out[j].AvailableBps
	})
	return out
}

// RecommendTransport returns the best transport name for the given peer and payload size.
// Returns an empty string if no transports are registered.
func (d *BandwidthDirector) RecommendTransport(peerUhid string, payloadBytes int64) string {
	candidates := d.GetEstimates(peerUhid)
	if len(candidates) == 0 {
		// No measurement data yet — fall back to lowest power-cost registered transport.
		d.mu.RLock()
		defer d.mu.RUnlock()
		best := ""
		bestCost := int(^uint(0) >> 1) // MaxInt
		for _, e := range d.estimators {
			cost := powerCost(e.TransportName())
			if cost < bestCost {
				bestCost = cost
				best = e.TransportName()
			}
		}
		return best
	}

	var bestName string
	bestScore := -1.0
	for _, s := range candidates {
		pc := float64(powerCost(s.TransportName))
		available := float64(s.AvailableBps)

		// Prefer larger BDP for large payloads.
		bdpBonus := 1.0
		if payloadBytes <= s.BdpBytes {
			bdpBonus = 1.5
		}

		// Penalise untrusted estimates.
		confidenceFactor := 1.0
		if s.Confidence == ConfidenceNone {
			confidenceFactor = 0.5
		}

		score := (available / pc) * bdpBonus * confidenceFactor
		if score > bestScore {
			bestScore = score
			bestName = s.TransportName
		}
	}
	return bestName
}

// BuildGossipPayload constructs a gossip payload to send to a new peer.
// Returns nil if the estimator has ConfidenceNone (no data worth gossiping).
func (d *BandwidthDirector) BuildGossipPayload(peerUhid, transportName string) *BandwidthGossipPayload {
	key := strings.ToLower(transportName)
	d.mu.RLock()
	estimator, ok := d.estimators[key]
	d.mu.RUnlock()
	if !ok {
		return nil
	}

	s := estimator.CurrentSample()
	if s.Confidence == ConfidenceNone {
		return nil
	}

	return &BandwidthGossipPayload{
		PeerUhid:      peerUhid,
		TransportName: transportName,
		BtlBwBps:      s.BtlBwBps,
		RtPropUs:      s.RtProp.Microseconds(),
		Confidence:    s.Confidence,
		MeasuredAt:    s.MeasuredAt,
	}
}

// ApplyGossip receives and applies a gossip payload from a remote peer.
// The appropriate estimator is warmed from the payload, and the matrix is seeded
// so GetEstimate returns something even before probing begins.
func (d *BandwidthDirector) ApplyGossip(payload BandwidthGossipPayload) {
	key := strings.ToLower(payload.TransportName)
	d.mu.RLock()
	estimator, ok := d.estimators[key]
	d.mu.RUnlock()
	if !ok {
		return
	}

	estimator.WarmFromGossip(
		payload.BtlBwBps,
		time.Duration(payload.RtPropUs)*time.Microsecond,
		payload.Confidence,
	)

	// Seed the matrix so GetEstimate returns something immediately.
	mk := matrixKey{peer: payload.PeerUhid, transport: key}
	s := estimator.CurrentSample()
	d.mu.Lock()
	d.matrix[mk] = s
	d.mu.Unlock()
}

// powerCost looks up the relative power cost for a transport name.
// Unknown transports get cost 5 (middle of the range).
func powerCost(transportName string) int {
	if c, ok := defaultPowerCosts[strings.ToLower(transportName)]; ok {
		return c
	}
	return 5
}
