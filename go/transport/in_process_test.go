// SPDX-License-Identifier: MIT

package transport_test

import (
	"context"
	"math"
	"testing"
	"time"

	"github.com/thegeeknetwork/aether-protocol-go/transport"
)

// floatEqual returns true if |a-b| < eps.
func floatEqual(a, b, eps float64) bool {
	return math.Abs(a-b) < eps
}

// ── NewInProcessTransport ────────────────────────────────────────────────────

func TestNewInProcessTransport_Name(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	if ipt.Name() != "InProcess" {
		t.Errorf("Name: got %q, want %q", ipt.Name(), "InProcess")
	}
}

func TestNewInProcessTransport_IsAvailable(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	if !ipt.IsAvailable() {
		t.Error("new transport should be available")
	}
}

func TestNewInProcessTransport_MaxBandwidthBpsPositive(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	if ipt.MaxBandwidthBps() <= 0 {
		t.Errorf("MaxBandwidthBps: got %d, want > 0", ipt.MaxBandwidthBps())
	}
}

func TestNewInProcessTransport_MaxRangeMetersPositive(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	if ipt.MaxRangeMeters() <= 0 {
		t.Errorf("MaxRangeMeters: got %d, want > 0", ipt.MaxRangeMeters())
	}
}

func TestNewInProcessTransport_PowerCostRelativePositive(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	if ipt.PowerCostRelative() <= 0 {
		t.Errorf("PowerCostRelative: got %d, want > 0", ipt.PowerCostRelative())
	}
}

func TestNewInProcessTransport_MaxConcurrentPeersPositive(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	if ipt.MaxConcurrentPeers() <= 0 {
		t.Errorf("MaxConcurrentPeers: got %d, want > 0", ipt.MaxConcurrentPeers())
	}
}

func TestNewInProcessTransport_MetricsNonNil(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	if ipt.Metrics() == nil {
		t.Error("Metrics should not be nil")
	}
}

func TestNewInProcessTransport_ImplementsTransportService(t *testing.T) {
	var _ transport.TransportService = transport.NewInProcessTransport()
}

// ── RegisterPeer ─────────────────────────────────────────────────────────────

func TestRegisterPeer_ReturnsChannel(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ch, err := ipt.RegisterPeer("alice", 10)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if ch == nil {
		t.Fatal("expected non-nil channel")
	}
}

func TestRegisterPeer_EmptyUhidErrors(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	_, err := ipt.RegisterPeer("", 10)
	if err == nil {
		t.Fatal("expected error for empty UHID, got nil")
	}
}

func TestRegisterPeer_MakesIsConnectedTrue(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	_, err := ipt.RegisterPeer("bob", 10)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !ipt.IsConnected("bob") {
		t.Error("IsConnected should return true after RegisterPeer")
	}
}

func TestRegisterPeer_ChannelHasCorrectBufferSize(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ch, _ := ipt.RegisterPeer("carol", 5)
	if cap(ch) != 5 {
		t.Errorf("channel buffer size: got %d, want 5", cap(ch))
	}
}

// ── IsConnected ───────────────────────────────────────────────────────────────

func TestIsConnected_FalseForUnregisteredPeer(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	if ipt.IsConnected("ghost") {
		t.Error("IsConnected should return false for unknown peer")
	}
}

func TestIsConnected_FalseAfterUnregister(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ipt.RegisterPeer("dave", 10)
	ipt.UnregisterPeer("dave")
	if ipt.IsConnected("dave") {
		t.Error("IsConnected should return false after UnregisterPeer")
	}
}

// ── SendAsync ─────────────────────────────────────────────────────────────────

func TestSendAsync_DeliversToPeerChannel(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ch, _ := ipt.RegisterPeer("bob", 10)

	payload := []byte{0xDE, 0xAD, 0xBE, 0xEF}
	ok, err := ipt.SendAsync(context.Background(), "bob", payload)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !ok {
		t.Fatal("expected true, got false")
	}

	select {
	case received := <-ch:
		if len(received) != len(payload) {
			t.Errorf("payload length: got %d, want %d", len(received), len(payload))
		}
		for i, b := range received {
			if b != payload[i] {
				t.Errorf("byte %d: got %02x, want %02x", i, b, payload[i])
			}
		}
	case <-time.After(100 * time.Millisecond):
		t.Fatal("timed out waiting for message on channel")
	}
}

func TestSendAsync_DeliveredDataIsDefensiveCopy(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ch, _ := ipt.RegisterPeer("bob", 10)

	original := []byte{0x01, 0x02}
	ipt.SendAsync(context.Background(), "bob", original)
	original[0] = 0xFF // mutate after send

	select {
	case received := <-ch:
		if received[0] == 0xFF {
			t.Error("SendAsync should deliver a copy, not reference the original slice")
		}
	case <-time.After(100 * time.Millisecond):
		t.Fatal("timed out")
	}
}

