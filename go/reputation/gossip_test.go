// SPDX-License-Identifier: MIT

package reputation

import (
	"context"
	"encoding/json"
	"math"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/thegeeknetwork/aether-protocol-go/protocol"
)

// ─────────────────────────────────────────────────────────────────────────────
// Fakes
// ─────────────────────────────────────────────────────────────────────────────

type fakeSender struct {
	localUhid string
	sent      []*protocol.MeshPacket
}

func (f *fakeSender) LocalUhid() string { return f.localUhid }
func (f *fakeSender) BroadcastAsync(pkt *protocol.MeshPacket) (int, error) {
	f.sent = append(f.sent, pkt)
	return 1, nil
}

// fakeSigner signs by setting Signature to a well-known sentinel and verifies
// by checking that sentinel.  verifyOk can be overridden per test.
type fakeSigner struct {
	verifyOk bool
}

var fakeSig = []byte("fake-sig")

func (f *fakeSigner) SignPacket(pkt *protocol.MeshPacket) (*protocol.MeshPacket, error) {
	copy := *pkt
	copy.Signature = fakeSig
	return &copy, nil
}

func (f *fakeSigner) VerifyPacket(pkt *protocol.MeshPacket, _ []byte) (bool, error) {
	return f.verifyOk, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

func newGossipEnv(localUhid string) (*ReputationGossipService, *fakeSender, *fakeSigner, *NodeReputationService) {
	sender := &fakeSender{localUhid: localUhid}
	signer := &fakeSigner{verifyOk: true}
	rep := NewNodeReputationService()
	svc := NewReputationGossipService(sender, signer, rep)
	return svc, sender, signer, rep
}

// makeGossipPacket builds a signed-looking gossip packet with the given payload.
func makeGossipPacket(payload ReputationUpdatePayload) *protocol.MeshPacket {
	b, _ := json.Marshal(payload)
	return &protocol.MeshPacket{
		ID:              uuid.New(),
		Type:            protocol.PacketTypeReputationUpdate,
		SourceUhid:      payload.ReporterUhid,
		DestinationUhid: "*",
		Ttl:             3,
		Payload:         b,
		TimestampMs:     payload.TimestampMs,
		Signature:       fakeSig,
	}
}

func nowMs() int64 { return time.Now().UnixMilli() }

// approxEq returns true when |a-b| < 1e-9.
func approxEq(a, b float64) bool { return math.Abs(a-b) < 1e-9 }

// ─────────────────────────────────────────────────────────────────────────────
// Tests
// ─────────────────────────────────────────────────────────────────────────────

// 1. BroadcastSendsOnePacket — exactly one packet is broadcast, correct type.
func TestGossip_BroadcastSendsOnePacket(t *testing.T) {
	svc, sender, _, _ := newGossipEnv("local")

	if err := svc.BroadcastReputationUpdate(context.Background(), "target", -0.1, "test"); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	if len(sender.sent) != 1 {
		t.Fatalf("expected 1 packet sent, got %d", len(sender.sent))
	}
	if sender.sent[0].Type != protocol.PacketTypeReputationUpdate {
		t.Fatalf("expected PacketTypeReputationUpdate, got %v", sender.sent[0].Type)
	}
}

// 2. BroadcastPayloadFields — reporter/target/delta/reason are all correct.
func TestGossip_BroadcastPayloadFields(t *testing.T) {
	svc, sender, _, _ := newGossipEnv("local-node")

	if err := svc.BroadcastReputationUpdate(context.Background(), "remote-node", -0.3, "bad behaviour"); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	var p ReputationUpdatePayload
	if err := json.Unmarshal(sender.sent[0].Payload, &p); err != nil {
		t.Fatalf("unmarshal error: %v", err)
	}

	if p.ReporterUhid != "local-node" {
		t.Errorf("ReporterUhid: want local-node, got %q", p.ReporterUhid)
	}
	if p.TargetUhid != "remote-node" {
		t.Errorf("TargetUhid: want remote-node, got %q", p.TargetUhid)
	}
	if !approxEq(p.ScoreDelta, -0.3) {
		t.Errorf("ScoreDelta: want -0.3, got %v", p.ScoreDelta)
	}
	if p.Reason != "bad behaviour" {
		t.Errorf("Reason: want %q, got %q", "bad behaviour", p.Reason)
	}
}

// 3. BroadcastClampsDeltaAbove1 — delta > 1 is clamped to 1.0.
func TestGossip_BroadcastClampsDeltaAbove1(t *testing.T) {
	svc, sender, _, _ := newGossipEnv("local")

	if err := svc.BroadcastReputationUpdate(context.Background(), "target", 5.0, ""); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	var p ReputationUpdatePayload
	json.Unmarshal(sender.sent[0].Payload, &p)

	if !approxEq(p.ScoreDelta, 1.0) {
		t.Errorf("expected clamped delta 1.0, got %v", p.ScoreDelta)
	}
}

// 4. BroadcastClampsDeltaBelow1 — delta < -1 is clamped to -1.0.
func TestGossip_BroadcastClampsDeltaBelow1(t *testing.T) {
	svc, sender, _, _ := newGossipEnv("local")

	if err := svc.BroadcastReputationUpdate(context.Background(), "target", -9.9, ""); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	var p ReputationUpdatePayload
	json.Unmarshal(sender.sent[0].Payload, &p)

	if !approxEq(p.ScoreDelta, -1.0) {
		t.Errorf("expected clamped delta -1.0, got %v", p.ScoreDelta)
	}
}

// 5. HandleInvalidSignature — verify fails → false.
func TestGossip_HandleInvalidSignature(t *testing.T) {
	svc, _, signer, _ := newGossipEnv("local")
	signer.verifyOk = false

	payload := ReputationUpdatePayload{
		ReporterUhid: "reporter",
		TargetUhid:   "target",
		ScoreDelta:   -0.1,
		TimestampMs:  nowMs(),
	}
	pkt := makeGossipPacket(payload)

	accepted, err := svc.HandleGossipPacket(context.Background(), pkt, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if accepted {
		t.Fatal("expected false when signature invalid, got true")
	}
}

// 6. HandleWrongType — non-ReputationUpdate packet type → false.
func TestGossip_HandleWrongType(t *testing.T) {
	svc, _, _, _ := newGossipEnv("local")

	pkt := &protocol.MeshPacket{
		ID:          uuid.New(),
		Type:        protocol.Data,
		TimestampMs: nowMs(),
		Payload:     []byte(`{}`),
	}

	accepted, err := svc.HandleGossipPacket(context.Background(), pkt, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if accepted {
		t.Fatal("expected false for wrong packet type, got true")
	}
}

// 7. HandleStaleTimestamp — payload older than 5 minutes → false.
func TestGossip_HandleStaleTimestamp(t *testing.T) {
	svc, _, _, _ := newGossipEnv("local")

	sixMinAgo := time.Now().Add(-6 * time.Minute).UnixMilli()
	payload := ReputationUpdatePayload{
		ReporterUhid: "reporter",
		TargetUhid:   "target",
		ScoreDelta:   -0.1,
		TimestampMs:  sixMinAgo,
	}
	pkt := makeGossipPacket(payload)

	accepted, err := svc.HandleGossipPacket(context.Background(), pkt, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if accepted {
		t.Fatal("expected false for stale timestamp, got true")
	}
}

// 8. HandleMissingFields — empty reporter → false.
func TestGossip_HandleMissingFields(t *testing.T) {
	svc, _, _, _ := newGossipEnv("local")

	payload := ReputationUpdatePayload{
		ReporterUhid: "", // missing
		TargetUhid:   "target",
		ScoreDelta:   -0.1,
		TimestampMs:  nowMs(),
	}
	pkt := makeGossipPacket(payload)

	accepted, err := svc.HandleGossipPacket(context.Background(), pkt, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if accepted {
		t.Fatal("expected false for missing reporter, got true")
	}
}

// 9. HandleOwnGossip — reporter == local UHID → false.
func TestGossip_HandleOwnGossip(t *testing.T) {
	svc, _, _, _ := newGossipEnv("local")

	payload := ReputationUpdatePayload{
		ReporterUhid: "local", // same as local node
		TargetUhid:   "target",
		ScoreDelta:   -0.1,
		TimestampMs:  nowMs(),
	}
	pkt := makeGossipPacket(payload)

	accepted, err := svc.HandleGossipPacket(context.Background(), pkt, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if accepted {
		t.Fatal("expected false for own-echo, got true")
	}
}

// 10. HandleUnknownReporter_FullDelta — unknown reporter has R=1.0, so
//
//	effectiveDelta == scoreDelta.
func TestGossip_HandleUnknownReporter_FullDelta(t *testing.T) {
	svc, _, _, rep := newGossipEnv("local")

	delta := -0.4
	payload := ReputationUpdatePayload{
		ReporterUhid: "unknown-reporter",
		TargetUhid:   "target",
		ScoreDelta:   delta,
		TimestampMs:  nowMs(),
	}
	pkt := makeGossipPacket(payload)

	accepted, err := svc.HandleGossipPacket(context.Background(), pkt, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !accepted {
		t.Fatal("expected true, got false")
	}

	want := 1.0 + delta // 0.6 — reporter R=1.0, effective == delta
	got := rep.GetReputationScore("target")
	if !approxEq(got, want) {
		t.Errorf("expected %.4f, got %.4f", want, got)
	}
}

// 11. HandleDegradedReporter_WeightedDelta — reporter R=0.5, effective = 0.5 × delta.
func TestGossip_HandleDegradedReporter_WeightedDelta(t *testing.T) {
	svc, _, _, rep := newGossipEnv("local")

	// Give the reporter a degraded reputation of 0.5.
	rep.RecordSignatureFailure("degraded-reporter") // 1.0 - 0.20 = 0.80
	rep.RecordSignatureFailure("degraded-reporter") // 0.80 - 0.20 = 0.60
	rep.RecordSignatureFailure("degraded-reporter") // 0.60 - 0.20 = 0.40? No...
	// Actually let's use ApplyWeightedDelta to set it directly to 0.5:
	// Reset via direct set by applying from 1.0: delta = -0.5
	repDirect := NewNodeReputationService()
	repDirect.ApplyWeightedDelta("degraded-reporter", -0.5)
	svc2 := NewReputationGossipService(&fakeSender{localUhid: "local"}, &fakeSigner{verifyOk: true}, repDirect)

	delta := -0.6
	payload := ReputationUpdatePayload{
		ReporterUhid: "degraded-reporter",
		TargetUhid:   "target",
		ScoreDelta:   delta,
		TimestampMs:  nowMs(),
	}
	pkt := makeGossipPacket(payload)

	accepted, err := svc2.HandleGossipPacket(context.Background(), pkt, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !accepted {
		t.Fatal("expected true, got false")
	}

	// reporter R = 0.5, effective = 0.5 × (-0.6) = -0.3
	// target starts at 1.0 → 1.0 + (-0.3) = 0.7
	reporterR := repDirect.GetReputationScore("degraded-reporter")
	want := 1.0 + (delta * reporterR) // 1.0 + (-0.6 * 0.5) = 0.7
	got := repDirect.GetReputationScore("target")
	if !approxEq(got, want) {
		t.Errorf("expected %.4f (R=%.4f), got %.4f", want, reporterR, got)
	}

	// suppress "svc not used" lint
	_ = svc
}

// 12. HandlePositiveDelta_ImprovesTarget — positive delta improves score.
func TestGossip_HandlePositiveDelta_ImprovesTarget(t *testing.T) {
	svc, _, _, rep := newGossipEnv("local")

	// Degrade target first so there is room to improve.
	rep.RecordSignatureFailure("target") // 0.80
	rep.RecordSignatureFailure("target") // 0.60

	before := rep.GetReputationScore("target")

	payload := ReputationUpdatePayload{
		ReporterUhid: "reporter",
		TargetUhid:   "target",
		ScoreDelta:   0.2,
		TimestampMs:  nowMs(),
	}
	pkt := makeGossipPacket(payload)

	accepted, err := svc.HandleGossipPacket(context.Background(), pkt, nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !accepted {
		t.Fatal("expected true, got false")
	}

	after := rep.GetReputationScore("target")
	if after <= before {
		t.Errorf("expected target score to improve: before=%.4f after=%.4f", before, after)
	}
}
