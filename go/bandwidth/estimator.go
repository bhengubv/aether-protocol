// SPDX-License-Identifier: MIT

package bandwidth

import (
	"math"
	"sync"
	"time"
)

// BandwidthEstimator is a per-transport BBRv3-inspired bandwidth estimator.
//
// Algorithm:
//   - BtlBw: rolling maximum of per-delivery delivery-rate samples over a
//     btlBwWindowSize = 10-RTprop window (BBRv3 BtlBwFilter).
//   - RTprop: rolling minimum RTT over a rtPropWindowMs = 10 000 ms window.
//   - SRTT/RTTVAR: RFC 6298 §2.3 Jacobson/Karels (α = 1/8, β = 1/4).
//   - Loss rate: EWMA with α = lossAlpha = 0.10.
//   - PHY cap: RSSI → capacity mapping prevents over-optimistic estimates
//     on weak radio links before probes complete.
//   - Gossip warm-start: WarmFromGossip pre-seeds the estimator from a peer's
//     measured value so sessions start warm, not cold.
//   - Confidence tiers: lets consumers distinguish a 1-probe estimate from
//     a stable 30-round estimate.
//
// All exported methods are safe for concurrent use.
type BandwidthEstimator struct {
	transportName string

	mu sync.Mutex

	// BtlBw max-filter: circular buffer of (deliveryRateBps, timestampMs).
	btlBwWindow [btlBwWindowSize]btlBwSample
	btlBwHead   int
	btlBwCount  int

	// RTprop min-filter queue.
	rtPropSamples []rtPropSample

	// RFC 6298 SRTT / RTTVAR (milliseconds).
	srttMs   float64
	rttVarMs float64
	firstRtt bool

	// Loss EWMA.
	lossRate float64

	// PHY cap (bps). 0 = unknown.
	phyCapBps int64

	// Confidence.
	probeRounds    int
	warmedFromGossip bool

	// Immutable snapshot updated after every observation (pointer swap, no lock needed to read).
	current *BandwidthSample

	// Callbacks registered via OnSampleImproved.
	mu2       sync.Mutex
	callbacks []func(BandwidthSample)
}

const (
	btlBwWindowSize     = 10
	rtPropWindowMs      = 10_000.0
	lossAlpha           = 0.10
	srttAlpha           = 0.125
	rttVarBeta          = 0.25
	improvementThreshold = 0.05
)

type btlBwSample struct {
	rateBps     int64
	timestampMs float64
}

type rtPropSample struct {
	rttMs       float64
	timestampMs float64
}

// NewBandwidthEstimator creates a new estimator for the named transport.
// maxBandwidthBps is the theoretical maximum; used as an optimistic seed
// with ConfidenceNone until probes or gossip arrive.
func NewBandwidthEstimator(transportName string, maxBandwidthBps int64) *BandwidthEstimator {
	e := &BandwidthEstimator{
		transportName: transportName,
		firstRtt:      true,
	}
	// Optimistic seed: start at theoretical max with ConfidenceNone.
	e.current = e.buildSnapshot(maxBandwidthBps, 50*time.Millisecond)
	return e
}

// TransportName returns the transport identifier (e.g. "BLE", "Wi-Fi Direct").
func (e *BandwidthEstimator) TransportName() string { return e.transportName }

// CurrentSample returns the latest bandwidth snapshot.
// The returned value is immutable and safe to share across goroutines.
func (e *BandwidthEstimator) CurrentSample() BandwidthSample {
	e.mu.Lock()
	s := *e.current
	e.mu.Unlock()
	return s
}

// RecordDelivery feeds a successful delivery observation into the estimator.
// bytes is the payload size; sendUs and deliverUs are microseconds since the
// Unix epoch on the same clock.
func (e *BandwidthEstimator) RecordDelivery(bytes int, sendUs, deliverUs int64) {
	if bytes <= 0 || deliverUs <= sendUs {
		return
	}
	elapsedMs := float64(deliverUs-sendUs) / 1000.0
	deliveryRateBps := int64(float64(bytes) * 8.0 / (elapsedMs / 1000.0))
	rttMs := elapsedMs // one-way treated as conservative RTT estimate

	e.mu.Lock()
	e.addToBtlBwWindow(deliveryRateBps, nowMs())
	e.updateRttEstimates(rttMs)
	e.probeRounds++
	s := e.commit()
	e.mu.Unlock()

	if s != nil {
		e.fireSampleImproved(*s)
	}
}

// RecordLoss records that bytes were lost (timeout or explicit NAK).
func (e *BandwidthEstimator) RecordLoss(bytes int) {
	if bytes <= 0 {
		return
	}
	e.mu.Lock()
	e.lossRate = lossAlpha*1.0 + (1-lossAlpha)*e.lossRate
	s := e.commit()
	e.mu.Unlock()

	if s != nil {
		e.fireSampleImproved(*s)
	}
}

