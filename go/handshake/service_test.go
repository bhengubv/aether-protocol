// SPDX-License-Identifier: MIT

package handshake

import (
	"context"
	"encoding/json"
	"sync"
	"testing"

	"github.com/thegeeknetwork/aether-protocol-go/constants"
	"github.com/thegeeknetwork/aether-protocol-go/protocol"
)

const (
	uhidAlice = "uhid:alice"
	uhidBob   = "uhid:bob"
	uhidCarol = "uhid:carol"
)

// fakeSender is a test double that records every send.
type fakeSender struct {
	mu       sync.Mutex
	uhid     string
	Unicasts []sentRecord
}

type sentRecord struct {
	Packet      *protocol.MeshPacket
	NextHopUhid string
}

func newFakeSender(uhid string) *fakeSender {
	return &fakeSender{uhid: uhid}
}

func (f *fakeSender) LocalUhid() string { return f.uhid }

func (f *fakeSender) Send(_ context.Context, packet *protocol.MeshPacket, nextHopUhid string) (bool, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.Unicasts = append(f.Unicasts, sentRecord{Packet: clonePkt(packet), NextHopUhid: nextHopUhid})
	return true, nil
}

func (f *fakeSender) clear() {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.Unicasts = nil
}

func clonePkt(src *protocol.MeshPacket) *protocol.MeshPacket {
	dst := *src
	dst.Payload = append([]byte(nil), src.Payload...)
	dst.Signature = append([]byte(nil), src.Signature...)
	dst.PacketNonce = append([]byte(nil), src.PacketNonce...)
	return &dst
}

