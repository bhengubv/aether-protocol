// SPDX-License-Identifier: MIT

package routing

import (
	"context"
	"crypto/rand"
	"testing"

	"github.com/google/uuid"
	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/security"
)

// Security acceptance tests for fail-closed RREP verification (Gap 3), mirroring
// the C# RouteReplyVerificationTests. Proves the four properties of the hardened
// routing layer:
//
//	(a) a Service with NO verifier supplied REJECTS an RREP — no forward route installed;
//	(b) an Ed25519RouteReplyVerifier whose resolver returns the correct public key
//	    ACCEPTS a validly-signed RREP — forward route installed;
//	(c) a forged RREP (signed by a DIFFERENT key) and an unsigned RREP are BOTH rejected;
//	(c') an unknown signer (resolver returns nil) is rejected even when validly self-signed.
//
// Signed RREPs are built with a real Ed25519 identity via the production signing
// primitive (SignalProtocolService.SignData over PacketSigningService's canonical
// signable bytes), so this exercises actual signature verification, not a stub.
// Assertions are on the observable side effect: presence/absence of the forward
// route in the store.

const (
	acceptLocal  = "local-uhid"
	acceptSource = "carol"
)

// newRrepForVerify builds an unsigned RREP from acceptSource to acceptLocal.
func newRrepForVerify() *protocol.MeshPacket {
	pkt := protocol.NewMeshPacket()
	pkt.ID = uuid.New()
	pkt.Type = protocol.RouteReply
	pkt.SourceUhid = acceptSource
	pkt.DestinationUhid = acceptLocal
	pkt.Ttl = constants.DefaultTtl
	nonce := make([]byte, 8)
	_, _ = rand.Read(nonce)
	pkt.PacketNonce = nonce
	return pkt
}

// signRrep signs rrep with the given identity over the canonical signable bytes,
// exactly as a real source node would (the Go equivalent of C#'s
// PacketSigningService.SignPacketAsync).
func signRrep(t *testing.T, rrep *protocol.MeshPacket, identity *security.SignalProtocolService, signer *security.PacketSigningService) *protocol.MeshPacket {
	t.Helper()
	signable := signer.ComputeSignableData(
		rrep.PacketNonce,
		rrep.TimestampMs,
		byte(rrep.Type),
		rrep.SourceUhid,
		rrep.DestinationUhid,
		rrep.Payload,
		rrep.Ttl,
		rrep.Priority,
	)
	sig, err := identity.SignData(signable)
	if err != nil {
		t.Fatalf("signing RREP: %v", err)
	}
	rrep.Signature = sig
	return rrep
}

// newIdentity creates a fresh Signal service with a distinct Ed25519 identity.
func newIdentity(t *testing.T) *security.SignalProtocolService {
	t.Helper()
	id, err := security.NewSignalProtocolService()
	if err != nil {
		t.Fatalf("creating identity: %v", err)
	}
	return id
}

// stubKeyResolver is a minimal in-test UHID->public-key map for the verifier.
type stubKeyResolver struct {
	keys map[string][]byte
}

func newStubKeyResolver() *stubKeyResolver {
	return &stubKeyResolver{keys: make(map[string][]byte)}
}

func (r *stubKeyResolver) add(uhid string, publicKey []byte) *stubKeyResolver {
	r.keys[uhid] = publicKey
	return r
}

func (r *stubKeyResolver) ResolvePublicKey(sourceUhid string) []byte {
	return r.keys[sourceUhid]
}

// newEd25519Verifier wires an Ed25519RouteReplyVerifier over the given resolver,
// reusing the shared canonical signable-bytes layout and the Ed25519 primitive.
func newEd25519Verifier(t *testing.T, resolver RouteReplyKeyResolver) (*Ed25519RouteReplyVerifier, *security.PacketSigningService) {
	t.Helper()
	signer := security.NewPacketSigningService(constants.MaxPacketAgeSeconds)
	v := NewEd25519RouteReplyVerifier(resolver, signer, security.NewEd25519Service())
	return v, signer
}

// ─── (a) No verifier ⇒ fail-closed reject ────────────────────────────────

func TestRrepVerify_NoVerifier_RejectsRrep_NoRouteInstalled(t *testing.T) {
	sender := NewFakeSender(acceptLocal)
	store := NewInMemoryRouteStore()
	// No verifier argument — the fail-closed default (RejectAll) must apply.
	svc := NewService(sender, store, nil, nil)

	if err := svc.HandleRouteReply(context.Background(), newRrepForVerify()); err != nil {
		t.Fatalf("HandleRouteReply: %v", err)
	}

	r, _ := store.Get(context.Background(), acceptSource)
	if r != nil {
		t.Fatalf("expected RREP rejected — no route installed, got %+v", r)
	}
	if svc.GetCachedRoute(acceptSource) != nil {
		t.Fatalf("expected no cached route")
	}
}

