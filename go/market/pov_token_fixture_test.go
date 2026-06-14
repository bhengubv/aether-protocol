// SPDX-License-Identifier: MIT

package market

import (
	"bytes"
	"context"
	"crypto/ed25519"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/bhengubv/aether-protocol/go/protocol"
)

// povVectors mirrors fixtures/market/pov_token_basic.json — the canonical cross-language parity source
// generated from the C# reference (PoVTokenCodec.BuildSignableTokenData + Ed25519). Every language
// port MUST reproduce canonical_body and witness_signature byte-for-byte.
type povVectors struct {
	Algorithm        string `json:"algorithm"`
	WitnessSeed      string `json:"witness_seed"`
	WitnessPublicKey string `json:"witness_public_key"`
	Cases            []struct {
		SubjectUhid     string `json:"subject_uhid"`
		TimestampTicks  int64  `json:"timestamp_ticks"`
		Transport       string `json:"transport"`
		TransportByte   byte   `json:"transport_byte"`
		CanonicalBody   string `json:"canonical_body"`
		WitnessSig      string `json:"witness_signature"`
	} `json:"cases"`
}

func loadPovVectors(t *testing.T) povVectors {
	t.Helper()
	path := filepath.Join("..", "..", "fixtures", "market", "pov_token_basic.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("read fixtures: %v", err)
	}
	var v povVectors
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

// TestPoVCanonicalBodyParity asserts BuildSignableTokenData reproduces the fixture canonical_body
// byte-for-byte for every case (covers all three transports + the .NET DateTime.Ticks i64 LE field).
func TestPoVCanonicalBodyParity(t *testing.T) {
	v := loadPovVectors(t)
	for i, c := range v.Cases {
		got := hex.EncodeToString(BuildSignableTokenData(c.SubjectUhid, c.TimestampTicks, PoVTransportType(c.TransportByte)))
		if got != c.CanonicalBody {
			t.Fatalf("case %d (%s): canonical body mismatch\n got=%s\nwant=%s",
				i, c.SubjectUhid, got, c.CanonicalBody)
		}
		// Transport enum byte must match the named transport.
		if PoVTransportType(c.TransportByte).String() != c.Transport {
			t.Fatalf("case %d: transport name mismatch: %s != %s",
				i, PoVTransportType(c.TransportByte).String(), c.Transport)
		}
	}
}

// TestPoVWitnessSignatureDeterministicParity asserts a fresh Ed25519 sign from the fixture witness
// seed reproduces the fixture witness_signature exactly (Ed25519 is deterministic), and that the
// fixture signature verifies against the fixture witness public key.
func TestPoVWitnessSignatureDeterministicParity(t *testing.T) {
	v := loadPovVectors(t)

	seed := mustHex(t, v.WitnessSeed)
	if len(seed) != ed25519.SeedSize {
		t.Fatalf("seed size: got %d want %d", len(seed), ed25519.SeedSize)
	}
	priv := ed25519.NewKeyFromSeed(seed)
	pub := priv.Public().(ed25519.PublicKey)

	if got := hex.EncodeToString(pub); got != v.WitnessPublicKey {
		t.Fatalf("witness public key: got %s want %s", got, v.WitnessPublicKey)
	}

	for i, c := range v.Cases {
		body := BuildSignableTokenData(c.SubjectUhid, c.TimestampTicks, PoVTransportType(c.TransportByte))

		sig := ed25519.Sign(priv, body)
		if got := hex.EncodeToString(sig); got != c.WitnessSig {
			t.Fatalf("case %d (%s): witness signature mismatch\n got=%s\nwant=%s",
				i, c.SubjectUhid, got, c.WitnessSig)
		}

		wantSig := mustHex(t, c.WitnessSig)
		if !ed25519.Verify(pub, body, wantSig) {
			t.Fatalf("case %d (%s): fixture witness signature failed to verify", i, c.SubjectUhid)
		}
	}
}

// TestPoVTokenJSONRoundTrip proves a token with both signatures survives a JSON round-trip with its
// canonical body intact.
func TestPoVTokenJSONRoundTrip(t *testing.T) {
	v := loadPovVectors(t)
	seed := mustHex(t, v.WitnessSeed)
	priv := ed25519.NewKeyFromSeed(seed)

	for i, c := range v.Cases {
		tok := &PoVToken{
			WitnessUhid:      "aether:witness:zz",
			SubjectUhid:      c.SubjectUhid,
			TimestampTicks:   c.TimestampTicks,
			TransportUsed:    PoVTransportType(c.TransportByte),
			WitnessSignature: ed25519.Sign(priv, BuildSignableTokenData(c.SubjectUhid, c.TimestampTicks, PoVTransportType(c.TransportByte))),
		}

		js, err := tok.ToJSON()
		if err != nil {
			t.Fatal(err)
		}
		back, err := ParsePoVToken(js)
		if err != nil {
			t.Fatal(err)
		}
		if !bytes.Equal(back.SignableData(), tok.SignableData()) {
			t.Fatalf("case %d: canonical body changed across JSON round-trip", i)
		}
		if !bytes.Equal(back.WitnessSignature, tok.WitnessSignature) {
			t.Fatalf("case %d: witness signature changed across JSON round-trip", i)
		}
		if back.TransportUsed != tok.TransportUsed {
			t.Fatalf("case %d: transport changed across round-trip", i)
		}
	}
}

// TestTicksTimeConversionRoundTrip confirms the .NET ticks <-> Go time conversion is lossless at
// 100ns resolution for the fixture timestamps.
func TestTicksTimeConversionRoundTrip(t *testing.T) {
	v := loadPovVectors(t)
	for i, c := range v.Cases {
		if got := TimeToTicks(TicksToTime(c.TimestampTicks)); got != c.TimestampTicks {
			t.Fatalf("case %d: ticks round-trip lost precision: got %d want %d", i, got, c.TimestampTicks)
		}
	}
}

// ── test doubles for the exchange-service flow ────────────────────────────────

type fakeSender struct {
	local string
	sent  []*protocol.MeshPacket
}

func (f *fakeSender) LocalUhid() string { return f.local }
func (f *fakeSender) Send(ctx context.Context, pkt *protocol.MeshPacket, subject string) (bool, error) {
	f.sent = append(f.sent, pkt)
	return true, nil
}

// realIdentity signs/verifies with real Ed25519 — the local node's identity key.
type realIdentity struct{ priv ed25519.PrivateKey }

func (r realIdentity) SignData(data []byte) ([]byte, error) { return ed25519.Sign(r.priv, data), nil }
func (r realIdentity) VerifySignature(pub, data, sig []byte) bool {
	return ed25519.Verify(ed25519.PublicKey(pub), data, sig)
}

// passSigner stamps a real Ed25519 envelope signature with the node's key and always verifies fresh
// (freshness/replay are exercised separately in the C# layer; here we focus on the body crypto).
type passSigner struct {
	priv ed25519.PrivateKey
	seen map[string]bool
}

func (s *passSigner) SignPacket(pkt *protocol.MeshPacket) (*protocol.MeshPacket, error) {
	cp := *pkt
	cp.PacketNonce = []byte{9, 9, 9, 9, 9, 9, 9, 9}
	cp.Signature = ed25519.Sign(s.priv, []byte(cp.SourceUhid+":"+cp.DestinationUhid))
	return &cp, nil
}
func (s *passSigner) VerifyPacket(pkt *protocol.MeshPacket, senderPub []byte) (bool, error) {
	// Replay-dedup on the nonce (mirrors the C# IPacketSigningService contract).
	if s.seen == nil {
		s.seen = make(map[string]bool)
	}
	key := pkt.SourceUhid + ":" + hex.EncodeToString(pkt.PacketNonce)
	if s.seen[key] {
		return false, nil
	}
	s.seen[key] = true
	return ed25519.Verify(ed25519.PublicKey(senderPub), []byte(pkt.SourceUhid+":"+pkt.DestinationUhid), pkt.Signature), nil
}

// TestPoVExchangeFullFlow exercises the on-mesh exchange end-to-end: the witness issues a token over
// packet 43; the subject verifies the witness Ed25519 signature, counter-signs, and records it; and
// BOTH signatures then verify against their respective keys.
func TestPoVExchangeFullFlow(t *testing.T) {
	witnessPub, witnessPriv, _ := ed25519.GenerateKey(nil)
	subjectPub, subjectPriv, _ := ed25519.GenerateKey(nil)

	const witnessUhid = "aether:node:witness"
	const subjectUhid = "aether:node:subject"

	// Witness side.
	wSender := &fakeSender{local: witnessUhid}
	wSigner := &passSigner{priv: witnessPriv}
	witness := NewPoVTokenExchangeService(wSender, wSigner, realIdentity{witnessPriv})

	token, err := witness.IssueToken(context.Background(), subjectUhid, TransportBle)
	if err != nil {
		t.Fatal(err)
	}
	if token == nil {
		t.Fatal("witness refused to issue a valid token")
	}
	if len(wSender.sent) != 1 {
		t.Fatalf("expected exactly 1 directed send, got %d", len(wSender.sent))
	}
	exchangePkt := wSender.sent[0]
	if exchangePkt.Type != protocol.PoVTokenExchange {
		t.Fatalf("issued packet type: got %s want PoVTokenExchange(43)", exchangePkt.Type)
	}
	if exchangePkt.Ttl != 1 {
		t.Fatalf("issued packet TTL: got %d want 1 (one short-range hop)", exchangePkt.Ttl)
	}

	// Subject side receives the witness's packet.
	sSender := &fakeSender{local: subjectUhid}
	sSigner := &passSigner{priv: subjectPriv}
	subject := NewPoVTokenExchangeService(sSender, sSigner, realIdentity{subjectPriv})

	var received *PoVToken
	subject.OnTokenReceived = func(tok *PoVToken) { received = tok }

	accepted, err := subject.HandleTokenExchange(context.Background(), exchangePkt, witnessPub)
	if err != nil {
		t.Fatal(err)
	}
	if !accepted {
		t.Fatal("subject rejected a valid witness token")
	}
	if received == nil {
		t.Fatal("OnTokenReceived did not fire")
	}

	// BOTH signatures must now verify over the same canonical body.
	body := received.SignableData()
	if !ed25519.Verify(witnessPub, body, received.WitnessSignature) {
		t.Fatal("witness signature failed to verify on the accepted token")
	}
	if !ed25519.Verify(subjectPub, body, received.SubjectSignature) {
		t.Fatal("subject countersignature failed to verify on the accepted token")
	}

	// Score reflects one unique witness for the subject.
	score := subject.GetScore(subjectUhid)
	if score.UniqueWitnesses != 1 {
		t.Fatalf("expected 1 unique witness, got %d", score.UniqueWitnesses)
	}

	// Replaying the same packet is rejected by the signer's nonce dedup.
	replay, err := subject.HandleTokenExchange(context.Background(), exchangePkt, witnessPub)
	if err != nil {
		t.Fatal(err)
	}
	if replay {
		t.Fatal("a replayed PoV exchange packet must be rejected")
	}
}

// TestPoVExchangeRejectsSelfVouchAndRemoteMint confirms the hard invariants: no self-vouch and no
// non-short-range minting.
func TestPoVExchangeRejectsSelfVouchAndRemoteMint(t *testing.T) {
	_, priv, _ := ed25519.GenerateKey(nil)
	sender := &fakeSender{local: "aether:node:self"}
	svc := NewPoVTokenExchangeService(sender, &passSigner{priv: priv}, realIdentity{priv})

	// Self-vouch refused.
	if tok, _ := svc.IssueToken(context.Background(), "aether:node:self", TransportBle); tok != nil {
		t.Fatal("a node must not be able to vouch for itself")
	}
	// Non-short-range refused (transport byte 9 is not BLE/NFC/NearLink).
	if tok, _ := svc.IssueToken(context.Background(), "aether:node:other", PoVTransportType(9)); tok != nil {
		t.Fatal("PoV must refuse to mint over a non-short-range transport")
	}
	if len(sender.sent) != 0 {
		t.Fatal("no packet should have been sent for refused issuances")
	}
}
