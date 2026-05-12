// SPDX-License-Identifier: MIT

package reputation

import (
	"sync"
	"time"
)

// AnomalyDetectorOptions configures the thresholds and windows used by
// BehavioralAnomalyDetector.
type AnomalyDetectorOptions struct {
	// VolumeWindowMs is the rolling window length (ms) for volume spike detection.
	VolumeWindowMs int64
	// VolumeSpikeMultiplier is the factor above the EWMA baseline that triggers
	// a flood signal.
	VolumeSpikeMultiplier float64
	// EwmaAlpha is the smoothing factor (0 < α ≤ 1) for the EWMA baseline.
	EwmaAlpha float64
	// ScatterWindowMs is the lookback window (ms) for destination-scatter detection.
	ScatterWindowMs int64
	// ScatterThreshold is the number of unique destinations within ScatterWindowMs
	// that triggers a flood signal.
	ScatterThreshold int
	// GeohashPrefixLength is the number of leading characters compared when
	// checking for a geohash mismatch.
	GeohashPrefixLength int
	// GeohashRateLimitMs is the minimum gap (ms) between successive geohash
	// mismatch signals for the same UHID.  0 means every mismatch fires.
	GeohashRateLimitMs int64
}

// DefaultAnomalyDetectorOptions returns the production defaults.
func DefaultAnomalyDetectorOptions() AnomalyDetectorOptions {
	return AnomalyDetectorOptions{
		VolumeWindowMs:        30_000,
		VolumeSpikeMultiplier: 5.0,
		EwmaAlpha:             0.20,
		ScatterWindowMs:       60_000,
		ScatterThreshold:      50,
		GeohashPrefixLength:   4,
		GeohashRateLimitMs:    60_000,
	}
}

// ---------------------------------------------------------------------------
// per-source internal state
// ---------------------------------------------------------------------------

type volumeState struct {
	mu          sync.Mutex
	windowStart int64 // -1 = not yet initialised
	windowCount int
	ewma        float64
	hasBaseline bool
}

type scatterEntry struct {
	dest string
	ts   int64
}

type scatterState struct {
	mu      sync.Mutex
	entries []scatterEntry
}

type geohashState struct {
	mu           sync.Mutex
	lastSignalMs int64 // -1 = never signalled
}

// ---------------------------------------------------------------------------
// BehavioralAnomalyDetector
// ---------------------------------------------------------------------------

// BehavioralAnomalyDetector observes per-source packet metadata and geohash
// claims, and fires reputation penalties via NodeReputationService when
// anomalies are detected.
//
// All exported methods are safe for concurrent use.
type BehavioralAnomalyDetector struct {
	rep  *NodeReputationService
	opts AnomalyDetectorOptions

	// sync.Map[uhid string -> *volumeState]
	volumeStates sync.Map
	// sync.Map[uhid string -> *scatterState]
	scatterStates sync.Map
	// sync.Map[uhid string -> *geohashState]
	geohashStates sync.Map
}

// NewBehavioralAnomalyDetector returns a new detector using the given
// reputation service and options.
func NewBehavioralAnomalyDetector(rep *NodeReputationService, opts AnomalyDetectorOptions) *BehavioralAnomalyDetector {
	return &BehavioralAnomalyDetector{rep: rep, opts: opts}
}

// ObservePacket records a packet from sourceUhid to destinationUhid at
// timestampMs (Unix milliseconds) and fires reputation penalties if volume-
// spike or destination-scatter anomalies are detected.
func (d *BehavioralAnomalyDetector) ObservePacket(sourceUhid, destinationUhid string, timestampMs int64) {
	d.checkVolume(sourceUhid, timestampMs)
	d.checkScatter(sourceUhid, destinationUhid, timestampMs)
}

// ObserveGeohashClaim compares the first GeohashPrefixLength characters of
// claimedGeohash and observedRoutingGeohash.  If they differ and the rate
// limit allows, RecordSignatureFailure is fired for uhid.
//
// The current wall-clock time (Unix ms) is used as the signal timestamp.
func (d *BehavioralAnomalyDetector) ObserveGeohashClaim(uhid, claimedGeohash, observedRoutingGeohash string) {
	ts := time.Now().UnixMilli()
	d.checkGeohash(uhid, claimedGeohash, observedRoutingGeohash, ts)
}