// ─── (b) Ed25519 verifier + correct key + valid signature ⇒ accept ───────

func TestRrepVerify_ValidlySignedRrep_InstallsForwardRoute(t *testing.T) {
	sender := NewFakeSender(acceptLocal)
	store := NewInMemoryRouteStore()

	// The source node's real identity; its public key is registered with the resolver.
	sourceIdentity := newIdentity(t)
	resolver := newStubKeyResolver().add(acceptSource, sourceIdentity.GetPublicKey())

	verifier, signer := newEd25519Verifier(t, resolver)
	defer signer.Close()
	svc := NewService(sender, store, verifier, nil)

	signedRrep := signRrep(t, newRrepForVerify(), sourceIdentity, signer)
	if err := svc.HandleRouteReply(context.Background(), signedRrep); err != nil {
		t.Fatalf("HandleRouteReply: %v", err)
	}

	r, _ := store.Get(context.Background(), acceptSource)
	if r == nil {
		t.Fatalf("expected forward route installed for validly-signed RREP")
	}
	if r.NextHop != acceptSource {
		t.Fatalf("expected next hop %q, got %q", acceptSource, r.NextHop)
	}
}

// ─── (c) Forged (wrong-key) signature ⇒ reject ───────────────────────────

func TestRrepVerify_ForgedRrep_SignedByDifferentKey_IsRejected(t *testing.T) {
	sender := NewFakeSender(acceptLocal)
	store := NewInMemoryRouteStore()

	// Resolver knows the LEGITIMATE source key...
	legitimateSource := newIdentity(t)
	resolver := newStubKeyResolver().add(acceptSource, legitimateSource.GetPublicKey())

	verifier, signer := newEd25519Verifier(t, resolver)
	defer signer.Close()
	svc := NewService(sender, store, verifier, nil)

	// ...but the attacker signs the RREP (claiming to be "carol") with a DIFFERENT key.
	attacker := newIdentity(t)
	forgedRrep := signRrep(t, newRrepForVerify(), attacker, signer)

	if err := svc.HandleRouteReply(context.Background(), forgedRrep); err != nil {
		t.Fatalf("HandleRouteReply: %v", err)
	}

	r, _ := store.Get(context.Background(), acceptSource)
	if r != nil {
		t.Fatalf("expected forged-signature RREP rejected — no route, got %+v", r)
	}
}

// ─── (c) Unsigned RREP ⇒ reject ──────────────────────────────────────────

func TestRrepVerify_UnsignedRrep_IsRejected(t *testing.T) {
	sender := NewFakeSender(acceptLocal)
	store := NewInMemoryRouteStore()

	sourceIdentity := newIdentity(t)
	resolver := newStubKeyResolver().add(acceptSource, sourceIdentity.GetPublicKey())

	verifier, signer := newEd25519Verifier(t, resolver)
	defer signer.Close()
	svc := NewService(sender, store, verifier, nil)

	// RREP with an empty Signature (the MeshPacket default) — must be rejected.
	if err := svc.HandleRouteReply(context.Background(), newRrepForVerify()); err != nil {
		t.Fatalf("HandleRouteReply: %v", err)
	}

	r, _ := store.Get(context.Background(), acceptSource)
	if r != nil {
		t.Fatalf("expected unsigned RREP rejected — no route, got %+v", r)
	}
}

// ─── (c') Unknown signer (resolver returns nil) ⇒ reject ─────────────────

func TestRrepVerify_UnknownSource_IsRejected(t *testing.T) {
	sender := NewFakeSender(acceptLocal)
	store := NewInMemoryRouteStore()

	// Resolver knows nobody — even a validly self-signed RREP is rejected (unknown signer).
	resolver := newStubKeyResolver() // empty
	verifier, signer := newEd25519Verifier(t, resolver)
	defer signer.Close()
	svc := NewService(sender, store, verifier, nil)

	sourceIdentity := newIdentity(t)
	signedRrep := signRrep(t, newRrepForVerify(), sourceIdentity, signer)

	if err := svc.HandleRouteReply(context.Background(), signedRrep); err != nil {
		t.Fatalf("HandleRouteReply: %v", err)
	}

	r, _ := store.Get(context.Background(), acceptSource)
	if r != nil {
		t.Fatalf("expected unknown-signer RREP rejected — no route, got %+v", r)
	}
}
