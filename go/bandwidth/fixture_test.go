// SPDX-License-Identifier: MIT

package bandwidth

import (
	"encoding/json"
	"math"
	"os"
	"path/filepath"
	"runtime"
	"testing"
	"time"
)

// fixture_test.go drives the Go ABMF SDK through the shared cross-language
// conformance corpus at tests/cross-language/bandwidth-fixtures.json. Every
// AetherNet SDK drives the SAME corpus and MUST produce identical results;
// this is the oracle that proves numeric parity across all language ports.
//
// It mirrors the C# reference driver
// (tests/AetherNet.Core.Tests/Bandwidth/BandwidthFixtureTests.cs) op-for-op and
// assertion-for-assertion. Integer/string/enum fields are asserted EXACTLY;
// floating-point fields (srttMs, rttVarMs, rtPropMs, lossRate) within the
// corpus toleranceAbs. The RTO comparison uses a fixed ±0.1 ms tolerance to
// match the C# precision:1 assertion.

// ── Corpus model ─────────────────────────────────────────────────────────────

type fixtureCorpus struct {
	ToleranceAbs float64           `json:"toleranceAbs"`
	ProbeAck     []probeAckFixture `json:"probeAck"`
	Rto          []rtoFixture      `json:"rto"`
	PhyCap       []phyCapFixture   `json:"phyCap"`
	Estimator    []estimatorFixture `json:"estimator"`
	Director     []directorFixture `json:"director"`
}

type probeAckFixture struct {
	Name               string `json:"name"`
	SenderSendUs       int64  `json:"senderSendUs"`
	ReceiverReceiveUs  int64  `json:"receiverReceiveUs"`
	ReceiverSendUs     int64  `json:"receiverSendUs"`
	SenderReceiveUs    int64  `json:"senderReceiveUs"`
	ProbeBytes         int    `json:"probeBytes"`
	ExpectRttUs        int64  `json:"expectRttUs"`
	ExpectForwardOwdUs int64  `json:"expectForwardOwdUs"`
}

type rtoFixture struct {
	Name        string  `json:"name"`
	SrttMs      float64 `json:"srttMs"`
	RttVarMs    float64 `json:"rttVarMs"`
	ExpectRtoMs float64 `json:"expectRtoMs"`
}

type phyCapFixture struct {
	Name         string `json:"name"`
	RssiDbm      int    `json:"rssiDbm"`
	ExpectCapBps int64  `json:"expectCapBps"`
}

type estimatorOp struct {
	Op string `json:"op"`

	// delivery
	Bytes     int   `json:"bytes"`
	SendUs    int64 `json:"sendUs"`
	DeliverUs int64 `json:"deliverUs"`

	// phyHint
	RssiDbm int `json:"rssiDbm"`

	// gossip
	BtlBwBps   int64   `json:"btlBwBps"`
	RtPropMs   float64 `json:"rtPropMs"`
	Confidence string  `json:"confidence"`
}

// estimatorExpect uses pointers so absent fields are distinguishable from
// zero values — mirroring the C# TryGetProperty guards.
type estimatorExpect struct {
	BtlBwBps     *int64   `json:"btlBwBps"`
	EffectiveBps *int64   `json:"effectiveBps"`
	AvailableBps *int64   `json:"availableBps"`
	BdpBytes     *int64   `json:"bdpBytes"`
	PhyCapBps    *int64   `json:"phyCapBps"`
	Confidence   *string  `json:"confidence"`
	SrttMs       *float64 `json:"srttMs"`
	RttVarMs     *float64 `json:"rttVarMs"`
	RtPropMs     *float64 `json:"rtPropMs"`
	LossRate     *float64 `json:"lossRate"`
}

type estimatorFixture struct {
	Name      string          `json:"name"`
	Transport string          `json:"transport"`
	MaxBps    int64           `json:"maxBps"`
	Ops       []estimatorOp   `json:"ops"`
	Expect    estimatorExpect `json:"expect"`
}

type directorGossip struct {
	PeerUhid   string `json:"peerUhid"`
	Transport  string `json:"transport"`
	BtlBwBps   int64  `json:"btlBwBps"`
	RtPropUs   int64  `json:"rtPropUs"`
	Confidence string `json:"confidence"`
}

type directorRecommend struct {
	PeerUhid     string `json:"peerUhid"`
	PayloadBytes int64  `json:"payloadBytes"`
}

type directorFixture struct {
	Name            string            `json:"name"`
	Register        []string          `json:"register"`
	Gossips         []directorGossip  `json:"gossips"`
	Recommend       directorRecommend `json:"recommend"`
	ExpectTransport *string           `json:"expectTransport"` // nil when JSON null
}

// ── Loader ───────────────────────────────────────────────────────────────────

