// SPDX-License-Identifier: MIT

package bandwidth

import (
	"sync"
	"testing"
	"time"
)

// ── BandwidthEstimator tests ─────────────────────────────────────────────────

func TestEstimator_InitialState(t *testing.T) {
	e := NewBandwidthEstimator("BLE", 2_000_000)
	s := e.CurrentSample()

	if s.TransportName != "BLE" {
		t.Errorf("TransportName = %q, want %q", s.TransportName, "BLE")
	}
	if s.Confidence != ConfidenceNone {
		t.Errorf("initial Confidence = %v, want None", s.Confidence)
	}
	if s.BtlBwBps <= 0 {
		t.Errorf("initial BtlBwBps should be positive (seeded from maxBandwidthBps), got %d", s.BtlBwBps)
	}
}

func TestEstimator_RecordDelivery_UpdatesConfidence(t *testing.T) {
	e := NewBandwidthEstimator("Wi-Fi Direct", 100_000_000)

	// Feed 5 deliveries → should advance to at least Low confidence.
	now := time.Now().UnixMicro()
	for i := 0; i < 5; i++ {
		send := now + int64(i)*1_000_000
		deliver := send + 10_000 // 10 ms one-way
		e.RecordDelivery(1500, send, deliver)
	}

	s := e.CurrentSample()
	if s.Confidence < ConfidenceLow {
		t.Errorf("after 5 deliveries confidence = %v, want >= Low", s.Confidence)
	}
}

func TestEstimator_RecordLoss_IncreasesLossRate(t *testing.T) {
	e := NewBandwidthEstimator("BLE", 2_000_000)

	initial := e.CurrentSample().LossRate

	e.RecordLoss(1500)
	e.RecordLoss(1500)
	e.RecordLoss(1500)

	after := e.CurrentSample().LossRate
	if after <= initial {
		t.Errorf("LossRate after losses = %f, want > initial %f", after, initial)
	}
	if after <= 0 {
		t.Errorf("LossRate should be > 0 after recording losses, got %f", after)
	}
}

func TestEstimator_RecordProbeResult_UpdatesRTT(t *testing.T) {
	e := NewBandwidthEstimator("BLE", 2_000_000)

	baseUs := time.Now().UnixMicro()
	ack := BandwidthProbeAck{
		Sequence:          1,
		SenderSendUs:      baseUs,
		ReceiverReceiveUs: baseUs + 30_000, // 30 ms forward OWD
		ReceiverSendUs:    baseUs + 30_100,
		SenderReceiveUs:   baseUs + 60_000, // 60 ms total − 100 µs processing = ~59.9 ms RTT
		ProbeBytes:        1200,
	}

	e.RecordProbeResult(ack, baseUs+60_000)

	s := e.CurrentSample()
	if s.Srtt <= 0 {
		t.Errorf("Srtt should be positive after probe, got %v", s.Srtt)
	}
	if s.RtProp <= 0 {
		t.Errorf("RtProp should be positive after probe, got %v", s.RtProp)
	}
}

func TestEstimator_WarmFromGossip_SeedsWhenNone(t *testing.T) {
	e := NewBandwidthEstimator("NearLink", 10_000_000)

	// Fresh estimator has ConfidenceNone and probeRounds = 0 — gossip should seed it.
	e.WarmFromGossip(5_000_000, 20*time.Millisecond, ConfidenceLow)

	s := e.CurrentSample()
	if s.Confidence < ConfidenceLow {
		t.Errorf("after gossip warm Confidence = %v, want >= Low", s.Confidence)
	}
	if s.Srtt <= 0 {
		t.Errorf("Srtt should be seeded from gossip rtProp, got %v", s.Srtt)
	}
}

