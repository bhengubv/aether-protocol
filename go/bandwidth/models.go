// SPDX-License-Identifier: MIT

// Package bandwidth implements the AetherNet Bandwidth Measurement Framework (ABMF, W18-5).
//
// It provides three concentric layers:
//   - [BandwidthEstimator] — per-transport BBRv3-inspired estimator.
//   - [BandwidthDirector]  — cross-transport synthesis and mesh gossip coordinator.
//   - [NodeActivityMonitor] — UI-facing sampler that produces [NodeActivitySnapshot].
package bandwidth

import (
	"fmt"
	"math"
	"time"
)

// ── Confidence ──────────────────────────────────────────────────────────────

// BandwidthConfidence indicates how reliable the current bandwidth estimate is.
// The tier rises with each probe round and resets on topology change or extended idle.
type BandwidthConfidence int

const (
	// ConfidenceNone means no measurement has been taken yet.
	ConfidenceNone BandwidthConfidence = iota
	// ConfidenceLow means fewer than 5 probe rounds have completed.
	ConfidenceLow
	// ConfidenceMedium means 5–19 probe rounds have completed.
	ConfidenceMedium
	// ConfidenceHigh means 20 or more probe rounds have completed.
	ConfidenceHigh
)

func (c BandwidthConfidence) String() string {
	switch c {
	case ConfidenceNone:
		return "None"
	case ConfidenceLow:
		return "Low"
	case ConfidenceMedium:
		return "Medium"
	case ConfidenceHigh:
		return "High"
	default:
		return "Unknown"
	}
}

// ── BandwidthSample ─────────────────────────────────────────────────────────

// BandwidthSample is a point-in-time bandwidth measurement for a single transport link.
//
// Derivation follows BBRv3 (draft-cardwell-iccrg-bbr-congestion-control-02):
//   - BtlBwBps — max delivery rate over 10×RTprop window.
//   - RtProp   — minimum RTT observed in last 10 s (ProbeRTT window).
//   - Srtt     — RFC 6298 smoothed RTT (α = 1/8).
//   - RttVar   — RFC 6298 mean deviation (β = 1/4).
//
// BdpBytes is pre-computed so callers never have to re-derive it.
// PhyCapBps is a PHY-layer cap from RSSI mapping; 0 if unknown.
// Confidence distinguishes a 1-probe estimate from a stable 30-round estimate.
type BandwidthSample struct {
	// TransportName identifies the link (e.g. "BLE", "Wi-Fi Direct").
	TransportName string

	// BtlBwBps is the BBRv3 bottleneck bandwidth: maximum sustained delivery rate (bps).
	BtlBwBps int64

	// AvailableBps is BtlBwBps × (1 − LossRate).
	AvailableBps int64

	// BdpBytes is the Bandwidth-Delay Product: BtlBwBps × RtProp / 8 (bytes).
	BdpBytes int64

	// Srtt is the RFC 6298 smoothed RTT.
	Srtt time.Duration

	// RttVar is the RFC 6298 RTT mean deviation (RTTVAR).
	RttVar time.Duration

	// RtProp is the BBRv3 RTprop: minimum observed RTT over the last 10 seconds.
	RtProp time.Duration

	// LossRate is the EWMA fractional packet loss rate in [0, 1]; α = 0.10.
	LossRate float64

	// PhyCapBps is the PHY-layer bandwidth cap from RSSI hints (bps). 0 = unknown.
	PhyCapBps int64

	// Confidence is the quality tier of this estimate.
	Confidence BandwidthConfidence

	// MeasuredAt is the UTC time this snapshot was built.
	MeasuredAt time.Time
}

// Rto returns the RFC 6298 §2.4 retransmission timeout:
//
//	RTO = SRTT + max(G, 4×RTTVAR),  G = 1 ms clock granularity.
//
// Clamped to [200 ms, 60 s].
func (s BandwidthSample) Rto() time.Duration {
	g := time.Millisecond
	rttVar4 := time.Duration(4 * s.RttVar.Milliseconds() * int64(time.Millisecond))
	clock := g
	if rttVar4 > clock {
		clock = rttVar4
	}
	raw := s.Srtt + clock
	const minRto = 200 * time.Millisecond
	const maxRto = 60 * time.Second
	if raw < minRto {
		return minRto
	}
	if raw > maxRto {
		return maxRto
	}
	return raw
}

// EffectiveBps returns the minimum of BtlBwBps and PhyCapBps (when PhyCapBps > 0).
func (s BandwidthSample) EffectiveBps() int64 {
	if s.PhyCapBps > 0 && s.PhyCapBps < s.BtlBwBps {
		return s.PhyCapBps
	}
	return s.BtlBwBps
}

// ── Probe wire models ────────────────────────────────────────────────────────

// BandwidthProbe is a latency/throughput probe request (PacketType.BandwidthProbe
// = 53 body). SenderSendUs is microseconds since Unix epoch on the sender's local
// clock; the responder echoes it back in a BandwidthProbeAck so RTT can be derived
// without clock synchronisation. Mirrors the C# AetherNet.Bandwidth.BandwidthProbe.
type BandwidthProbe struct {
	Sequence     uint32
	SenderSendUs int64
}

