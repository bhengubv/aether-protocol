// SPDX-License-Identifier: MIT

package incentive

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/google/uuid"

	"github.com/bhengubv/aether-protocol/go/protocol"
)

// tipVectors mirrors fixtures/tipping/tip_packet_basic.json — the canonical cross-language parity
// source generated from the C# reference (TipPacketPayload.BuildCanonicalData + Ed25519). Every
// language port MUST reproduce canonical_bytes and signature byte-for-byte.
type tipVectors struct {
	Algorithm   string `json:"algorithm"`
	Ed25519Seed string `json:"ed25519_seed"`
	PublicKey   string `json:"public_key"`
	Cases       []struct {
		TipperUhid     string `json:"tipper_uhid"`
		RecipientUhid  string `json:"recipient_uhid"`
		Amount         string `json:"amount"`
		TrafficType    string `json:"traffic_type"`
		ReferenceID    string `json:"reference_id"`
		TimestampUnix  int64  `json:"timestamp_unix_ms"`
		CanonicalBytes string `json:"canonical_bytes"`
		Signature      string `json:"signature"`
	} `json:"cases"`
}

func loadTipVectors(t *testing.T) tipVectors {
	t.Helper()
	path := filepath.Join("..", "..", "fixtures", "tipping", "tip_packet_basic.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read fixtures: %v", err)
	}
	var v tipVectors
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("parse fixtures: %v", err)
	}
	return v
}

func mustHex(t *testing.T, s string) []byte {
	t.Helper()
	b, err := hex.DecodeString(s)
	if err != nil {
		t.Fatalf("decode hex %q: %v", s, err)
	}
	return b
}

// caseToPayload reconstructs a TipPacketPayload from a fixture case (without the signature).
func caseToPayload(t *testing.T, c struct {
	TipperUhid     string `json:"tipper_uhid"`
	RecipientUhid  string `json:"recipient_uhid"`
	Amount         string `json:"amount"`
	TrafficType    string `json:"traffic_type"`
	ReferenceID    string `json:"reference_id"`
	TimestampUnix  int64  `json:"timestamp_unix_ms"`
	CanonicalBytes string `json:"canonical_bytes"`
	Signature      string `json:"signature"`
}) *TipPacketPayload {
	t.Helper()
	p := &TipPacketPayload{
		TipperUhid:      c.TipperUhid,
		RecipientUhid:   c.RecipientUhid,
		Amount:          c.Amount,
		TrafficType:     c.TrafficType,
		TimestampUnixMs: c.TimestampUnix,
	}
	if c.ReferenceID != "" {
		id, err := uuid.Parse(c.ReferenceID)
		if err != nil {
			t.Fatalf("parse reference_id %q: %v", c.ReferenceID, err)
		}
		p.ReferenceID = &id
	}
	return p
}

// TestTipCanonicalBytesParity asserts BuildCanonicalData reproduces the fixture canonical_bytes
// byte-for-byte for every case (covers null reference_id → 16 zero bytes, and the .NET mixed-endian
// GUID byte order).
func TestTipCanonicalBytesParity(t *testing.T) {
	v := loadTipVectors(t)
	for i, c := range v.Cases {
		p := caseToPayload(t, c)
		got := hex.EncodeToString(p.BuildCanonicalData())
		if got != c.CanonicalBytes {
			t.Fatalf("case %d (%s): canonical bytes mismatch\n got=%s\nwant=%s",
				i, c.TipperUhid, got, c.CanonicalBytes)
		}
	}
}