func TestEstimator_WarmFromGossip_NeverDowngrades(t *testing.T) {
	e := NewBandwidthEstimator("BLE", 2_000_000)

	// Record enough deliveries to reach High confidence.
	now := time.Now().UnixMicro()
	for i := 0; i < 25; i++ {
		send := now + int64(i)*1_000_000
		deliver := send + 5_000
		e.RecordDelivery(1500, send, deliver)
	}

	before := e.CurrentSample().Confidence
	if before < ConfidenceHigh {
		t.Fatalf("pre-condition failed: expected High confidence after 25 rounds, got %v", before)
	}

	// Gossip should be ignored because probeRounds > 0.
	e.WarmFromGossip(1, 1*time.Millisecond, ConfidenceNone)
	after := e.CurrentSample().Confidence

	if after < before {
		t.Errorf("WarmFromGossip downgraded confidence from %v to %v", before, after)
	}
}

func TestEstimator_ApplyPhyHint_WeakSignalCapsEstimate(t *testing.T) {
	e := NewBandwidthEstimator("BLE", 2_000_000)

	// Seed a high BtlBw via delivery records.
	now := time.Now().UnixMicro()
	for i := 0; i < 5; i++ {
		send := now + int64(i)*100_000
		deliver := send + 1_000 // 1 ms → very high delivery rate
		e.RecordDelivery(100_000, send, deliver)
	}

	// Apply a very weak signal hint (< -95 dBm → cap at 40 kbps).
	e.ApplyPhyHint(-100)

	s := e.CurrentSample()
	// PHY cap at -100 dBm is 40_000 bps.
	if s.BtlBwBps > 40_000 {
		t.Errorf("BtlBwBps should be capped to PHY hint 40000 bps, got %d", s.BtlBwBps)
	}
	if s.PhyCapBps != 40_000 {
		t.Errorf("PhyCapBps = %d, want 40000", s.PhyCapBps)
	}
}

func TestProbeAck_Rtt_ClockSyncFree(t *testing.T) {
	// Arrange timestamps so RTT = (60000 - 0) - (30100 - 30000) = 59900 µs ≈ 59.9 ms
	ack := BandwidthProbeAck{
		SenderSendUs:      0,
		ReceiverReceiveUs: 30_000,
		ReceiverSendUs:    30_100,
		SenderReceiveUs:   60_000,
		ProbeBytes:        1200,
	}
	rtt := ack.Rtt()
	wantUs := int64(59_900)
	gotUs := rtt.Microseconds()
	if gotUs != wantUs {
		t.Errorf("Rtt() = %d µs, want %d µs", gotUs, wantUs)
	}
}

func TestBandwidthSample_Rto_ClampedToMinimum(t *testing.T) {
	s := BandwidthSample{
		Srtt:   1 * time.Millisecond,
		RttVar: 0,
	}
	rto := s.Rto()
	if rto < 200*time.Millisecond {
		t.Errorf("Rto() = %v, want >= 200ms (RFC 6298 minimum)", rto)
	}
}

func TestBandwidthSample_Rto_UsesMax(t *testing.T) {
	// With high SRTT/RTTVAR the RTO should be large but clamped to 60 s.
	s := BandwidthSample{
		Srtt:   30 * time.Second,
		RttVar: 10 * time.Second,
	}
	rto := s.Rto()
	if rto > 60*time.Second {
		t.Errorf("Rto() = %v, want <= 60s (RFC 6298 maximum)", rto)
	}
}

func TestEstimator_OnSampleImproved_Fires(t *testing.T) {
	e := NewBandwidthEstimator("BLE", 2_000_000)

	var mu sync.Mutex
	var received []BandwidthSample

	e.OnSampleImproved(func(s BandwidthSample) {
		mu.Lock()
		received = append(received, s)
		mu.Unlock()
	})

	// Trigger improvements via RecordLoss (changes snapshot).
	now := time.Now().UnixMicro()
	e.RecordDelivery(1500, now, now+100_000)

	// Give any callbacks a moment to execute.
	time.Sleep(10 * time.Millisecond)

	mu.Lock()
	n := len(received)
	mu.Unlock()

	if n == 0 {
		t.Error("OnSampleImproved callback was not called after RecordDelivery")
	}
}

// ── BandwidthDirector tests ──────────────────────────────────────────────────

