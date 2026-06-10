// SPDX-License-Identifier: MIT

package bandwidth

import (
	"math"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// NodeActivityMonitor is the UI-facing sampler of the ABMF.
//
// It runs a background goroutine at SampleIntervalMs (default 500 ms).
// Each tick computes ingress/egress rates from atomic byte counters, reads
// per-transport estimates from registered BandwidthEstimators, and publishes
// a NodeActivitySnapshot.
//
// Rate computation: byte deltas are divided by the elapsed wall-clock interval.
// The sample interval acts as the averaging window (allocation-free on the hot path).
//
// All methods are safe for concurrent use.
type NodeActivityMonitor struct {
	// SampleIntervalMs controls how often the monitor re-samples (milliseconds).
	// Default: 500. Must be set before Start() for best results; changing after
	// Start() takes effect on the next tick.
	SampleIntervalMs int

	// IdleThresholdSeconds controls how long without traffic before a transport
	// is considered idle. Default: 5.
	IdleThresholdSeconds int

	mu         sync.RWMutex
	transports map[string]*transportEntry // keyed by lower-case name

	// lastSeenPeerMs maps peerUhid → last-seen Unix ms. A peer is "active" if it
	// had ingress or egress within IdleThresholdSeconds. Populated only by the
	// peer-aware RecordIngressFromPeer/RecordEgressToPeer methods; the
	// transport-only RecordIngress/RecordEgress do not contribute (the caller did
	// not supply a peer). Stale entries are pruned each tick so the map stays
	// bounded by the count of recently-active peers, not the lifetime peer set.
	peerMu         sync.Mutex
	lastSeenPeerMs map[string]int64

	// current is the last published snapshot (pointer; swapped atomically).
	currentMu sync.RWMutex
	current   *NodeActivitySnapshot

	// subscription management
	subMu   sync.Mutex
	subs    map[uint64]func(NodeActivitySnapshot)
	nextSub uint64

	stopCh chan struct{}
	doneCh chan struct{}
}

type transportEntry struct {
	name      string
	estimator *BandwidthEstimator

	// Atomic byte counters reset every tick.
	ingressBytes int64
	egressBytes  int64

	// Last egress timestamp in Unix ms.
	lastEgressMs int64
}

// NewNodeActivityMonitor returns a monitor ready to be configured and started.
func NewNodeActivityMonitor() *NodeActivityMonitor {
	return &NodeActivityMonitor{
		SampleIntervalMs:     500,
		IdleThresholdSeconds: 5,
		transports:           make(map[string]*transportEntry),
		lastSeenPeerMs:       make(map[string]int64),
		current:              offlineSnapshot(),
		subs:                 make(map[uint64]func(NodeActivitySnapshot)),
	}
}

// Register adds a transport's estimator so its activity is included in snapshots.
func (m *NodeActivityMonitor) Register(name string, estimator *BandwidthEstimator) {
	key := strings.ToLower(name)
	entry := &transportEntry{
		name:         name,
		estimator:    estimator,
		lastEgressMs: time.Now().UnixMilli(),
	}
	m.mu.Lock()
	m.transports[key] = entry
	m.mu.Unlock()
}

// RecordIngress records inbound bytes on the named transport.
// Safe to call from any goroutine; uses atomic addition.
func (m *NodeActivityMonitor) RecordIngress(transport string, bytes int) {
	key := strings.ToLower(transport)
	m.mu.RLock()
	entry, ok := m.transports[key]
	m.mu.RUnlock()
	if ok {
		atomic.AddInt64(&entry.ingressBytes, int64(bytes))
	}
}

// RecordEgress records outbound bytes on the named transport.
// Safe to call from any goroutine; uses atomic addition.
func (m *NodeActivityMonitor) RecordEgress(transport string, bytes int) {
	key := strings.ToLower(transport)
	m.mu.RLock()
	entry, ok := m.transports[key]
	m.mu.RUnlock()
	if ok {
		atomic.AddInt64(&entry.egressBytes, int64(bytes))
		atomic.StoreInt64(&entry.lastEgressMs, time.Now().UnixMilli())
	}
}

// RecordIngressFromPeer records inbound bytes on the named transport from a
// specific peer. It updates the transport counters via RecordIngress and tracks
// the peer for the NodeActivitySnapshot.ActivePeers count.
// Safe to call from any goroutine.
func (m *NodeActivityMonitor) RecordIngressFromPeer(transport, peerUhid string, bytes int) {
	m.RecordIngress(transport, bytes)
	m.recordPeerSeen(peerUhid)
}

// RecordEgressToPeer records outbound bytes on the named transport to a specific
// peer. It updates the transport counters via RecordEgress and tracks the peer
// for the NodeActivitySnapshot.ActivePeers count.
// Safe to call from any goroutine.
func (m *NodeActivityMonitor) RecordEgressToPeer(transport, peerUhid string, bytes int) {
	m.RecordEgress(transport, bytes)
	m.recordPeerSeen(peerUhid)
}

// recordPeerSeen stamps peerUhid with the current Unix ms. Empty UHIDs are ignored.
func (m *NodeActivityMonitor) recordPeerSeen(peerUhid string) {
	if peerUhid == "" {
		return
	}
	now := time.Now().UnixMilli()
	m.peerMu.Lock()
	m.lastSeenPeerMs[peerUhid] = now
	m.peerMu.Unlock()
}

// Start launches the background sampling goroutine.
// Calling Start more than once has no effect.
func (m *NodeActivityMonitor) Start() {
	m.mu.Lock()
	if m.stopCh != nil {
		m.mu.Unlock()
		return
	}
	m.stopCh = make(chan struct{})
	m.doneCh = make(chan struct{})
	m.mu.Unlock()

	go m.loop()
}

// Stop halts the background goroutine and waits for it to exit.
func (m *NodeActivityMonitor) Stop() {
	m.mu.Lock()
	stopCh := m.stopCh
	doneCh := m.doneCh
	m.stopCh = nil
	m.mu.Unlock()

	if stopCh != nil {
		close(stopCh)
		<-doneCh
	}
}

// Current returns the most recent snapshot.
// Never nil after construction — initialised to an Offline snapshot with zero rates.
func (m *NodeActivityMonitor) Current() NodeActivitySnapshot {
	m.currentMu.RLock()
	s := *m.current
	m.currentMu.RUnlock()
	return s
}

// Subscribe registers a callback that fires every time a new snapshot is published.
// The returned function removes the subscription; calling it more than once is safe.
func (m *NodeActivityMonitor) Subscribe(fn func(NodeActivitySnapshot)) func() {
	m.subMu.Lock()
	id := m.nextSub
	m.nextSub++
	m.subs[id] = fn
	m.subMu.Unlock()

	return func() {
		m.subMu.Lock()
		delete(m.subs, id)
		m.subMu.Unlock()
	}
}

// loop is the background sampling goroutine.
func (m *NodeActivityMonitor) loop() {
	// Capture channels once at start; they are set before Start() returns.
	m.mu.Lock()
	stopCh := m.stopCh
	doneCh := m.doneCh
	m.mu.Unlock()

	defer close(doneCh)

	lastTickMs := time.Now().UnixMilli()
	for {
		interval := time.Duration(m.SampleIntervalMs) * time.Millisecond

		select {
		case <-stopCh:
			return
		case <-time.After(interval):
		}

		nowMillis := time.Now().UnixMilli()
		elapsedSec := math.Max(0.001, float64(nowMillis-lastTickMs)/1000.0)
		lastTickMs = nowMillis

		m.tick(elapsedSec, nowMillis)
	}
}

// tick computes one snapshot and publishes it.
func (m *NodeActivityMonitor) tick(elapsedSec float64, nowMillis int64) {
	m.mu.RLock()
	idleThreshMs := int64(m.IdleThresholdSeconds) * 1000
	entries := make([]*transportEntry, 0, len(m.transports))
	for _, e := range m.transports {
		entries = append(entries, e)
	}
	m.mu.RUnlock()

	// Count distinct peers active within the idle window; prune stale entries so
	// the map stays bounded by recently-active peers rather than the lifetime set.
	activePeers := 0
	m.peerMu.Lock()
	for uhid, lastSeen := range m.lastSeenPeerMs {
		if nowMillis-lastSeen < idleThreshMs {
			activePeers++
		} else {
			delete(m.lastSeenPeerMs, uhid)
		}
	}
	m.peerMu.Unlock()

	var totalIngress, totalEgress int64
	var activeTransports int
	var transportSnaps []TransportActivitySnapshot

	for _, entry := range entries {
		ingressDelta := atomic.SwapInt64(&entry.ingressBytes, 0)
		egressDelta := atomic.SwapInt64(&entry.egressBytes, 0)

		ingressBps := int64(float64(ingressDelta) * 8.0 / elapsedSec)
		egressBps := int64(float64(egressDelta) * 8.0 / elapsedSec)

		s := entry.estimator.CurrentSample()
		utilFraction := 0.0
		if s.BtlBwBps > 0 {
			utilFraction = clampFloat64(float64(egressBps)/float64(s.BtlBwBps), 0.0, 1.0)
		}

		lastEgress := atomic.LoadInt64(&entry.lastEgressMs)
		isRecent := (nowMillis - lastEgress) < idleThreshMs
		state := computeTransportState(egressBps, ingressBps, s, isRecent)

		if state != NodeOffline && state != NodeIdle {
			activeTransports++
		}

		totalIngress += ingressBps
		totalEgress += egressBps

		transportSnaps = append(transportSnaps, TransportActivitySnapshot{
			TransportName:       entry.name,
			IsAvailable:         true,
			IngressBps:          ingressBps,
			EgressBps:           egressBps,
			Srtt:                s.Srtt,
			BtlBwBps:            s.BtlBwBps,
			UtilizationFraction: utilFraction,
			State:               state,
			Confidence:          s.Confidence,
		})
	}

	nodeState := computeNodeState(transportSnaps)

	primaryName := ""
	var primaryBps int64 = -1
	for _, ts := range transportSnaps {
		if ts.EgressBps > primaryBps {
			primaryBps = ts.EgressBps
			primaryName = ts.TransportName
		}
	}
	if nodeState == NodeOffline || nodeState == NodeIdle {
		primaryName = ""
	}

	snapshot := &NodeActivitySnapshot{
		State:                nodeState,
		IngressBps:           totalIngress,
		EgressBps:            totalEgress,
		ActivePeers:          activePeers,
		ActiveTransports:     activeTransports,
		Transports:           transportSnaps,
		PrimaryTransportName: primaryName,
		Timestamp:            time.Now().UTC(),
	}

	prev := m.Current()
	m.currentMu.Lock()
	m.current = snapshot
	m.currentMu.Unlock()

	// Fire subscribers.
	m.subMu.Lock()
	cbs := make([]func(NodeActivitySnapshot), 0, len(m.subs))
	for _, fn := range m.subs {
		cbs = append(cbs, fn)
	}
	m.subMu.Unlock()

	s := *snapshot
	for _, fn := range cbs {
		safeCall(fn, s)
	}

	// Suppress if identical (state/rates/transport count unchanged).
	_ = prev // available for change detection if callers need it
}

// ── State computation ────────────────────────────────────────────────────────

func computeTransportState(
	egressBps, ingressBps int64,
	s BandwidthSample,
	isRecent bool,
) NodeActivityState {
	if !isRecent && egressBps == 0 && ingressBps == 0 {
		return NodeIdle
	}
	if egressBps == 0 && ingressBps == 0 {
		return NodeIdle
	}
	if s.LossRate > 0.05 {
		return NodeDegraded
	}
	util := 0.0
	if s.BtlBwBps > 0 {
		util = float64(egressBps) / float64(s.BtlBwBps)
	}
	if util >= 0.5 {
		return NodeBusy
	}
	return NodeActive
}

func computeNodeState(transports []TransportActivitySnapshot) NodeActivityState {
	if len(transports) == 0 {
		return NodeOffline
	}
	hasDegraded, hasBusy, hasActive, allOffline := false, false, false, true
	for _, t := range transports {
		switch t.State {
		case NodeDegraded:
			hasDegraded = true
			allOffline = false
		case NodeBusy:
			hasBusy = true
			allOffline = false
		case NodeActive:
			hasActive = true
			allOffline = false
		case NodeIdle:
			allOffline = false
		}
	}
	switch {
	case hasDegraded:
		return NodeDegraded
	case hasBusy:
		return NodeBusy
	case hasActive:
		return NodeActive
	case allOffline:
		return NodeOffline
	default:
		return NodeIdle
	}
}

// offlineSnapshot is the zero-value snapshot returned before any data flows.
func offlineSnapshot() *NodeActivitySnapshot {
	return &NodeActivitySnapshot{
		State:     NodeOffline,
		Timestamp: time.Now().UTC(),
	}
}

// safeCall invokes fn, swallowing any panics so subscriber errors cannot kill the loop.
func safeCall(fn func(NodeActivitySnapshot), s NodeActivitySnapshot) {
	defer func() { recover() }() //nolint:errcheck
	fn(s)
}

