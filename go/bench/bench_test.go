// SPDX-License-Identifier: MIT

// Package bench holds the Go benchmark harness for aether-protocol.
//
// Mirrors the C# Aether.Benchmarks suite (commit 9b334a9) — the same hot
// paths so a regression in either language shows up as a delta against the
// committed baseline. Run with:
//
//	cd go && go test -bench=. -benchmem ./bench/
//
// Pin a baseline:
//
//	cd go && go test -bench=. -benchmem -count=3 ./bench/ | tee bench/baseline.txt
//
// The benches only call exported APIs from the security/, protocol/, and
// routing/ packages. Lower-level primitives (X25519 ECDH, HKDF) are
// re-implemented here against the Go stdlib (crypto/ecdh + crypto/hkdf via
// golang.org/x/crypto) — same algorithms the production code uses, so the
// numbers are directly comparable to the C# PrimitivesBenchmarks.
package bench

import (
	"bytes"
	"context"
	"crypto/ecdh"
	"crypto/rand"
	"crypto/sha256"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
	"github.com/bhengubv/aether-protocol/go/security"
	"golang.org/x/crypto/hkdf"
)

const (
	aliceUhid = "alice-uhid"
	bobUhid   = "bob-uhid"
)

var plaintextSmall = []byte("hello, mesh")

// ─── X25519 ECDH ─────────────────────────────────────────────────────────

// BenchmarkX25519Agree pins a baseline for one ECDH agreement — the
// inner-loop primitive of X3DH (4x per session establishment) and DH-ratchet
// (2x per ratchet step).
func BenchmarkX25519Agree(b *testing.B) {
	curve := ecdh.X25519()
	priv, err := curve.GenerateKey(rand.Reader)
	if err != nil {
		b.Fatal(err)
	}
	peerPriv, err := curve.GenerateKey(rand.Reader)
	if err != nil {
		b.Fatal(err)
	}
	peerPub := peerPriv.PublicKey()

	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		_, err := priv.ECDH(peerPub)
		if err != nil {
			b.Fatal(err)
		}
	}
}

// BenchmarkHkdfSha256_64Bytes pins KDF_RK per Signal §5.2 — 32-byte new
// root + 32-byte new chain = 64 bytes out, called once per DH-ratchet step.
func BenchmarkHkdfSha256_64Bytes(b *testing.B) {
	ikm := make([]byte, 32)
	salt := make([]byte, 32)
	info := []byte("aether-ratchet-rk-v1")
	rand.Read(ikm)
	rand.Read(salt)
	out := make([]byte, 64)

	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		r := hkdf.New(sha256.New, ikm, salt, info)
		if _, err := r.Read(out); err != nil {
			b.Fatal(err)
		}
	}
}

// ─── Signal: X3DH + Double Ratchet ──────────────────────────────────────

// BenchmarkX3DHEstablish pins the cost of a full pre-key bundle process —
// 4 X25519 agreements + HKDF root derivation. One-shot per peer.
func BenchmarkX3DHEstablish(b *testing.B) {
	bob, err := security.NewSignalProtocolService()
	if err != nil {
		b.Fatal(err)
	}

	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		// Each iteration uses a fresh initiator (so the session state
		// dictionary doesn't grow unbounded) and a fresh bundle (so an
		// OPK is consumed each round and Bob's pool is exercised).
		b.StopTimer()
		alice, err := security.NewSignalProtocolService()
		if err != nil {
			b.Fatal(err)
		}
		alice.GeneratePreKeyBundle(aliceUhid)
		bundle, err := bob.GeneratePreKeyBundle(bobUhid)
		if err != nil {
			b.Fatal(err)
		}
		b.StartTimer()
		if err := alice.ProcessPreKeyBundle(bundle); err != nil {
			b.Fatal(err)
		}
	}
}

// BenchmarkSignalEncrypt benches the steady-state Encrypt path — 1 HMAC
// chain step + AES-GCM. Excludes the one-shot X3DH cost by warming the
// session before the loop starts.
func BenchmarkSignalEncrypt(b *testing.B) {
	alice, bob := warmedPair(b)
	plaintext := make([]byte, 256)
	rand.Read(plaintext)

	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		if _, err := alice.Encrypt(bobUhid, plaintext); err != nil {
			b.Fatal(err)
		}
	}
	_ = bob // keep the receiver alive so the session pair isn't GC'd mid-bench
}

// BenchmarkSignalDecrypt benches the steady-state Decrypt path. Each
// iteration must consume a freshly-encrypted payload (the receive ratchet
// advances, so re-decrypting the same bytes is not allowed). The setup
// cost of producing the payload is excluded via b.StopTimer / StartTimer.
func BenchmarkSignalDecrypt(b *testing.B) {
	alice, bob := warmedPair(b)
	plaintext := make([]byte, 256)
	rand.Read(plaintext)

	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		b.StopTimer()
		payload, err := alice.Encrypt(bobUhid, plaintext)
		if err != nil {
			b.Fatal(err)
		}
		b.StartTimer()
		if _, err := bob.Decrypt(aliceUhid, payload); err != nil {
			b.Fatal(err)
		}
	}
}