func TestDirector_GetEstimate_UnknownPeer_ReturnsNil(t *testing.T) {
	d := NewBandwidthDirector()
	got := d.GetEstimate("unknown-peer", "BLE")
	if got != nil {
		t.Errorf("GetEstimate for unknown peer = %+v, want nil", got)
	}
}

func TestDirector_ApplyGossip_SeedsMatrix(t *testing.T) {
	d := NewBandwidthDirector()
	e := NewBandwidthEstimator("BLE", 2_000_000)
	d.Register(e)

	payload := BandwidthGossipPayload{
		PeerUhid:      "peer-abc",
		TransportName: "BLE",
		BtlBwBps:      1_000_000,
		RtPropUs:      20_000, // 20 ms
		Confidence:    ConfidenceLow,
		MeasuredAt:    time.Now().UTC(),
	}
	d.ApplyGossip(payload)

	got := d.GetEstimate("peer-abc", "BLE")
	if got == nil {
		t.Fatal("GetEstimate returned nil after ApplyGossip")
	}
}

func TestDirector_GetEstimates_OrderedByAvailableBps(t *testing.T) {
	d := NewBandwidthDirector()

	// Manually seed the matrix with two transports.
	d.mu.Lock()
	d.matrix[matrixKey{"peer-xyz", "ble"}] = BandwidthSample{
		TransportName: "BLE",
		AvailableBps:  500_000,
	}
	d.matrix[matrixKey{"peer-xyz", "wi-fi direct"}] = BandwidthSample{
		TransportName: "Wi-Fi Direct",
		AvailableBps:  50_000_000,
	}
	d.mu.Unlock()

	estimates := d.GetEstimates("peer-xyz")
	if len(estimates) != 2 {
		t.Fatalf("GetEstimates returned %d entries, want 2", len(estimates))
	}
	if estimates[0].AvailableBps < estimates[1].AvailableBps {
		t.Errorf("results not sorted descending: [0].AvailableBps=%d < [1].AvailableBps=%d",
			estimates[0].AvailableBps, estimates[1].AvailableBps)
	}
}

func TestDirector_RecommendTransport_OnlyBLE_ReturnsBLE(t *testing.T) {
	d := NewBandwidthDirector()
	e := NewBandwidthEstimator("BLE", 2_000_000)
	d.Register(e)

	// Seed matrix with one BLE entry for the peer.
	d.mu.Lock()
	d.matrix[matrixKey{"peer-1", "ble"}] = BandwidthSample{
		TransportName: "BLE",
		AvailableBps:  1_000_000,
		BdpBytes:      2500,
		Confidence:    ConfidenceLow,
	}
	d.mu.Unlock()

	got := d.RecommendTransport("peer-1", 1024)
	if got != "BLE" {
		t.Errorf("RecommendTransport = %q, want %q", got, "BLE")
	}
}

func TestDirector_BuildGossipPayload_NoConfidence_ReturnsNil(t *testing.T) {
	d := NewBandwidthDirector()
	e := NewBandwidthEstimator("BLE", 2_000_000)
	d.Register(e)

	// Estimator has ConfidenceNone — payload should be nil.
	got := d.BuildGossipPayload("peer-2", "BLE")
	if got != nil {
		t.Errorf("BuildGossipPayload with ConfidenceNone = %+v, want nil", got)
	}
}

func TestDirector_ApplyGossip_NeverDowngrades(t *testing.T) {
	d := NewBandwidthDirector()
	e := NewBandwidthEstimator("BLE", 2_000_000)
	d.Register(e)

	// Warm via probe rounds so we have > 0 probeRounds.
	now := time.Now().UnixMicro()
	for i := 0; i < 5; i++ {
		send := now + int64(i)*1_000_000
		deliver := send + 10_000
		e.RecordDelivery(1500, send, deliver)
	}
	beforeConf := e.CurrentSample().Confidence

	// Apply gossip with ConfidenceNone — should be ignored by WarmFromGossip.
	d.ApplyGossip(BandwidthGossipPayload{
		PeerUhid:      "peer-X",
		TransportName: "BLE",
		BtlBwBps:      1,
		RtPropUs:      1,
		Confidence:    ConfidenceNone,
		MeasuredAt:    time.Now(),
	})

	afterConf := e.CurrentSample().Confidence
	if afterConf < beforeConf {
		t.Errorf("ApplyGossip downgraded confidence from %v to %v", beforeConf, afterConf)
	}
}