func buildHelloPacket(t *testing.T, ptype protocol.PacketType, source, dest string,
	minV, maxV byte, caps []string, impl string) *protocol.MeshPacket {
	t.Helper()
	body, err := json.Marshal(HelloPayload{
		MinVersion:     minV,
		MaxVersion:     maxV,
		Capabilities:   caps,
		Implementation: impl,
	})
	if err != nil {
		t.Fatalf("marshal HelloPayload: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = ptype
	pkt.SourceUhid = source
	pkt.DestinationUhid = dest
	pkt.Ttl = 1
	pkt.Payload = body
	return pkt
}

func unicastsOf(t *testing.T, sender *fakeSender, ptype protocol.PacketType) []sentRecord {
	t.Helper()
	sender.mu.Lock()
	defer sender.mu.Unlock()
	out := make([]sentRecord, 0)
	for _, u := range sender.Unicasts {
		if u.Packet.Type == ptype {
			out = append(out, u)
		}
	}
	return out
}

func newTestService(t *testing.T, sender *fakeSender, opts ...Option) *Service {
	t.Helper()
	svc, err := NewService(sender, opts...)
	if err != nil {
		t.Fatalf("NewService: %v", err)
	}
	return svc
}

// ── Hello → HelloAck round-trip ───────────────────────────────

// TwoServices_ExchangeHelloAndHelloAck_BothLockInCapabilities mirrors the
// C# TwoServices_ExchangeHelloAndHelloAck_BothLockInCapabilities test.
func TestTwoServices_ExchangeHelloAndHelloAck_BothLockInCapabilities(t *testing.T) {
	senderA := newFakeSender(uhidAlice)
	senderB := newFakeSender(uhidBob)
	serviceA := newTestService(t, senderA)
	serviceB := newTestService(t, senderB)

	if err := serviceA.Initiate(context.Background(), uhidBob); err != nil {
		t.Fatalf("A.Initiate: %v", err)
	}

	hellos := unicastsOf(t, senderA, protocol.Hello)
	if len(hellos) != 1 {
		t.Fatalf("expected 1 Hello on the wire, got %d", len(hellos))
	}
	helloOnTheWire := hellos[0].Packet
	if helloOnTheWire.SourceUhid != uhidAlice || helloOnTheWire.DestinationUhid != uhidBob {
		t.Fatalf("Hello src/dst mismatch: src=%s dst=%s", helloOnTheWire.SourceUhid, helloOnTheWire.DestinationUhid)
	}

	if err := serviceB.HandleHello(context.Background(), helloOnTheWire); err != nil {
		t.Fatalf("B.HandleHello: %v", err)
	}

	acks := unicastsOf(t, senderB, protocol.HelloAck)
	if len(acks) != 1 {
		t.Fatalf("expected 1 HelloAck on the wire, got %d", len(acks))
	}
	ackOnTheWire := acks[0].Packet
	if ackOnTheWire.SourceUhid != uhidBob || ackOnTheWire.DestinationUhid != uhidAlice {
		t.Fatalf("HelloAck src/dst mismatch")
	}

	if err := serviceA.HandleHelloAck(context.Background(), ackOnTheWire); err != nil {
		t.Fatalf("A.HandleHelloAck: %v", err)
	}

	aSide, ok := serviceA.GetPeerCapabilities(uhidBob)
	if !ok {
		t.Fatalf("A has no record of Bob")
	}
	bSide, ok := serviceB.GetPeerCapabilities(uhidAlice)
	if !ok {
		t.Fatalf("B has no record of Alice")
	}
	if aSide.NegotiatedVersion != constants.CurrentProtocolVersion {
		t.Errorf("A negotiatedVersion: got %d want %d", aSide.NegotiatedVersion, constants.CurrentProtocolVersion)
	}
	if bSide.NegotiatedVersion != constants.CurrentProtocolVersion {
		t.Errorf("B negotiatedVersion: got %d want %d", bSide.NegotiatedVersion, constants.CurrentProtocolVersion)
	}
	// Default capabilities match on both sides → full set intersects to itself.
	for _, c := range DefaultCapabilities {
		if !aSide.HasCapability(c) {
			t.Errorf("A is missing capability %q", c)
		}
		if !bSide.HasCapability(c) {
			t.Errorf("B is missing capability %q", c)
		}
	}
}

// ── Version selection ─────────────────────────────────────────

func TestPeerWithHigherMaxVersion_NegotiatesOnOurMax(t *testing.T) {
	senderA := newFakeSender(uhidAlice)
	serviceA := newTestService(t, senderA, WithMinVersion(1), WithMaxVersion(2))

	hello := buildHelloPacket(t, protocol.Hello, uhidBob, uhidAlice, 1, 5, DefaultCapabilities, "test/1")
	if err := serviceA.HandleHello(context.Background(), hello); err != nil {
		t.Fatalf("HandleHello: %v", err)
	}
	caps, ok := serviceA.GetPeerCapabilities(uhidBob)
	if !ok || caps.NegotiatedVersion != 2 {
		t.Fatalf("expected negotiated v2, got %v ok=%v", caps, ok)
	}
}

func TestPeerWithLowerMaxVersion_NegotiatesOnTheirMax(t *testing.T) {
	senderA := newFakeSender(uhidAlice)
	serviceA := newTestService(t, senderA, WithMinVersion(1), WithMaxVersion(2))

	hello := buildHelloPacket(t, protocol.Hello, uhidBob, uhidAlice, 1, 1, DefaultCapabilities, "test/1")
	if err := serviceA.HandleHello(context.Background(), hello); err != nil {
		t.Fatalf("HandleHello: %v", err)
	}
	caps, ok := serviceA.GetPeerCapabilities(uhidBob)
	if !ok || caps.NegotiatedVersion != 1 {
		t.Fatalf("expected negotiated v1, got %v ok=%v", caps, ok)
	}
}

// ── Incompatible peer ─────────────────────────────────────────

func TestPeerWithNoOverlap_FiresIncompatiblePeer_AndIsNotRecorded(t *testing.T) {
	senderA := newFakeSender(uhidAlice)
	var captured *IncompatiblePeerEvent
	serviceA := newTestService(t, senderA,
		WithMinVersion(2),
		WithMaxVersion(3),
		WithIncompatiblePeerHandler(func(evt IncompatiblePeerEvent) {
			c := evt
			captured = &c
		}))

	hello := buildHelloPacket(t, protocol.Hello, uhidBob, uhidAlice, 4, 5, DefaultCapabilities, "test/1")
	if err := serviceA.HandleHello(context.Background(), hello); err != nil {
		t.Fatalf("HandleHello: %v", err)
	}

	if captured == nil {
		t.Fatalf("IncompatiblePeer was not fired")
	}
	if captured.PeerUhid != uhidBob {
		t.Errorf("PeerUhid: got %q want %q", captured.PeerUhid, uhidBob)
	}
	if captured.TheirMinVersion != 4 || captured.TheirMaxVersion != 5 {
		t.Errorf("their range: got %d..%d want 4..5", captured.TheirMinVersion, captured.TheirMaxVersion)
	}
	if captured.OurMinVersion != 2 || captured.OurMaxVersion != 3 {
		t.Errorf("our range: got %d..%d want 2..3", captured.OurMinVersion, captured.OurMaxVersion)
	}

	if len(unicastsOf(t, senderA, protocol.HelloAck)) != 0 {
		t.Errorf("HelloAck must not be sent for rejected peer")
	}
	if _, ok := serviceA.GetPeerCapabilities(uhidBob); ok {
		t.Errorf("rejected peer must not be recorded as negotiated")
	}
}

func TestPeerBelowOurMinVersion_FiresIncompatiblePeer(t *testing.T) {
	senderA := newFakeSender(uhidAlice)
	fired := false
	serviceA := newTestService(t, senderA,
		WithMinVersion(2),
		WithMaxVersion(3),
		WithIncompatiblePeerHandler(func(_ IncompatiblePeerEvent) { fired = true }))

	hello := buildHelloPacket(t, protocol.Hello, uhidBob, uhidAlice, 1, 1, DefaultCapabilities, "test/1")
	if err := serviceA.HandleHello(context.Background(), hello); err != nil {
		t.Fatalf("HandleHello: %v", err)
	}
	if !fired {
		t.Fatalf("expected IncompatiblePeer to fire")
	}
	if _, ok := serviceA.GetPeerCapabilities(uhidBob); ok {
		t.Errorf("rejected peer must not be recorded")
	}
}

// ── Backward-compat ───────────────────────────────────────────

func TestPeerNeverRepliesWithHelloAck_AssumeLegacyV1_LocksInV1Fallback(t *testing.T) {
	senderA := newFakeSender(uhidAlice)
	serviceA := newTestService(t, senderA)

	if err := serviceA.Initiate(context.Background(), uhidBob); err != nil {
		t.Fatalf("Initiate: %v", err)
	}
	serviceA.AssumeLegacyV1(uhidBob)

	caps, ok := serviceA.GetPeerCapabilities(uhidBob)
	if !ok {
		t.Fatalf("AssumeLegacyV1 did not install fallback")
	}
	if caps.NegotiatedVersion != 1 {
		t.Errorf("NegotiatedVersion: got %d want 1", caps.NegotiatedVersion)
	}
	if len(caps.Capabilities) != 0 {
		t.Errorf("expected empty capabilities, got %v", caps.Capabilities)
	}
	if caps.ImplementationVersion != "" {
		t.Errorf("ImplementationVersion: got %q want empty", caps.ImplementationVersion)
	}
}

func TestAssumeLegacyV1_AfterRealHelloAck_DoesNotOverwrite(t *testing.T) {
	senderA := newFakeSender(uhidAlice)
	serviceA := newTestService(t, senderA)

	hello := buildHelloPacket(t, protocol.Hello, uhidBob, uhidAlice, 1, 2, DefaultCapabilities, "test/1")
	if err := serviceA.HandleHello(context.Background(), hello); err != nil {
		t.Fatalf("HandleHello: %v", err)
	}
	before, _ := serviceA.GetPeerCapabilities(uhidBob)
	if before == nil {
		t.Fatalf("expected real record after HandleHello")
	}

	serviceA.AssumeLegacyV1(uhidBob)

	after, _ := serviceA.GetPeerCapabilities(uhidBob)
	if after != before {
		t.Fatalf("AssumeLegacyV1 overwrote a real HelloAck record")
	}
	if after.NegotiatedVersion != 2 || len(after.Capabilities) == 0 {
		t.Errorf("post-assume record has wrong shape: ver=%d caps=%v", after.NegotiatedVersion, after.Capabilities)
	}
}

// ── Capability intersection ──────────────────────────────────

func TestCapabilityIntersection_DropsCapabilitiesOnlyOneSideClaims(t *testing.T) {
	senderA := newFakeSender(uhidAlice)
	ourCaps := []string{"signal-x3dh", "dtn-custody", "sos"}
	serviceA := newTestService(t, senderA, WithCapabilities(ourCaps))

	// Peer claims [signal-x3dh, sos, voice]: intersection = [signal-x3dh, sos].
	hello := buildHelloPacket(t, protocol.Hello, uhidBob, uhidAlice,
		1, constants.CurrentProtocolVersion,
		[]string{"signal-x3dh", "sos", "voice"}, "test/1")
	if err := serviceA.HandleHello(context.Background(), hello); err != nil {
		t.Fatalf("HandleHello: %v", err)
	}
	caps, ok := serviceA.GetPeerCapabilities(uhidBob)
	if !ok {
		t.Fatalf("peer not recorded")
	}
	if len(caps.Capabilities) != 2 {
		t.Errorf("intersection size: got %d want 2 (caps=%v)", len(caps.Capabilities), caps.Capabilities)
	}
	if !caps.HasCapability("signal-x3dh") {
		t.Error("missing signal-x3dh")
	}
	if !caps.HasCapability("sos") {
		t.Error("missing sos")
	}
	if caps.HasCapability("voice") {
		t.Error("voice should have been dropped (we don't claim it)")
	}
	if caps.HasCapability("dtn-custody") {
		t.Error("dtn-custody should have been dropped (peer didn't claim it)")
	}
}

// ── Initiate semantics ────────────────────────────────────────

func TestInitiate_SendsExactlyOneHelloPerPeer(t *testing.T) {
	sender := newFakeSender(uhidAlice)
	service := newTestService(t, sender)

	for i := 0; i < 3; i++ {
		if err := service.Initiate(context.Background(), uhidBob); err != nil {
			t.Fatalf("Initiate %d: %v", i, err)
		}
	}

	hellos := unicastsOf(t, sender, protocol.Hello)
	if len(hellos) != 1 {
		t.Fatalf("expected 1 Hello, got %d", len(hellos))
	}
	if hellos[0].NextHopUhid != uhidBob {
		t.Errorf("nextHop: got %q want %q", hellos[0].NextHopUhid, uhidBob)
	}
}

func TestInitiate_SkipsLocalUhid(t *testing.T) {
	sender := newFakeSender(uhidAlice)
	service := newTestService(t, sender)

	if err := service.Initiate(context.Background(), uhidAlice); err != nil {
		t.Fatalf("Initiate(self): %v", err)
	}
	if len(sender.Unicasts) != 0 {
		t.Fatalf("expected no sends to self, got %d", len(sender.Unicasts))
	}
}

// ── Renegotiate ───────────────────────────────────────────────

func TestRenegotiate_ClearsCachedCapabilities_AllowsNewHello(t *testing.T) {
	sender := newFakeSender(uhidAlice)
	service := newTestService(t, sender)

	hello := buildHelloPacket(t, protocol.Hello, uhidBob, uhidAlice, 1, 2, DefaultCapabilities, "test/1")
	if err := service.HandleHello(context.Background(), hello); err != nil {
		t.Fatalf("HandleHello: %v", err)
	}
	if _, ok := service.GetPeerCapabilities(uhidBob); !ok {
		t.Fatalf("peer not recorded")
	}

	service.Renegotiate(uhidBob)
	if _, ok := service.GetPeerCapabilities(uhidBob); ok {
		t.Fatalf("Renegotiate did not clear record")
	}

	sender.clear()
	if err := service.Initiate(context.Background(), uhidBob); err != nil {
		t.Fatalf("post-Renegotiate Initiate: %v", err)
	}
	if got := len(unicastsOf(t, sender, protocol.Hello)); got != 1 {
		t.Errorf("post-Renegotiate Hello count: got %d want 1", got)
	}
}

// ── Malformed payload ─────────────────────────────────────────

func TestMalformedPayload_IsIgnored_NoExceptionsThrown(t *testing.T) {
	sender := newFakeSender(uhidAlice)
	service := newTestService(t, sender)

	// `{` followed by garbage — not valid JSON.
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.Hello
	pkt.SourceUhid = uhidBob
	pkt.DestinationUhid = uhidAlice
	pkt.Payload = []byte{0x7B, 0xFF, 0xFE, 0x00}

	if err := service.HandleHello(context.Background(), pkt); err != nil {
		t.Fatalf("HandleHello swallows malformed payload but returned err: %v", err)
	}
	if _, ok := service.GetPeerCapabilities(uhidBob); ok {
		t.Errorf("malformed peer must not be recorded")
	}
}

// ── PeerNegotiated callback ───────────────────────────────────

func TestPeerNegotiated_EventFires_OnSuccessfulHello(t *testing.T) {
	sender := newFakeSender(uhidAlice)
	var captured *PeerCapabilities
	service := newTestService(t, sender, WithPeerNegotiatedHandler(func(c *PeerCapabilities) {
		captured = c
	}))

	hello := buildHelloPacket(t, protocol.Hello, uhidBob, uhidAlice, 1, 2, DefaultCapabilities, "test/1")
	if err := service.HandleHello(context.Background(), hello); err != nil {
		t.Fatalf("HandleHello: %v", err)
	}
	if captured == nil {
		t.Fatalf("PeerNegotiated did not fire")
	}
	if captured.PeerUhid != uhidBob {
		t.Errorf("PeerUhid: got %q want %q", captured.PeerUhid, uhidBob)
	}
}

// ── GetAllNegotiated ──────────────────────────────────────────

func TestGetAllNegotiated_ReturnsEverySuccessfulPeer(t *testing.T) {
	sender := newFakeSender(uhidAlice)
	service := newTestService(t, sender)

	if err := service.HandleHello(context.Background(),
		buildHelloPacket(t, protocol.Hello, uhidBob, uhidAlice, 1, 2, DefaultCapabilities, "test/1")); err != nil {
		t.Fatal(err)
	}
	if err := service.HandleHello(context.Background(),
		buildHelloPacket(t, protocol.Hello, uhidCarol, uhidAlice, 1, 2, DefaultCapabilities, "test/1")); err != nil {
		t.Fatal(err)
	}

	all := service.GetAllNegotiated()
	if len(all) != 2 {
		t.Fatalf("expected 2 negotiated, got %d", len(all))
	}
	seen := make(map[string]bool)
	for _, c := range all {
		seen[c.PeerUhid] = true
	}
	if !seen[uhidBob] || !seen[uhidCarol] {
		t.Errorf("missing peer in GetAllNegotiated: seen=%v", seen)
	}
}

// ── Wire interop ──────────────────────────────────────────────

// Sanity check: HelloPayload JSON shape uses snake_case (matches C# wire).
func TestHelloPayload_JSONShape_IsSnakeCase(t *testing.T) {
	body, err := json.Marshal(HelloPayload{
		MinVersion:     1,
		MaxVersion:     2,
		Capabilities:   []string{"signal-x3dh"},
		Implementation: "aether-go/1.0.0",
	})
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	got := string(body)
	for _, key := range []string{`"min_version":1`, `"max_version":2`, `"capabilities":["signal-x3dh"]`, `"implementation":"aether-go/1.0.0"`} {
		if !contains(got, key) {
			t.Errorf("expected %q in JSON, got %s", key, got)
		}
	}
}

// Hello dispatch routes to the right handler based on PacketType.
func TestHandle_DispatchesByType(t *testing.T) {
	sender := newFakeSender(uhidAlice)
	service := newTestService(t, sender)

	// Hello → records peer + sends ack.
	if err := service.Handle(context.Background(),
		buildHelloPacket(t, protocol.Hello, uhidBob, uhidAlice, 1, 2, DefaultCapabilities, "test/1")); err != nil {
		t.Fatalf("Handle Hello: %v", err)
	}
	if len(unicastsOf(t, sender, protocol.HelloAck)) != 1 {
		t.Errorf("Hello dispatch did not produce a HelloAck")
	}

	// HelloAck → records peer, no further send.
	sender.clear()
	if err := service.Handle(context.Background(),
		buildHelloPacket(t, protocol.HelloAck, uhidCarol, uhidAlice, 1, 2, DefaultCapabilities, "test/1")); err != nil {
		t.Fatalf("Handle HelloAck: %v", err)
	}
	if len(sender.Unicasts) != 0 {
		t.Errorf("HelloAck dispatch should not send anything, got %d", len(sender.Unicasts))
	}
	if _, ok := service.GetPeerCapabilities(uhidCarol); !ok {
		t.Errorf("HelloAck peer not recorded")
	}

	// Non-handshake packets are ignored without error.
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.Data
	pkt.SourceUhid = uhidBob
	if err := service.Handle(context.Background(), pkt); err != nil {
		t.Errorf("Handle Data: unexpected err %v", err)
	}
}

func contains(s, sub string) bool {
	for i := 0; i+len(sub) <= len(s); i++ {
		if s[i:i+len(sub)] == sub {
			return true
		}
	}
	return false
}