// TestTipSignatureDeterministicParity asserts a fresh Ed25519 sign from the fixture seed reproduces
// the fixture signature exactly (Ed25519 is deterministic), and that the fixture signature verifies
// against the fixture public key.
func TestTipSignatureDeterministicParity(t *testing.T) {
	v := loadTipVectors(t)

	seed := mustHex(t, v.Ed25519Seed)
	if len(seed) != ed25519.SeedSize {
		t.Fatalf("seed size: got %d want %d", len(seed), ed25519.SeedSize)
	}
	priv := ed25519.NewKeyFromSeed(seed)
	pub := priv.Public().(ed25519.PublicKey)

	// The derived public key must match the fixture's published key.
	if got := hex.EncodeToString(pub); got != v.PublicKey {
		t.Fatalf("public key: got %s want %s", got, v.PublicKey)
	}

	for i, c := range v.Cases {
		p := caseToPayload(t, c)
		canonical := p.BuildCanonicalData()

		// Deterministic re-sign reproduces the exact fixture signature.
		sig := ed25519.Sign(priv, canonical)
		if got := hex.EncodeToString(sig); got != c.Signature {
			t.Fatalf("case %d (%s): signature mismatch\n got=%s\nwant=%s",
				i, c.TipperUhid, got, c.Signature)
		}

		// The fixture signature verifies against the fixture public key.
		wantSig := mustHex(t, c.Signature)
		if !ed25519.Verify(pub, canonical, wantSig) {
			t.Fatalf("case %d (%s): fixture signature failed to verify", i, c.TipperUhid)
		}
	}
}

// TestTipPayloadJSONRoundTrip proves a signed payload survives a JSON round-trip with canonical bytes
// and signature intact.
func TestTipPayloadJSONRoundTrip(t *testing.T) {
	v := loadTipVectors(t)
	seed := mustHex(t, v.Ed25519Seed)
	priv := ed25519.NewKeyFromSeed(seed)

	for i, c := range v.Cases {
		p := caseToPayload(t, c)
		p.Signature = ed25519.Sign(priv, p.BuildCanonicalData())

		js, err := p.ToJSON()
		if err != nil {
			t.Fatal(err)
		}
		back, err := ParseTipPacketPayload(js)
		if err != nil {
			t.Fatal(err)
		}

		if !bytes.Equal(back.BuildCanonicalData(), p.BuildCanonicalData()) {
			t.Fatalf("case %d: canonical bytes changed across JSON round-trip", i)
		}
		if !bytes.Equal(back.Signature, p.Signature) {
			t.Fatalf("case %d: signature changed across JSON round-trip", i)
		}
		if back.Amount != c.Amount {
			t.Fatalf("case %d: amount changed across round-trip: got %q want %q", i, back.Amount, c.Amount)
		}
		// reference_id presence/absence must survive.
		if (back.ReferenceID == nil) != (p.ReferenceID == nil) {
			t.Fatalf("case %d: reference_id nullity changed across round-trip", i)
		}
		if p.ReferenceID != nil && *back.ReferenceID != *p.ReferenceID {
			t.Fatalf("case %d: reference_id value changed across round-trip", i)
		}
	}
}

// ── test doubles for the MeshTipService dispatch test ─────────────────────────

type fakeSender struct {
	local      string
	sent       []*protocol.MeshPacket
	broadcasts []*protocol.MeshPacket
}

func (f *fakeSender) LocalUhid() string { return f.local }
func (f *fakeSender) Send(ctx context.Context, pkt *protocol.MeshPacket, nextHop string) (bool, error) {
	f.sent = append(f.sent, pkt)
	return true, nil
}
func (f *fakeSender) Broadcast(ctx context.Context, pkt *protocol.MeshPacket) (int, error) {
	f.broadcasts = append(f.broadcasts, pkt)
	return 1, nil
}

type fakeSigner struct{}

func (fakeSigner) SignPacket(pkt *protocol.MeshPacket) (*protocol.MeshPacket, error) {
	cp := *pkt
	cp.Signature = []byte("envelope-sig")
	cp.PacketNonce = []byte{1, 2, 3, 4, 5, 6, 7, 8}
	return &cp, nil
}

type seedIdentity struct{ priv ed25519.PrivateKey }

func (s seedIdentity) SignData(data []byte) ([]byte, error) {
	return ed25519.Sign(s.priv, data), nil
}

type recordingSettler struct {
	calls []*TipPacketPayload
}

func (r *recordingSettler) SettleMeshTip(ctx context.Context, payload *TipPacketPayload) error {
	r.calls = append(r.calls, payload)
	return nil
}