// ── NodeActivityMonitor tests ────────────────────────────────────────────────

func TestMonitor_InitialState_Offline(t *testing.T) {
	m := NewNodeActivityMonitor()
	s := m.Current()

	if s.State != NodeOffline {
		t.Errorf("initial State = %v, want Offline", s.State)
	}
	if s.IngressBps != 0 || s.EgressBps != 0 {
		t.Errorf("initial rates should be zero, got ingress=%d egress=%d", s.IngressBps, s.EgressBps)
	}
}

func TestMonitor_Subscribe_FiresSnapshots(t *testing.T) {
	m := NewNodeActivityMonitor()
	m.SampleIntervalMs = 50 // fast for tests
	m.IdleThresholdSeconds = 5

	e := NewBandwidthEstimator("BLE", 2_000_000)
	m.Register("BLE", e)

	var mu sync.Mutex
	var snapshots []NodeActivitySnapshot

	unsub := m.Subscribe(func(s NodeActivitySnapshot) {
		mu.Lock()
		snapshots = append(snapshots, s)
		mu.Unlock()
	})
	defer unsub()

	m.Start()
	defer m.Stop()

	// Wait for at least two ticks.
	time.Sleep(200 * time.Millisecond)

	mu.Lock()
	n := len(snapshots)
	mu.Unlock()

	if n < 1 {
		t.Errorf("Subscribe fired %d snapshots in 200ms with 50ms interval, want >= 1", n)
	}
}

func TestMonitor_Unsubscribe_StopsCallbacks(t *testing.T) {
	m := NewNodeActivityMonitor()
	m.SampleIntervalMs = 50

	e := NewBandwidthEstimator("BLE", 2_000_000)
	m.Register("BLE", e)

	var mu sync.Mutex
	count := 0

	unsub := m.Subscribe(func(s NodeActivitySnapshot) {
		mu.Lock()
		count++
		mu.Unlock()
	})

	m.Start()
	time.Sleep(120 * time.Millisecond)

	unsub() // unsubscribe

	mu.Lock()
	countAfterUnsub := count
	mu.Unlock()

	// Wait another interval; count must not increase.
	time.Sleep(120 * time.Millisecond)
	m.Stop()

	mu.Lock()
	countFinal := count
	mu.Unlock()

	if countFinal > countAfterUnsub {
		t.Errorf("callback was called %d times after unsubscribe (was %d before)",
			countFinal-countAfterUnsub, countAfterUnsub)
	}
}

func TestNodeActivitySnapshot_HasActivity(t *testing.T) {
	cases := []struct {
		state    NodeActivityState
		wantHas  bool
	}{
		{NodeOffline, false},
		{NodeIdle, false},
		{NodeActive, true},
		{NodeBusy, true},
		{NodeDegraded, true},
	}

	for _, tc := range cases {
		s := NodeActivitySnapshot{State: tc.state}
		got := s.HasActivity()
		if got != tc.wantHas {
			t.Errorf("State=%v HasActivity()=%v, want %v", tc.state, got, tc.wantHas)
		}
	}
}

func TestMonitor_RecordIngress_UpdatesSnapshot(t *testing.T) {
	m := NewNodeActivityMonitor()
	m.SampleIntervalMs = 50

	e := NewBandwidthEstimator("BLE", 2_000_000)
	m.Register("BLE", e)

	var mu sync.Mutex
	var received []NodeActivitySnapshot

	m.Subscribe(func(s NodeActivitySnapshot) {
		mu.Lock()
		received = append(received, s)
		mu.Unlock()
	})

	m.Start()
	defer m.Stop()

	// Inject ingress traffic.
	for i := 0; i < 10; i++ {
		m.RecordIngress("BLE", 10_000)
	}

	time.Sleep(200 * time.Millisecond)

	mu.Lock()
	var hasIngress bool
	for _, s := range received {
		if s.IngressBps > 0 {
			hasIngress = true
			break
		}
	}
	mu.Unlock()

	if !hasIngress {
		t.Error("expected at least one snapshot with IngressBps > 0 after RecordIngress")
	}
}