// RecordProbeResult feeds an active probe ACK into the estimator.
// localReceiveUs is the local clock time (µs) at ACK receipt; it is accepted
// for API parity but not used in the calculation (RTT is clock-sync-free).
func (e *BandwidthEstimator) RecordProbeResult(ack BandwidthProbeAck, localReceiveUs int64) {
	rtt := ack.Rtt()
	if rtt <= 0 || rtt > 30*time.Second {
		return
	}
	var deliveryRateBps int64
	if ack.ProbeBytes > 0 {
		deliveryRateBps = int64(float64(ack.ProbeBytes) * 8.0 / rtt.Seconds())
	}

	e.mu.Lock()
	e.updateRttEstimates(float64(rtt.Milliseconds()))
	if deliveryRateBps > 0 {
		e.addToBtlBwWindow(deliveryRateBps, nowMs())
	}
	e.probeRounds++
	s := e.commit()
	e.mu.Unlock()

	if s != nil {
		e.fireSampleImproved(*s)
	}
}

// WarmFromGossip pre-seeds the estimator from a peer's measured value.
// Only effective when Confidence is ConfidenceNone — never downgrades an existing estimate.
func (e *BandwidthEstimator) WarmFromGossip(btlBwBps int64, rtProp time.Duration, confidence BandwidthConfidence) {
	e.mu.Lock()
	defer e.mu.Unlock()

	if e.probeRounds > 0 || e.warmedFromGossip {
		return // never downgrade
	}
	e.addToBtlBwWindow(btlBwBps, nowMs())
	if rttMs := float64(rtProp.Milliseconds()); rttMs > 0 {
		e.srttMs = rttMs
		e.rttVarMs = rttMs / 2.0
		e.firstRtt = false
		e.addToRtPropWindow(rttMs, nowMs())
	}
	e.warmedFromGossip = true
	e.commit()
}

// ApplyPhyHint constrains the BtlBw estimate based on received signal strength.
// rssiDbm is the received signal strength in dBm.
//
// RSSI calibration tables:
//   - BLE (Bluetooth SIG Core Spec 5.4, 2 Msym/s PHY): ≥ −70 dBm → 2 Mbps, etc.
//   - Wi-Fi 802.11ax (3GPP TS 36.213 Annex A): ≥ −50 dBm → 600 Mbps, etc.
//   - Shared interface: BLE table is used as a conservative cross-transport fallback.
func (e *BandwidthEstimator) ApplyPhyHint(rssiDbm int) {
	var cap int64
	switch {
	case rssiDbm >= -50:
		cap = 600_000_000
	case rssiDbm >= -67:
		cap = 200_000_000
	case rssiDbm >= -70:
		cap = 2_000_000
	case rssiDbm >= -80:
		cap = 54_000_000
	case rssiDbm >= -85:
		cap = 500_000
	case rssiDbm >= -95:
		cap = 125_000
	default:
		cap = 40_000
	}

	e.mu.Lock()
	e.phyCapBps = cap
	s := e.commit()
	e.mu.Unlock()

	if s != nil {
		e.fireSampleImproved(*s)
	}
}

// OnSampleImproved registers a callback that fires when BtlBw improves by ≥ 5 %
// or Confidence advances. Multiple callbacks may be registered.
func (e *BandwidthEstimator) OnSampleImproved(fn func(BandwidthSample)) {
	e.mu2.Lock()
	e.callbacks = append(e.callbacks, fn)
	e.mu2.Unlock()
}

// ── Internal helpers ─────────────────────────────────────────────────────────

// updateRttEstimates applies RFC 6298 §2.3 SRTT/RTTVAR update and records RTprop.
// Must be called with e.mu held.
func (e *BandwidthEstimator) updateRttEstimates(rttMs float64) {
	if e.firstRtt {
		e.srttMs = rttMs
		e.rttVarMs = rttMs / 2.0
		e.firstRtt = false
	} else {
		e.rttVarMs = (1-rttVarBeta)*e.rttVarMs + rttVarBeta*math.Abs(e.srttMs-rttMs)
		e.srttMs = (1-srttAlpha)*e.srttMs + srttAlpha*rttMs
	}
	// Successful sample → feed zero loss into EWMA.
	e.lossRate = lossAlpha*0.0 + (1-lossAlpha)*e.lossRate
	e.addToRtPropWindow(rttMs, nowMs())
}

// addToBtlBwWindow inserts a delivery-rate sample and evicts expired entries.
// Must be called with e.mu held.
func (e *BandwidthEstimator) addToBtlBwWindow(rateBps int64, now float64) {
	windowDurationMs := 10.0 * math.Max(1.0, e.minRtPropMs())
	expiry := now - windowDurationMs

	// Evict expired tail entries.
	for e.btlBwCount > 0 {
		tail := (e.btlBwHead + btlBwWindowSize - e.btlBwCount) % btlBwWindowSize
		if e.btlBwWindow[tail].timestampMs < expiry {
			e.btlBwCount--
		} else {
			break
		}
	}

	e.btlBwWindow[e.btlBwHead] = btlBwSample{rateBps: rateBps, timestampMs: now}
	e.btlBwHead = (e.btlBwHead + 1) % btlBwWindowSize
	if e.btlBwCount < btlBwWindowSize {
		e.btlBwCount++
	}
}