// loadCorpus locates and parses the cross-language corpus by walking up from the
// test source directory (via runtime.Caller) until tests/cross-language/
// bandwidth-fixtures.json is found — mirroring the C# LoadCorpus walk-up.
func loadCorpus(t *testing.T) fixtureCorpus {
	t.Helper()

	_, thisFile, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatalf("runtime.Caller failed; cannot locate corpus")
	}

	dir := filepath.Dir(thisFile)
	for {
		candidate := filepath.Join(dir, "tests", "cross-language", "bandwidth-fixtures.json")
		if _, err := os.Stat(candidate); err == nil {
			data, readErr := os.ReadFile(candidate)
			if readErr != nil {
				t.Fatalf("reading corpus %s: %v", candidate, readErr)
			}
			var corpus fixtureCorpus
			if jsonErr := json.Unmarshal(data, &corpus); jsonErr != nil {
				t.Fatalf("parsing corpus %s: %v", candidate, jsonErr)
			}
			return corpus
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			break // reached filesystem root
		}
		dir = parent
	}

	t.Fatalf("bandwidth-fixtures.json not found walking up from %s", filepath.Dir(thisFile))
	return fixtureCorpus{}
}

// parseConfidence maps a corpus confidence string to the Go enum.
func parseConfidence(t *testing.T, s string) BandwidthConfidence {
	t.Helper()
	switch s {
	case "None":
		return ConfidenceNone
	case "Low":
		return ConfidenceLow
	case "Medium":
		return ConfidenceMedium
	case "High":
		return ConfidenceHigh
	default:
		t.Fatalf("bad confidence %q", s)
		return ConfidenceNone
	}
}

// ── probeAck ─────────────────────────────────────────────────────────────────

func TestFixture_ProbeAck_RttAndOwd_Exact(t *testing.T) {
	corpus := loadCorpus(t)
	if len(corpus.ProbeAck) == 0 {
		t.Fatal("no probeAck fixtures loaded")
	}

	for _, f := range corpus.ProbeAck {
		f := f
		t.Run(f.Name, func(t *testing.T) {
			ack := BandwidthProbeAck{
				Sequence:          1,
				SenderSendUs:      f.SenderSendUs,
				ReceiverReceiveUs: f.ReceiverReceiveUs,
				ReceiverSendUs:    f.ReceiverSendUs,
				SenderReceiveUs:   f.SenderReceiveUs,
				ProbeBytes:        f.ProbeBytes,
			}

			if got := ack.Rtt().Microseconds(); got != f.ExpectRttUs {
				t.Errorf("Rtt = %d us, want %d us", got, f.ExpectRttUs)
			}
			if got := ack.ForwardOwd().Microseconds(); got != f.ExpectForwardOwdUs {
				t.Errorf("ForwardOwd = %d us, want %d us", got, f.ExpectForwardOwdUs)
			}
		})
	}
}

// ── rto ──────────────────────────────────────────────────────────────────────

func TestFixture_Rto_Clamped_MatchesRfc6298(t *testing.T) {
	corpus := loadCorpus(t)
	if len(corpus.Rto) == 0 {
		t.Fatal("no rto fixtures loaded")
	}

	const rtoTol = 0.1 // ±0.1 ms, matching the C# precision:1 assertion

	for _, f := range corpus.Rto {
		f := f
		t.Run(f.Name, func(t *testing.T) {
			sample := BandwidthSample{
				TransportName: "T",
				BtlBwBps:      1_000_000,
				AvailableBps:  900_000,
				BdpBytes:      1000,
				Srtt:          time.Duration(f.SrttMs * float64(time.Millisecond)),
				RttVar:        time.Duration(f.RttVarMs * float64(time.Millisecond)),
				RtProp:        10 * time.Millisecond,
				LossRate:      0.0,
				PhyCapBps:     0,
				Confidence:    ConfidenceHigh,
				MeasuredAt:    time.Now().UTC(),
			}

			gotMs := float64(sample.Rto()) / float64(time.Millisecond)
			if math.Abs(gotMs-f.ExpectRtoMs) > rtoTol {
				t.Errorf("Rto = %.4f ms, want %.4f ms (tol %.1f)", gotMs, f.ExpectRtoMs, rtoTol)
			}
		})
	}
}

// ── phyCap ───────────────────────────────────────────────────────────────────

func TestFixture_PhyCap_FromRssi_Exact(t *testing.T) {
	corpus := loadCorpus(t)
	if len(corpus.PhyCap) == 0 {
		t.Fatal("no phyCap fixtures loaded")
	}

	for _, f := range corpus.PhyCap {
		f := f
		t.Run(f.Name, func(t *testing.T) {
			e := NewBandwidthEstimator("T", 10_000_000_000)
			e.ApplyPhyHint(f.RssiDbm)
			if got := e.CurrentSample().PhyCapBps; got != f.ExpectCapBps {
				t.Errorf("PhyCapBps = %d, want %d", got, f.ExpectCapBps)
			}
		})
	}
}

// ── estimator ────────────────────────────────────────────────────────────────