func TestMonitor_RecordEgressToPeer_CountsActivePeers(t *testing.T) {
	m := NewNodeActivityMonitor()
	m.SampleIntervalMs = 50
	m.IdleThresholdSeconds = 5

	e := NewBandwidthEstimator("BLE", 2_000_000)
	m.Register("BLE", e)

	var mu sync.Mutex
	var received []NodeActivitySnapshot

	m.Subscribe(func(s NodeActivitySnapshot) {
		mu.Lock()
		received = append(received, s)
		mu.Unlock()
	})

	m.Start()
	defer m.Stop()

	// Two distinct peers send traffic to this node.
	m.RecordEgressToPeer("BLE", "peer-A", 10_000)
	m.RecordEgressToPeer("BLE", "peer-B", 10_000)

	time.Sleep(200 * time.Millisecond)

	mu.Lock()
	var maxPeers int
	for _, s := range received {
		if s.ActivePeers > maxPeers {
			maxPeers = s.ActivePeers
		}
	}
	mu.Unlock()

	if maxPeers < 2 {
		t.Errorf("expected ActivePeers >= 2 after egress to 2 distinct peers, got %d", maxPeers)
	}
}

func TestMonitor_RecordEgress_WithoutPeer_NoActivePeers(t *testing.T) {
	m := NewNodeActivityMonitor()
	m.SampleIntervalMs = 50
	m.IdleThresholdSeconds = 5

	e := NewBandwidthEstimator("BLE", 2_000_000)
	m.Register("BLE", e)

	var mu sync.Mutex
	var received []NodeActivitySnapshot

	m.Subscribe(func(s NodeActivitySnapshot) {
		mu.Lock()
		received = append(received, s)
		mu.Unlock()
	})

	m.Start()
	defer m.Stop()

	// Transport-only egress (no peer) must not contribute to the peer count.
	for i := 0; i < 10; i++ {
		m.RecordEgress("BLE", 10_000)
	}

	time.Sleep(200 * time.Millisecond)

	mu.Lock()
	defer mu.Unlock()
	for _, s := range received {
		if s.ActivePeers != 0 {
			t.Errorf("expected ActivePeers == 0 for transport-only egress, got %d", s.ActivePeers)
		}
	}
}

// ── Packet type constants (regression check) ─────────────────────────────────

// TestPacketTypes_Values validates the packet type constants added to the protocol package.
// This test lives here because it validates the ABMF additions; the constants themselves
// are in the protocol package (see go/protocol/packet.go).
func TestProbeAck_ForwardOwd(t *testing.T) {
	ack := BandwidthProbeAck{
		SenderSendUs:      1000,
		ReceiverReceiveUs: 31000,
	}
	got := ack.ForwardOwd()
	want := 30 * time.Millisecond
	if got != want {
		t.Errorf("ForwardOwd() = %v, want %v", got, want)
	}
}

func TestBandwidthSample_EffectiveBps(t *testing.T) {
	s := BandwidthSample{BtlBwBps: 10_000_000, PhyCapBps: 500_000}
	if s.EffectiveBps() != 500_000 {
		t.Errorf("EffectiveBps() = %d, want 500000 (limited by PHY cap)", s.EffectiveBps())
	}

	s2 := BandwidthSample{BtlBwBps: 1_000_000, PhyCapBps: 0}
	if s2.EffectiveBps() != 1_000_000 {
		t.Errorf("EffectiveBps() with no PHY cap = %d, want 1000000", s2.EffectiveBps())
	}
}

func TestTransportActivitySnapshot_UtilizationPercent(t *testing.T) {
	ts := TransportActivitySnapshot{UtilizationFraction: 0.34}
	got := ts.UtilizationPercent()
	want := "34 %"
	if got != want {
		t.Errorf("UtilizationPercent() = %q, want %q", got, want)
	}
}