// BandwidthProbeAck carries four timestamps from a two-way probe (RFC 5136 §3).
// All timestamps are microseconds since Unix epoch on each peer's local clock.
// Clock synchronisation is NOT required — RTT uses sender-side timestamps only.
type BandwidthProbeAck struct {
	Sequence          uint32
	SenderSendUs      int64
	ReceiverReceiveUs int64
	ReceiverSendUs    int64
	SenderReceiveUs   int64
	ProbeBytes        int
}

// Rtt returns the clock-sync-free round-trip time:
//
//	RTT = (SenderReceive − SenderSend) − receiver processing time.
func (a BandwidthProbeAck) Rtt() time.Duration {
	rttUs := (a.SenderReceiveUs - a.SenderSendUs) - (a.ReceiverSendUs - a.ReceiverReceiveUs)
	return time.Duration(rttUs) * time.Microsecond
}

// ForwardOwd returns the forward one-way delay (sender → receiver).
// Requires loose clock sync; treat as approximate unless NTP/PTP is available.
func (a BandwidthProbeAck) ForwardOwd() time.Duration {
	return time.Duration(a.ReceiverReceiveUs-a.SenderSendUs) * time.Microsecond
}

// ── Gossip warm-start ────────────────────────────────────────────────────────

// BandwidthGossipPayload is the gossip message broadcast to new peers during handshake.
// It allows a new session to start with a warm BtlBw estimate instead of probing from zero.
// Gossip warm-start is unique to AetherNet; QUIC and TCP always cold-start.
type BandwidthGossipPayload struct {
	PeerUhid      string
	TransportName string
	BtlBwBps      int64
	RtPropUs      int64
	Confidence    BandwidthConfidence
	MeasuredAt    time.Time
}

// ── Node activity ────────────────────────────────────────────────────────────

// NodeActivityState is the high-level activity state of a node.
// Suitable for status-bar indicators, dashboard health badges, and connection-quality icons.
type NodeActivityState int

const (
	// NodeOffline means no transports are available; node is isolated.
	NodeOffline NodeActivityState = iota
	// NodeIdle means transports are available but no data has flowed in the last 5 s.
	NodeIdle
	// NodeActive means data is flowing; link utilization < 50 % of estimated capacity.
	NodeActive
	// NodeBusy means link utilization ≥ 50 %; performance good but approaching limits.
	NodeBusy
	// NodeDegraded means loss rate > 5 % or delivery rate is declining.
	NodeDegraded
)

func (s NodeActivityState) String() string {
	switch s {
	case NodeOffline:
		return "Offline"
	case NodeIdle:
		return "Idle"
	case NodeActive:
		return "Active"
	case NodeBusy:
		return "Busy"
	case NodeDegraded:
		return "Degraded"
	default:
		return "Unknown"
	}
}

// TransportActivitySnapshot is the activity snapshot for a single transport within the node.
type TransportActivitySnapshot struct {
	TransportName string
	IsAvailable   bool

	// IngressBps is bytes per second being received on this transport.
	IngressBps int64

	// EgressBps is bytes per second being sent on this transport.
	EgressBps int64

	// Srtt is the smoothed RTT from the BandwidthEstimator.
	Srtt time.Duration

	// BtlBwBps is the bottleneck bandwidth from the BandwidthEstimator.
	BtlBwBps int64

	// UtilizationFraction is egress utilization: EgressBps / BtlBwBps. 0 if BtlBwBps = 0.
	UtilizationFraction float64

	State      NodeActivityState
	Confidence BandwidthConfidence
}

// UtilizationPercent returns a human-readable utilization string (e.g. "34 %").
func (t TransportActivitySnapshot) UtilizationPercent() string {
	return fmt.Sprintf("%.0f %%", t.UtilizationFraction*100.0)
}

// NodeActivitySnapshot is the full node activity snapshot surfaced to the UI layer.
//
// Consumption patterns:
//   - Status bar / widget: poll [NodeActivityMonitor.Current] every 1 s.
//   - Dashboard / SignalR: subscribe via [NodeActivityMonitor.Subscribe].
//   - ABR controller: watch for [NodeDegraded] and step down the bitrate ladder.
type NodeActivitySnapshot struct {
	State NodeActivityState

	// IngressBps is aggregate bytes per second flowing INTO this node (all transports).
	IngressBps int64

	// EgressBps is aggregate bytes per second flowing OUT of this node (all transports).
	EgressBps int64

	// ActivePeers is the number of remote peers that had traffic in the last idle-threshold.
	ActivePeers int

	// ActiveTransports is the number of transports currently carrying data.
	ActiveTransports int

	// Transports is the per-transport breakdown.
	Transports []TransportActivitySnapshot

	// PrimaryTransportName is the dominant transport (most egress bytes). Empty if offline/idle.
	PrimaryTransportName string

	Timestamp time.Time
}

// TotalBps returns the combined throughput (ingress + egress).
func (s NodeActivitySnapshot) TotalBps() int64 {
	return s.IngressBps + s.EgressBps
}

// HasActivity returns true if any transport has data flowing.
func (s NodeActivitySnapshot) HasActivity() bool {
	return s.State == NodeActive || s.State == NodeBusy || s.State == NodeDegraded
}

// ── Internal helpers ─────────────────────────────────────────────────────────

// clampFloat64 returns v clamped to [lo, hi].
func clampFloat64(v, lo, hi float64) float64 {
	return math.Min(hi, math.Max(lo, v))
}