func TestSendAsync_ReturnsFalseForUnregisteredPeer(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ok, err := ipt.SendAsync(context.Background(), "ghost", []byte{0x01})
	if err == nil {
		t.Fatal("expected error for unregistered peer")
	}
	if ok {
		t.Error("expected false for unregistered peer")
	}
}

func TestSendAsync_ReturnsFalseForEmptyData(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ipt.RegisterPeer("bob", 10)
	ok, err := ipt.SendAsync(context.Background(), "bob", []byte{})
	if err == nil {
		t.Fatal("expected error for empty data")
	}
	if ok {
		t.Error("expected false for empty data")
	}
}

func TestSendAsync_ReturnsFalseForEmptyPeerUhid(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ok, err := ipt.SendAsync(context.Background(), "", []byte{0x01})
	if err == nil {
		t.Fatal("expected error for empty peer UHID")
	}
	if ok {
		t.Error("expected false for empty UHID")
	}
}

func TestSendAsync_ReturnsFalseWhenUnavailable(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ipt.RegisterPeer("bob", 10)
	ipt.Shutdown()
	ok, err := ipt.SendAsync(context.Background(), "bob", []byte{0x01})
	if err == nil {
		t.Fatal("expected error when transport unavailable")
	}
	if ok {
		t.Error("expected false when unavailable")
	}
}

func TestSendAsync_IncrementsMetricsSampleCount(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ipt.RegisterPeer("bob", 10)

	before := ipt.Metrics().SampleCount()
	ipt.SendAsync(context.Background(), "bob", []byte{0x01, 0x02})
	after := ipt.Metrics().SampleCount()

	if after <= before {
		t.Errorf("SampleCount did not increase: before=%d after=%d", before, after)
	}
}

func TestSendAsync_MultipleSendsAllDelivered(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ch, _ := ipt.RegisterPeer("bob", 20)

	for i := 0; i < 5; i++ {
		ipt.SendAsync(context.Background(), "bob", []byte{byte(i)})
	}

	for i := 0; i < 5; i++ {
		select {
		case msg := <-ch:
			if msg[0] != byte(i) {
				t.Errorf("message %d: got %d, want %d", i, msg[0], i)
			}
		case <-time.After(100 * time.Millisecond):
			t.Fatalf("timed out waiting for message %d", i)
		}
	}
}

// ── SendStreamAsync ───────────────────────────────────────────────────────────

func TestSendStreamAsync_DeliversToPeerChannel(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ch, _ := ipt.RegisterPeer("bob", 10)

	payload := []byte{0x01, 0x02, 0x03}
	ok, err := ipt.SendStreamAsync(context.Background(), "bob", payload)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !ok {
		t.Fatal("expected true")
	}

	select {
	case received := <-ch:
		if len(received) != 3 {
			t.Errorf("expected 3 bytes, got %d", len(received))
		}
	case <-time.After(100 * time.Millisecond):
		t.Fatal("timed out")
	}
}

func TestSendStreamAsync_ReturnsFalseForUnregisteredPeer(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ok, err := ipt.SendStreamAsync(context.Background(), "ghost", []byte{0x01})
	if err == nil {
		t.Fatal("expected error")
	}
	if ok {
		t.Error("expected false")
	}
}

// ── UnregisterPeer ────────────────────────────────────────────────────────────

func TestUnregisterPeer_ClosesChannel(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ch, _ := ipt.RegisterPeer("alice", 10)
	ipt.UnregisterPeer("alice")

	// A closed channel can be read from (returns zero value, ok=false)
	_, ok := <-ch
	if ok {
		t.Error("channel should be closed after UnregisterPeer")
	}
}

func TestUnregisterPeer_RemovesFromConnectedPeers(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ipt.RegisterPeer("alice", 10)
	ipt.UnregisterPeer("alice")
	if ipt.IsConnected("alice") {
		t.Error("IsConnected should be false after UnregisterPeer")
	}
}

func TestUnregisterPeer_NoPanicForUnknownPeer(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ipt.UnregisterPeer("ghost") // must not panic
}

// ── Shutdown ──────────────────────────────────────────────────────────────────

func TestShutdown_SetsUnavailable(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	if err := ipt.Shutdown(); err != nil {
		t.Fatalf("Shutdown error: %v", err)
	}
	if ipt.IsAvailable() {
		t.Error("IsAvailable should return false after Shutdown")
	}
}