// warmedPair builds an Alice/Bob pair with a fully-primed Double Ratchet
// (PreKey message sent + decrypted) so subsequent Encrypt/Decrypt benches
// measure the steady-state chain step rather than the one-shot X3DH cost.
func warmedPair(b *testing.B) (*security.SignalProtocolService, *security.SignalProtocolService) {
	b.Helper()
	alice, err := security.NewSignalProtocolService()
	if err != nil {
		b.Fatal(err)
	}
	bob, err := security.NewSignalProtocolService()
	if err != nil {
		b.Fatal(err)
	}
	bobBundle, err := bob.GeneratePreKeyBundle(bobUhid)
	if err != nil {
		b.Fatal(err)
	}
	if _, err := alice.GeneratePreKeyBundle(aliceUhid); err != nil {
		b.Fatal(err)
	}
	if err := alice.ProcessPreKeyBundle(bobBundle); err != nil {
		b.Fatal(err)
	}
	first, err := alice.Encrypt(bobUhid, plaintextSmall)
	if err != nil {
		b.Fatal(err)
	}
	if _, err := bob.Decrypt(aliceUhid, first); err != nil {
		b.Fatal(err)
	}
	return alice, bob
}

// ─── Wire-format serializer ─────────────────────────────────────────────

// BenchmarkPacketSerialize pins the Serialize hot path on a representative
// Data packet. Every packet on the mesh runs through this on send.
func BenchmarkPacketSerialize(b *testing.B) {
	pkt := makePacket(50)
	ps := &protocol.PacketSerializer{}

	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		if _, err := ps.Serialize(pkt); err != nil {
			b.Fatal(err)
		}
	}
}

// BenchmarkPacketSerialize_Large pins Serialize on a 4 KB payload (typical
// chunked-data or video-frame packet).
func BenchmarkPacketSerialize_Large(b *testing.B) {
	pkt := makePacket(4096)
	ps := &protocol.PacketSerializer{}

	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		if _, err := ps.Serialize(pkt); err != nil {
			b.Fatal(err)
		}
	}
}

// BenchmarkPacketDeserialize pins the Deserialize hot path. Every hop runs
// this on receive; a regression multiplies across every router.
func BenchmarkPacketDeserialize(b *testing.B) {
	ps := &protocol.PacketSerializer{}
	pkt := makePacket(50)
	wire, err := ps.Serialize(pkt)
	if err != nil {
		b.Fatal(err)
	}

	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		if _, err := ps.Deserialize(wire); err != nil {
			b.Fatal(err)
		}
	}
}

// BenchmarkPacketRoundTrip combines Serialize + Deserialize — useful as a
// single-number regression detector that catches changes in either side.
func BenchmarkPacketRoundTrip(b *testing.B) {
	ps := &protocol.PacketSerializer{}
	pkt := makePacket(50)

	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		wire, err := ps.Serialize(pkt)
		if err != nil {
			b.Fatal(err)
		}
		got, err := ps.Deserialize(wire)
		if err != nil {
			b.Fatal(err)
		}
		// Defeat dead-store elimination — touch a field so the compiler
		// doesn't optimise the deserialize away.
		if got == nil || len(got.SourceUhid) == 0 {
			b.Fatal("unexpected nil/empty packet")
		}
	}
}

func makePacket(payloadSize int) *protocol.MeshPacket {
	pkt := protocol.NewMeshPacket()
	pkt.ID = uuid.New()
	pkt.Type = protocol.Data
	pkt.SourceUhid = "alice-uhid-0001"
	pkt.DestinationUhid = "bob-uhid-0002"
	pkt.Ttl = 7
	pkt.Priority = 1
	pkt.ProtocolVersion = 2
	pkt.TimestampMs = time.Now().UnixMilli()
	pkt.PacketNonce = make([]byte, 8)
	rand.Read(pkt.PacketNonce)
	pkt.Payload = make([]byte, payloadSize)
	rand.Read(pkt.Payload)
	pkt.Signature = make([]byte, 64)
	rand.Read(pkt.Signature)
	return pkt
}

// ─── Routing ────────────────────────────────────────────────────────────

// BenchmarkRouteStore_Lookup pins the cached-route hot path — the steady
// state for every outbound packet that already has a route. Falls back
// from FindRoute (which would otherwise broadcast an RREQ) to the
// in-memory store's Get directly, so no transport / fakery is required.
func BenchmarkRouteStore_Lookup(b *testing.B) {
	store := routing.NewInMemoryRouteStore()
	ctx := context.Background()
	entry := &models.RouteEntry{
		DestinationUhid: bobUhid,
		NextHop:         "relay-uhid",
		HopCount:        2,
		ExpiresAt:       time.Now().Add(1 * time.Hour),
		QualityScore:    90,
		SourceUhid:      aliceUhid,
	}
	if err := store.Save(ctx, entry); err != nil {
		b.Fatal(err)
	}

	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		got, err := store.Get(ctx, bobUhid)
		if err != nil {
			b.Fatal(err)
		}
		if got == nil {
			b.Fatal("expected cached route")
		}
	}
}

// BenchmarkRouteStore_Save pins the cost of installing a new route entry —
// what happens on every successful RREP arrival.
func BenchmarkRouteStore_Save(b *testing.B) {
	store := routing.NewInMemoryRouteStore()
	ctx := context.Background()

	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		entry := &models.RouteEntry{
			DestinationUhid: "dest",
			NextHop:         "hop",
			HopCount:        1,
			ExpiresAt:       time.Now().Add(1 * time.Hour),
			QualityScore:    100,
			SourceUhid:      aliceUhid,
		}
		if err := store.Save(ctx, entry); err != nil {
			b.Fatal(err)
		}
	}
}

// ensure imports are not flagged unused if a bench is conditionally
// compiled out — bytes is referenced here so `goimports` keeps it.
var _ = bytes.Equal