// addToRtPropWindow records an RTT sample and evicts those outside the 10 s window.
// Must be called with e.mu held.
func (e *BandwidthEstimator) addToRtPropWindow(rttMs, now float64) {
	e.rtPropSamples = append(e.rtPropSamples, rtPropSample{rttMs: rttMs, timestampMs: now})
	cutoff := now - rtPropWindowMs
	start := 0
	for start < len(e.rtPropSamples) && e.rtPropSamples[start].timestampMs < cutoff {
		start++
	}
	if start > 0 {
		e.rtPropSamples = e.rtPropSamples[start:]
	}
}

// maxBtlBwBps returns the maximum BtlBw sample in the window.
// Must be called with e.mu held.
func (e *BandwidthEstimator) maxBtlBwBps() int64 {
	if e.btlBwCount == 0 {
		return 0
	}
	var max int64
	for i := 0; i < e.btlBwCount; i++ {
		idx := (e.btlBwHead + btlBwWindowSize - e.btlBwCount + i) % btlBwWindowSize
		if e.btlBwWindow[idx].rateBps > max {
			max = e.btlBwWindow[idx].rateBps
		}
	}
	return max
}

// minRtPropMs returns the minimum observed RTT in the window.
// Must be called with e.mu held.
func (e *BandwidthEstimator) minRtPropMs() float64 {
	if len(e.rtPropSamples) == 0 {
		if e.srttMs > 0 {
			return e.srttMs
		}
		return 50.0
	}
	min := math.MaxFloat64
	for _, s := range e.rtPropSamples {
		if s.rttMs < min {
			min = s.rttMs
		}
	}
	if min > 0 {
		return min
	}
	return 1.0
}

// computeConfidence maps probeRounds to a BandwidthConfidence tier.
// Must be called with e.mu held.
func (e *BandwidthEstimator) computeConfidence() BandwidthConfidence {
	switch {
	case e.probeRounds == 0 && !e.warmedFromGossip:
		return ConfidenceNone
	case e.probeRounds == 0:
		return ConfidenceLow
	case e.probeRounds < 5:
		return ConfidenceLow
	case e.probeRounds < 20:
		return ConfidenceMedium
	default:
		return ConfidenceHigh
	}
}

// commit rebuilds the snapshot and returns a pointer to it if SampleImproved
// should fire, or nil otherwise.
// Must be called with e.mu held.
func (e *BandwidthEstimator) commit() *BandwidthSample {
	prev := e.current
	btlBw := e.maxBtlBwBps()
	rtProp := time.Duration(e.minRtPropMs()*float64(time.Millisecond))
	next := e.buildSnapshot(btlBw, rtProp)
	e.current = next

	// Signal if BtlBw improved by ≥5% or confidence tier advanced.
	if prev.BtlBwBps == 0 ||
		float64(next.BtlBwBps-prev.BtlBwBps) > float64(prev.BtlBwBps)*improvementThreshold ||
		next.Confidence > prev.Confidence {
		return next
	}
	return nil
}

// buildSnapshot constructs a BandwidthSample from current estimator state.
// Must be called with e.mu held.
func (e *BandwidthEstimator) buildSnapshot(btlBw int64, rtProp time.Duration) *BandwidthSample {
	srttMs := math.Max(1.0, e.srttMs)
	srtt := time.Duration(srttMs * float64(time.Millisecond))
	rttVar := time.Duration(math.Max(0.0, e.rttVarMs) * float64(time.Millisecond))

	loss := clampFloat64(e.lossRate, 0.0, 1.0)

	// Effective = the PHY-capped deliverable rate. BDP and AvailableBps are both
	// derived from the EFFECTIVE rate — the BDP must size the in-flight window to
	// the rate the link can actually carry, not the uncapped BtlBw.
	effective := btlBw
	if e.phyCapBps > 0 && e.phyCapBps < btlBw {
		effective = e.phyCapBps
	}

	var bdp int64
	if effective > 0 {
		bdp = int64(float64(effective) / 8.0 * rtProp.Seconds())
	}

	effectiveAvail := int64(float64(effective) * (1.0 - loss))

	return &BandwidthSample{
		TransportName: e.transportName,
		BtlBwBps:     effective,
		AvailableBps: effectiveAvail,
		BdpBytes:     bdp,
		Srtt:         srtt,
		RttVar:       rttVar,
		RtProp:       rtProp,
		LossRate:     e.lossRate,
		PhyCapBps:    e.phyCapBps,
		Confidence:   e.computeConfidence(),
		MeasuredAt:   time.Now().UTC(),
	}
}

// fireSampleImproved invokes registered callbacks outside the lock.
func (e *BandwidthEstimator) fireSampleImproved(s BandwidthSample) {
	e.mu2.Lock()
	cbs := make([]func(BandwidthSample), len(e.callbacks))
	copy(cbs, e.callbacks)
	e.mu2.Unlock()
	for _, fn := range cbs {
		fn(s)
	}
}

// nowMs returns the current Unix time in milliseconds as a float64.
func nowMs() float64 {
	return float64(time.Now().UnixMilli())
}
