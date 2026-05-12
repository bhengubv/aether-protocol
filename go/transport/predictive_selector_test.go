// SPDX-License-Identifier: MIT
// Unit tests for PredictiveTransportSelector — Kalman RTT filter and scoring.

package transport

import (
	"context"
	"math"
	"testing"
)

// ── fakeTransport — minimal TransportService stub ─────────────────────────────

type fakeTransport struct {
	name          string
	available     bool
	bandwidthBps  int64
	powerCost     int32
	metrics       *PerTransportMetrics
}

func newFakeTransport(name string, bps int64, power int32, available bool) *fakeTransport {
	return &fakeTransport{
		name:         name,
		available:    available,
		bandwidthBps: bps,
		powerCost:    power,
		metrics:      NewPerTransportMetrics(),
	}
}

func (f *fakeTransport) Name() string                     { return f.name }
func (f *fakeTransport) IsAvailable() bool                { return f.available }
func (f *fakeTransport) MaxBandwidthBps() int64           { return f.bandwidthBps }
func (f *fakeTransport) MaxRangeMeters() int32            { return 100 }
func (f *fakeTransport) PowerCostRelative() int32         { return f.powerCost }
func (f *fakeTransport) MaxConcurrentPeers() int32        { return 10 }
func (f *fakeTransport) Metrics() *PerTransportMetrics    { return f.metrics }
func (f *fakeTransport) IsConnected(_ string) bool        { return false }

func (f *fakeTransport) SendAsync(_ context.Context, _ string, _ []byte) (bool, error) {
	return true, nil
}

func (f *fakeTransport) SendStreamAsync(_ context.Context, _ string, _ []byte) (bool, error) {
	return true, nil
}

// ── Kalman filter unit tests ──────────────────────────────────────────────────

func TestKalmanFilterConvergesOnSteadyState(t *testing.T) {
	f := newKalmanRttFilter(200.0)
	for i := 0; i < 50; i++ {
		f.update(100.0)
	}
	if math.Abs(f.rtt-100.0) > 5.0 {
		t.Fatalf("Kalman did not converge: rtt = %.2f, want ~100", f.rtt)
	}
}

func TestKalmanFilterVarianceDecreases(t *testing.T) {
	f := newKalmanRttFilter(200.0)
	initial := f.p00
	for i := 0; i < 10; i++ {
		f.update(200.0)
	}
	if f.p00 >= initial {
		t.Fatalf("posterior variance %.4f should be < initial %.4f", f.p00, initial)
	}
}

func TestKalmanFilterDetectsDrift(t *testing.T) {
	f := newKalmanRttFilter(100.0)
	// Feed rising RTT: 100, 110, 120, …, 200 ms.
	for i := 0; i < 10; i++ {
		f.update(100.0 + float64(i+1)*10.0)
	}
	if f.drift <= 0 {
		t.Fatalf("drift %.4f should be positive with rising RTT", f.drift)
	}
}

// ── PredictiveTransportSelector lifecycle ────────────────────────────────────

func TestPredictiveSelectorRegisterAndRank(t *testing.T) {
	sel := NewPredictiveTransportSelector()
	ta := newFakeTransport("fast", 1_000_000, 1, true)
	tb := newFakeTransport("slow", 10_000, 10, true)

	sel.Register(ta, 50.0)
	sel.Register(tb, 150.0)

	// Feed a few good samples to ta so it has a real score.
	for i := 0; i < 5; i++ {
		sel.ObserveMetrics(ta, 50, true, 1000)
	}

	ranked := sel.Rank(100)
	if len(ranked) != 2 {
		t.Fatalf("expected 2 ranked transports, got %d", len(ranked))
	}
	// "fast" should rank first (lower power, higher bandwidth).
	if ranked[0].Transport.Name() != "fast" {
		t.Fatalf("expected 'fast' first, got %q", ranked[0].Transport.Name())
	}
}

func TestPredictiveSelectorUnavailableTransportExcluded(t *testing.T) {
	sel := NewPredictiveTransportSelector()
	ta := newFakeTransport("available",   500_000, 1, true)
	tb := newFakeTransport("unavailable", 500_000, 1, false)

	sel.Register(ta, 100.0)
	sel.Register(tb, 100.0)

	ranked := sel.Rank(64)
	if len(ranked) != 1 {
		t.Fatalf("expected 1 ranked transport, got %d", len(ranked))
	}
	if ranked[0].Transport.Name() != "available" {
		t.Fatalf("got %q, want 'available'", ranked[0].Transport.Name())
	}
}

func TestPredictiveSelectorUnregister(t *testing.T) {
	sel := NewPredictiveTransportSelector()
	ta := newFakeTransport("a", 100_000, 1, true)
	sel.Register(ta, 100.0)
	sel.Unregister(ta)
	ranked := sel.Rank(64)
	if len(ranked) != 0 {
		t.Fatalf("expected 0 ranked transports after unregister, got %d", len(ranked))
	}
}

func TestPredictiveSelectorSelectBestReturnsNilWhenEmpty(t *testing.T) {
	sel := NewPredictiveTransportSelector()
	if sel.SelectBest(64) != nil {
		t.Fatal("SelectBest on empty selector should return nil")
	}
}

func TestPredictiveSelectorDuplicateRegisterIgnored(t *testing.T) {
	sel := NewPredictiveTransportSelector()
	ta := newFakeTransport("t", 100_000, 1, true)
	sel.Register(ta, 100.0)
	sel.Register(ta, 200.0) // duplicate — should be ignored
	ranked := sel.Rank(64)
	if len(ranked) != 1 {
		t.Fatalf("duplicate register should not double-add: got %d entries", len(ranked))
	}
}

func TestPredictiveSelectorKalmanState(t *testing.T) {
	sel := NewPredictiveTransportSelector()
	ta := newFakeTransport("t", 100_000, 1, true)
	sel.Register(ta, 123.0)
	rtt, drift, variance, ok := sel.KalmanState(ta)
	if !ok {
		t.Fatal("KalmanState returned ok=false for registered transport")
	}
	if rtt != 123.0 {
		t.Fatalf("initial KalmanState rtt %.2f want 123.0", rtt)
	}
	if drift != 0.0 {
		t.Fatalf("initial drift %.4f want 0.0", drift)
	}
	if variance <= 0 {
		t.Fatalf("initial variance %.4f should be > 0", variance)
	}
}

func TestPredictiveSelectorObserveUpdatesDrift(t *testing.T) {
	sel := NewPredictiveTransportSelector()
	ta := newFakeTransport("t", 100_000, 1, true)
	sel.Register(ta, 100.0)

	// Submit rising RTTs — Kalman drift should go positive.
	for i := 0; i < 10; i++ {
		sel.ObserveMetrics(ta, int64(100+i*15), true, 1000)
	}

	_, drift, _, _ := sel.KalmanState(ta)
	if drift <= 0 {
		t.Fatalf("drift %.4f should be positive after rising RTT observations", drift)
	}
}