func TestShutdown_ClosesPeerChannels(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	ch, _ := ipt.RegisterPeer("alice", 10)
	ipt.Shutdown()

	_, ok := <-ch
	if ok {
		t.Error("peer channel should be closed after Shutdown")
	}
}

func TestShutdown_ReturnsNilError(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	if err := ipt.Shutdown(); err != nil {
		t.Errorf("Shutdown returned non-nil error: %v", err)
	}
}

func TestShutdown_MultiplePeerChannelsAllClosed(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	chA, _ := ipt.RegisterPeer("a", 10)
	chB, _ := ipt.RegisterPeer("b", 10)
	chC, _ := ipt.RegisterPeer("c", 10)
	ipt.Shutdown()

	for name, ch := range map[string]chan []byte{"a": chA, "b": chB, "c": chC} {
		if _, ok := <-ch; ok {
			t.Errorf("channel for %q should be closed after Shutdown", name)
		}
	}
}

// ── Context cancellation ──────────────────────────────────────────────────────

func TestSendAsync_RespectsContextCancellation(t *testing.T) {
	ipt := transport.NewInProcessTransport()
	// Register a peer with buffer=0 so every send blocks
	ch, _ := ipt.RegisterPeer("bob", 0)
	_ = ch

	ctx, cancel := context.WithCancel(context.Background())
	cancel() // cancel immediately

	// With a full (zero-buffer) channel and cancelled context,
	// SendAsync should not hang and should return an error or false
	done := make(chan struct{})
	go func() {
		defer close(done)
		ipt.SendAsync(ctx, "bob", []byte{0x01})
	}()

	select {
	case <-done:
		// succeeded without blocking
	case <-time.After(500 * time.Millisecond):
		t.Error("SendAsync blocked on cancelled context")
	}
}

// ── PerTransportMetrics ───────────────────────────────────────────────────────

func TestPerTransportMetrics_InitialSampleCountZero(t *testing.T) {
	m := transport.NewPerTransportMetrics()
	if m.SampleCount() != 0 {
		t.Errorf("initial SampleCount: got %d, want 0", m.SampleCount())
	}
}

func TestPerTransportMetrics_InitialRttPrior(t *testing.T) {
	m := transport.NewPerTransportMetrics()
	rtt := m.EwmaRttMs()
	if rtt != 200.0 {
		t.Errorf("initial EwmaRttMs: got %f, want 200.0", rtt)
	}
}

func TestPerTransportMetrics_InitialLossRatePrior(t *testing.T) {
	m := transport.NewPerTransportMetrics()
	loss := m.EwmaLossRate()
	if loss != 0.05 {
		t.Errorf("initial EwmaLossRate: got %f, want 0.05", loss)
	}
}

func TestPerTransportMetrics_InitialThroughputZero(t *testing.T) {
	m := transport.NewPerTransportMetrics()
	if m.EwmaThroughputBps() != 0.0 {
		t.Errorf("initial EwmaThroughputBps: got %f, want 0.0", m.EwmaThroughputBps())
	}
}

func TestPerTransportMetrics_RecordSample_IncrementsSampleCount(t *testing.T) {
	m := transport.NewPerTransportMetrics()
	m.RecordSample(10, true, 100)
	if m.SampleCount() != 1 {
		t.Errorf("SampleCount after 1 sample: got %d, want 1", m.SampleCount())
	}
	m.RecordSample(20, true, 200)
	m.RecordSample(30, false, 0)
	if m.SampleCount() != 3 {
		t.Errorf("SampleCount after 3 samples: got %d, want 3", m.SampleCount())
	}
}

func TestPerTransportMetrics_RecordSample_UpdatesRtt(t *testing.T) {
	m := transport.NewPerTransportMetrics()
	m.RecordSample(100, true, 1000)
	rtt := m.EwmaRttMs()
	// EWMA: 0.2*100 + 0.8*200 = 20 + 160 = 180
	if !floatEqual(rtt, 180.0, 1e-9) {
		t.Errorf("EwmaRttMs after one 100ms sample: got %f, want 180.0", rtt)
	}
}

func TestPerTransportMetrics_RecordSample_UpdatesLossOnFailure(t *testing.T) {
	m := transport.NewPerTransportMetrics()
	m.RecordSample(0, false, 0)
	// EWMA loss: 0.2*1.0 + 0.8*0.05 = 0.2 + 0.04 = 0.24
	loss := m.EwmaLossRate()
	if !floatEqual(loss, 0.24, 1e-9) {
		t.Errorf("EwmaLossRate after failure: got %f, want ~0.24", loss)
	}
}