func TestFixture_Estimator_DrivesToExpectedSample(t *testing.T) {
	corpus := loadCorpus(t)
	if len(corpus.Estimator) == 0 {
		t.Fatal("no estimator fixtures loaded")
	}
	tol := corpus.ToleranceAbs

	for _, f := range corpus.Estimator {
		f := f
		t.Run(f.Name, func(t *testing.T) {
			e := NewBandwidthEstimator(f.Transport, f.MaxBps)

			for _, op := range f.Ops {
				switch op.Op {
				case "delivery":
					e.RecordDelivery(op.Bytes, op.SendUs, op.DeliverUs)
				case "loss":
					e.RecordLoss(op.Bytes)
				case "phyHint":
					e.ApplyPhyHint(op.RssiDbm)
				case "gossip":
					e.WarmFromGossip(
						op.BtlBwBps,
						time.Duration(op.RtPropMs*float64(time.Millisecond)),
						parseConfidence(t, op.Confidence),
					)
				default:
					t.Fatalf("unknown op %q", op.Op)
				}
			}

			s := e.CurrentSample()
			exp := f.Expect

			// Integer / enum fields — exact.
			if exp.BtlBwBps != nil && s.BtlBwBps != *exp.BtlBwBps {
				t.Errorf("BtlBwBps = %d, want %d", s.BtlBwBps, *exp.BtlBwBps)
			}
			if exp.EffectiveBps != nil && s.EffectiveBps() != *exp.EffectiveBps {
				t.Errorf("EffectiveBps = %d, want %d", s.EffectiveBps(), *exp.EffectiveBps)
			}
			if exp.AvailableBps != nil && s.AvailableBps != *exp.AvailableBps {
				t.Errorf("AvailableBps = %d, want %d", s.AvailableBps, *exp.AvailableBps)
			}
			if exp.BdpBytes != nil && s.BdpBytes != *exp.BdpBytes {
				t.Errorf("BdpBytes = %d, want %d", s.BdpBytes, *exp.BdpBytes)
			}
			if exp.PhyCapBps != nil && s.PhyCapBps != *exp.PhyCapBps {
				t.Errorf("PhyCapBps = %d, want %d", s.PhyCapBps, *exp.PhyCapBps)
			}
			if exp.Confidence != nil {
				want := parseConfidence(t, *exp.Confidence)
				if s.Confidence != want {
					t.Errorf("Confidence = %v, want %v", s.Confidence, want)
				}
			}

			// Float fields — tolerance.
			if exp.SrttMs != nil {
				got := float64(s.Srtt) / float64(time.Millisecond)
				if math.Abs(got-*exp.SrttMs) > tol {
					t.Errorf("SrttMs = %.6f, want %.6f (tol %g)", got, *exp.SrttMs, tol)
				}
			}
			if exp.RttVarMs != nil {
				got := float64(s.RttVar) / float64(time.Millisecond)
				if math.Abs(got-*exp.RttVarMs) > tol {
					t.Errorf("RttVarMs = %.6f, want %.6f (tol %g)", got, *exp.RttVarMs, tol)
				}
			}
			if exp.RtPropMs != nil {
				got := float64(s.RtProp) / float64(time.Millisecond)
				if math.Abs(got-*exp.RtPropMs) > tol {
					t.Errorf("RtPropMs = %.6f, want %.6f (tol %g)", got, *exp.RtPropMs, tol)
				}
			}
			if exp.LossRate != nil {
				if math.Abs(s.LossRate-*exp.LossRate) > tol {
					t.Errorf("LossRate = %.6f, want %.6f (tol %g)", s.LossRate, *exp.LossRate, tol)
				}
			}
		})
	}
}

// ── director ─────────────────────────────────────────────────────────────────

func TestFixture_Director_RecommendsExpectedTransport(t *testing.T) {
	corpus := loadCorpus(t)
	if len(corpus.Director) == 0 {
		t.Fatal("no director fixtures loaded")
	}

	for _, f := range corpus.Director {
		f := f
		t.Run(f.Name, func(t *testing.T) {
			director := NewBandwidthDirector()

			// Register one estimator per declared transport. Generous maxBps so the
			// PHY default does not cap gossip-seeded values.
			for _, transport := range f.Register {
				director.Register(NewBandwidthEstimator(transport, 10_000_000_000))
			}

			for _, g := range f.Gossips {
				director.ApplyGossip(BandwidthGossipPayload{
					PeerUhid:      g.PeerUhid,
					TransportName: g.Transport,
					BtlBwBps:      g.BtlBwBps,
					RtPropUs:      g.RtPropUs,
					Confidence:    parseConfidence(t, g.Confidence),
					MeasuredAt:    time.Now().UTC(),
				})
			}

			result := director.RecommendTransport(f.Recommend.PeerUhid, f.Recommend.PayloadBytes)

			if f.ExpectTransport == nil {
				// JSON null → expect no recommendation (empty string in Go).
				if result != "" {
					t.Errorf("RecommendTransport = %q, want empty (null)", result)
				}
			} else if result != *f.ExpectTransport {
				t.Errorf("RecommendTransport = %q, want %q", result, *f.ExpectTransport)
			}
		})
	}
}