// ObserveSpkSigFailure directly records a signature failure for uhid.
func (d *BehavioralAnomalyDetector) ObserveSpkSigFailure(uhid string) {
	d.rep.RecordSignatureFailure(uhid)
}

// ---------------------------------------------------------------------------
// volume spike
// ---------------------------------------------------------------------------

func (d *BehavioralAnomalyDetector) getVolumeState(uhid string) *volumeState {
	v, _ := d.volumeStates.LoadOrStore(uhid, &volumeState{windowStart: -1})
	return v.(*volumeState)
}

func (d *BehavioralAnomalyDetector) checkVolume(uhid string, ts int64) {
	vs := d.getVolumeState(uhid)
	vs.mu.Lock()
	defer vs.mu.Unlock()

	// Initialise window on first packet.
	if vs.windowStart == -1 {
		vs.windowStart = ts
		vs.windowCount = 1
		return
	}

	if ts-vs.windowStart >= d.opts.VolumeWindowMs {
		// Roll window: update EWMA baseline.
		if !vs.hasBaseline {
			vs.ewma = float64(vs.windowCount)
			vs.hasBaseline = true
		} else {
			alpha := d.opts.EwmaAlpha
			vs.ewma = alpha*float64(vs.windowCount) + (1-alpha)*vs.ewma
		}

		// Fire flood signal when count exceeded multiplier × positive baseline.
		if vs.hasBaseline && vs.ewma > 0 && float64(vs.windowCount) > d.opts.VolumeSpikeMultiplier*vs.ewma {
			d.rep.RecordRreqFloodAttempt(uhid)
		}

		// Reset window.
		vs.windowStart = ts
		vs.windowCount = 1
	} else {
		vs.windowCount++
	}
}

// ---------------------------------------------------------------------------
// destination scatter
// ---------------------------------------------------------------------------

func (d *BehavioralAnomalyDetector) getScatterState(uhid string) *scatterState {
	v, _ := d.scatterStates.LoadOrStore(uhid, &scatterState{})
	return v.(*scatterState)
}

func (d *BehavioralAnomalyDetector) checkScatter(sourceUhid, destUhid string, ts int64) {
	ss := d.getScatterState(sourceUhid)
	ss.mu.Lock()
	defer ss.mu.Unlock()

	// Append new entry.
	ss.entries = append(ss.entries, scatterEntry{dest: destUhid, ts: ts})

	// Prune entries outside the scatter window.
	cutoff := ts - d.opts.ScatterWindowMs
	trimmed := ss.entries[:0]
	for _, e := range ss.entries {
		if e.ts > cutoff {
			trimmed = append(trimmed, e)
		}
	}
	ss.entries = trimmed

	// Count unique destinations within the window.
	seen := make(map[string]struct{}, len(ss.entries))
	for _, e := range ss.entries {
		seen[e.dest] = struct{}{}
	}
	if len(seen) > d.opts.ScatterThreshold {
		d.rep.RecordRreqFloodAttempt(sourceUhid)
	}
}

// ---------------------------------------------------------------------------
// geohash mismatch
// ---------------------------------------------------------------------------

func (d *BehavioralAnomalyDetector) getGeohashState(uhid string) *geohashState {
	v, _ := d.geohashStates.LoadOrStore(uhid, &geohashState{lastSignalMs: -1})
	return v.(*geohashState)
}

func (d *BehavioralAnomalyDetector) checkGeohash(uhid, claimed, observed string, ts int64) {
	n := d.opts.GeohashPrefixLength
	if geohashPrefix(claimed, n) == geohashPrefix(observed, n) {
		return
	}

	gs := d.getGeohashState(uhid)
	gs.mu.Lock()
	defer gs.mu.Unlock()

	if gs.lastSignalMs == -1 || ts-gs.lastSignalMs >= d.opts.GeohashRateLimitMs {
		d.rep.RecordSignatureFailure(uhid)
		gs.lastSignalMs = ts
	}
}

// ---------------------------------------------------------------------------
// helpers
// ---------------------------------------------------------------------------

// geohashPrefix returns the first n runes of s (or all of s if shorter).
func geohashPrefix(s string, n int) string {
	if n <= 0 {
		return ""
	}
	r := []rune(s)
	if len(r) <= n {
		return s
	}
	return string(r[:n])
}