// TestSendTipProducesFixtureSignature wires the full MeshTipService send path with the fixture seed
// and confirms the signed payload inside the emitted TipPacket(24) carries the exact fixture
// signature — proving the service-level flow is byte-identical to C#.
func TestSendTipProducesFixtureSignature(t *testing.T) {
	v := loadTipVectors(t)
	seed := mustHex(t, v.Ed25519Seed)
	priv := ed25519.NewKeyFromSeed(seed)

	c := v.Cases[0]
	sender := &fakeSender{local: c.TipperUhid}
	svc := NewMeshTipService(sender, fakeSigner{}, seedIdentity{priv}, nil, nil)

	id, err := uuid.Parse(c.ReferenceID)
	if err != nil {
		t.Fatal(err)
	}

	signed, err := svc.SendTip(context.Background(), c.RecipientUhid, c.Amount, c.TrafficType, &id, c.TimestampUnix)
	if err != nil {
		t.Fatal(err)
	}
	if signed.Type != protocol.TipPacket {
		t.Fatalf("emitted packet type: got %s want TipPacket", signed.Type)
	}

	var payload TipPacketPayload
	if err := json.Unmarshal(signed.Payload, &payload); err != nil {
		t.Fatal(err)
	}
	if got := hex.EncodeToString(payload.Signature); got != c.Signature {
		t.Fatalf("service-emitted signature mismatch\n got=%s\nwant=%s", got, c.Signature)
	}
	// With no route resolver, the tip must have been broadcast.
	if len(sender.broadcasts) != 1 || len(sender.sent) != 0 {
		t.Fatalf("expected 1 broadcast and 0 unicast, got %d/%d", len(sender.broadcasts), len(sender.sent))
	}
}

// TestHandleTipPacketRoutesToSettlementHook proves an inbound TipPacket(24) is dispatched to the
// host settlement hook (the Go analog of IAetherNetIncentiveProvider.SettleMeshTip), and a packet
// with a malformed signature is dropped before the hook fires.
func TestHandleTipPacketRoutesToSettlementHook(t *testing.T) {
	v := loadTipVectors(t)
	seed := mustHex(t, v.Ed25519Seed)
	priv := ed25519.NewKeyFromSeed(seed)
	c := v.Cases[0]

	// Local node is the addressed recipient, so no onward relay happens.
	sender := &fakeSender{local: c.RecipientUhid}
	settler := &recordingSettler{}
	svc := NewMeshTipService(sender, fakeSigner{}, seedIdentity{priv}, nil, settler)

	// Build a well-formed, signed tip payload.
	p := caseToPayload(t, c)
	p.Signature = ed25519.Sign(priv, p.BuildCanonicalData())
	body, err := p.ToJSON()
	if err != nil {
		t.Fatal(err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.TipPacket
	pkt.SourceUhid = c.TipperUhid
	pkt.DestinationUhid = c.RecipientUhid
	pkt.Payload = body

	handled, err := svc.HandleTipPacket(context.Background(), pkt)
	if err != nil {
		t.Fatal(err)
	}
	if !handled {
		t.Fatal("expected the tip to be handled")
	}
	if len(settler.calls) != 1 {
		t.Fatalf("settlement hook should fire once, fired %d times", len(settler.calls))
	}
	if settler.calls[0].TipperUhid != c.TipperUhid {
		t.Fatalf("settlement hook got wrong payload: %s", settler.calls[0].TipperUhid)
	}

	// A malformed signature (wrong length) must be dropped before the hook fires.
	settler.calls = nil
	p.Signature = []byte{0x00, 0x01, 0x02}
	badBody, _ := p.ToJSON()
	badPkt := protocol.NewMeshPacket()
	badPkt.Type = protocol.TipPacket
	badPkt.SourceUhid = c.TipperUhid
	badPkt.DestinationUhid = c.RecipientUhid
	badPkt.Payload = badBody

	handled, err = svc.HandleTipPacket(context.Background(), badPkt)
	if err != nil {
		t.Fatal(err)
	}
	if handled {
		t.Fatal("a malformed-signature tip must be dropped")
	}
	if len(settler.calls) != 0 {
		t.Fatal("settlement hook must NOT fire for a malformed-signature tip")
	}
}

// TestNoopSettlementProvider confirms the default no-op settles nothing without error.
func TestNoopSettlementProvider(t *testing.T) {
	if err := (NoopMeshTipSettlementProvider{}).SettleMeshTip(context.Background(), &TipPacketPayload{}); err != nil {
		t.Fatalf("no-op settlement returned error: %v", err)
	}
}