func TestPerTransportMetrics_RecordSample_UpdatesThroughput(t *testing.T) {
	m := transport.NewPerTransportMetrics()
	m.RecordSample(10, true, 1000) // 1000 bytes in 10ms → 800 kbps
	tput := m.EwmaThroughputBps()
	// Expected: 1000 * 8 * 1000 / 10 = 800000 bps (bootstrapped, no prior)
	if tput <= 0 {
		t.Errorf("EwmaThroughputBps should be > 0 after success, got %f", tput)
	}
}

func TestPerTransportMetrics_RecordSample_NoThroughputOnFailure(t *testing.T) {
	m := transport.NewPerTransportMetrics()
	m.RecordSample(10, false, 0)
	if m.EwmaThroughputBps() != 0.0 {
		t.Error("throughput should remain 0 after a failed sample")
	}
}

func TestPerTransportMetrics_CompositeScore_PositiveForAvailableTransport(t *testing.T) {
	m := transport.NewPerTransportMetrics()
	m.RecordSample(10, true, 1000)
	score := m.CompositeScore(1_000_000, 1)
	if score <= 0 {
		t.Errorf("CompositeScore should be > 0, got %f", score)
	}
}

func TestPerTransportMetrics_CompositeScore_HigherBandwidthBetter(t *testing.T) {
	m1 := transport.NewPerTransportMetrics()
	m1.RecordSample(10, true, 1000)
	score1 := m1.CompositeScore(1_000_000, 1)

	m2 := transport.NewPerTransportMetrics()
	m2.RecordSample(10, true, 1000)
	score2 := m2.CompositeScore(10_000_000, 1)

	if score2 <= score1 {
		t.Errorf("higher MaxBandwidthBps should produce higher score: score1=%f, score2=%f", score1, score2)
	}
}

func TestPerTransportMetrics_CompositeScore_HigherPowerCostWorse(t *testing.T) {
	m1 := transport.NewPerTransportMetrics()
	m1.RecordSample(10, true, 1000)
	score1 := m1.CompositeScore(1_000_000, 1)

	m2 := transport.NewPerTransportMetrics()
	m2.RecordSample(10, true, 1000)
	score2 := m2.CompositeScore(1_000_000, 100)

	if score2 >= score1 {
		t.Errorf("higher power cost should produce lower score: score1=%f, score2=%f", score1, score2)
	}
}

// ── Rank ──────────────────────────────────────────────────────────────────────

func TestRank_ExcludesUnavailableTransports(t *testing.T) {
	available := transport.NewInProcessTransport()
	unavailable := transport.NewInProcessTransport()
	unavailable.Shutdown()

	ranked := transport.Rank([]transport.TransportService{available, unavailable})
	if len(ranked) != 1 {
		t.Errorf("Rank should include only available transports: got %d", len(ranked))
	}
}

func TestRank_OrdersHighestScoreFirst(t *testing.T) {
	// Two identical transports; record a success on one to boost its score
	t1 := transport.NewInProcessTransport()
	t2 := transport.NewInProcessTransport()
	t1.RegisterPeer("bob", 10)
	t2.RegisterPeer("bob", 10)

	// Make t1 look worse by recording a failure
	t1.Metrics().RecordSample(0, false, 0)
	t1.Metrics().RecordSample(0, false, 0)
	// t2 has a success
	t2.Metrics().RecordSample(5, true, 10000)

	ranked := transport.Rank([]transport.TransportService{t1, t2})
	if len(ranked) < 2 {
		t.Fatal("expected 2 ranked transports")
	}
	if ranked[0].Score < ranked[1].Score {
		t.Error("first ranked transport should have higher or equal score than second")
	}
}

func TestRank_EmptyListReturnsEmpty(t *testing.T) {
	ranked := transport.Rank(nil)
	if len(ranked) != 0 {
		t.Errorf("Rank of nil should return empty slice, got %d", len(ranked))
	}
}

func TestRank_AllUnavailableReturnsEmpty(t *testing.T) {
	t1 := transport.NewInProcessTransport()
	t2 := transport.NewInProcessTransport()
	t1.Shutdown()
	t2.Shutdown()

	ranked := transport.Rank([]transport.TransportService{t1, t2})
	if len(ranked) != 0 {
		t.Errorf("all unavailable: got %d ranked, want 0", len(ranked))
	}
}

func TestRank_ScoresAllPositive(t *testing.T) {
	t1 := transport.NewInProcessTransport()
	t2 := transport.NewInProcessTransport()

	ranked := transport.Rank([]transport.TransportService{t1, t2})
	for _, r := range ranked {
		if r.Score <= 0 {
			t.Errorf("score should be positive, got %f", r.Score)
		}
	}
}
