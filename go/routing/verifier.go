// SPDX-License-Identifier: MIT

package routing

import (
	"context"

	"github.com/bhengubv/aether-protocol/go/protocol"
)

// RouteReplyVerifier verifies that a received RREP was actually signed by the
// node it claims to come from.
//
// Threat — RREP hijack. AODV-style reactive routing installs a forward route
// straight from an RREP's SourceUhid. Any intermediate forwarder that sees a
// route-request flood can fabricate an RREP claiming to be the destination,
// poison every hop's route table, and pull the victim's traffic onto itself
// (blackhole / man-in-the-middle). The only defence is to require a valid
// source signature on the RREP before trusting it.
//
// Fail-closed by default. The routing Service now falls back to
// RejectAllRouteReplyVerifier when no verifier is supplied: an absent or
// partial configuration must never silently trust unverified route replies. A
// host that ships a real implementation (typically Ed25519RouteReplyVerifier,
// backed by the security package's signature primitive) opts in to actually
// validating signatures; until it does, no RREP is accepted and no forward
// route is installed.
type RouteReplyVerifier interface {
	// Verify returns true only if rrep is proven authentic (validly signed by
	// the node it claims to originate from).
	Verify(ctx context.Context, rrep *protocol.MeshPacket) (bool, error)
}

// RejectAllRouteReplyVerifier is the fail-closed verifier: every RREP is
// REJECTED. This is the safe default the routing Service falls back to when no
// verifier is supplied — an unverified route reply is never trusted, so the
// RREP-hijack attack surface is closed until a host wires a real signature
// verifier. Route discovery for peers that would otherwise reply legitimately
// will simply not complete under this verifier; that is intentional
// (correctness over availability for an unconfigured node).
type RejectAllRouteReplyVerifier struct{}

// Verify always returns (false, nil): no RREP is trusted.
func (RejectAllRouteReplyVerifier) Verify(ctx context.Context, rrep *protocol.MeshPacket) (bool, error) {
	return false, nil
}

// AcceptAllRouteReplyVerifier is INSECURE: it accepts every RREP without any
// signature check. This is an explicit opt-in escape hatch for unit tests that
// exercise routing mechanics (forwarding, caching, TTL) and for trust-the-fabric
// demos on a closed, fully-trusted network. It provides NO protection against
// RREP hijack and MUST NOT be used in production or on any open mesh — a single
// malicious forwarder can blackhole traffic. It is deliberately NOT the default:
// callers have to reach for it by name so the choice to disable verification is
// visible in the code.
type AcceptAllRouteReplyVerifier struct{}

// Verify always returns (true, nil): every RREP is accepted unchecked.
func (AcceptAllRouteReplyVerifier) Verify(ctx context.Context, rrep *protocol.MeshPacket) (bool, error) {
	return true, nil
}

// RouteReplyKeyResolver resolves the Ed25519 public key of a node given its
// source UHID, so an RREP's signature can be checked against the identity it
// claims. It returns nil when the UHID is unknown — the verifier treats an
// unresolvable signer as untrusted and rejects the RREP (fail-closed: an unknown
// key can never produce a valid signature we would accept).
//
// No shared peer-key directory exists in the protocol today — callers that verify
// packets (reputation gossip, PoV token exchange) pass the sender public key in
// explicitly. This minimal resolver abstracts "UHID -> public key" for the
// routing layer so a host can plug in whatever key source it already maintains
// (handshake-established keys, a published identity directory, a prekey/identity
// store, etc.) without the routing layer depending on any one of them.
type RouteReplyKeyResolver interface {
	// ResolvePublicKey returns the Ed25519 public key registered for sourceUhid,
	// or nil if the node is unknown. A nil result causes the RREP to be rejected.
	ResolvePublicKey(sourceUhid string) []byte
}

// signableDataComputer builds the canonical signable byte sequence for a packet
// from its fields. It is satisfied by *security.PacketSigningService — the SAME
// layout the source signed and every other language implementation shares
// (Nonce || TimestampMs || Type || SourceLen || Source || DestLen || Dest ||
// SHA256(Payload) || Ttl || Priority). The verifier depends on this interface
// (not the concrete type) so it reuses the existing canonical layout without
// re-implementing it or taking ownership of the signing service's lifecycle.
type signableDataComputer interface {
	ComputeSignableData(
		nonce []byte,
		timestampMs int64,
		packetType byte,
		sourceUhid string,
		destUhid string,
		payload []byte,
		ttl int32,
		priority byte,
	) []byte
}

// ed25519Verifier is the Ed25519 signature-verification primitive. It is
// satisfied by *security.Ed25519Service (Verify) directly; a host that already
// holds a SignalProtocolService can adapt its VerifySignature method with a
// one-line shim. The signature matches the C# ISignalProtocolService.VerifySignature.
type ed25519Verifier interface {
	Verify(publicKey, data, signature []byte) bool
}

// Ed25519RouteReplyVerifier is the production RouteReplyVerifier: it accepts an
// RREP only if the packet carries a valid Ed25519 signature produced by the node
// it claims to originate from.
//
// This closes the RREP-hijack hole. A forward route is installed straight from an
// RREP's SourceUhid; without a signature check, any intermediate forwarder can
// forge an RREP for the destination and blackhole / man-in-the-middle the
// victim's traffic. Here we resolve the claimed source's public key and verify
// the signature over the exact same canonical bytes the source signed (via the
// shared ComputeSignableData layout), so a forged or unsigned RREP fails and no
// route is installed.
//
// Fail-closed at every branch: a missing signature, an unresolvable / unknown
// source key, or a signature that does not verify all return (false, nil). Only a
// signature that validates against a known key is accepted.
//
// Replay / freshness (nonce dedup, timestamp window) is NOT duplicated here —
// that is already enforced by the packet-ingest pipeline (PacketSigningService).
// This verifier is purely the source-identity gate the routing layer needs
// before trusting a route reply.
type Ed25519RouteReplyVerifier struct {
	resolver RouteReplyKeyResolver
	signable signableDataComputer
	verifier ed25519Verifier
}

// NewEd25519RouteReplyVerifier constructs the production verifier.
//
//   - resolver maps an RREP source UHID to its Ed25519 public key; a nil result
//     (unknown signer) causes the RREP to be rejected.
//   - signable computes the canonical signable bytes — pass the host's
//     *security.PacketSigningService so the exact same layout the source signed
//     is reused (do NOT re-implement the layout).
//   - verifier provides the Ed25519 Verify primitive — pass *security.Ed25519Service.
//
// Panics if any dependency is nil: a verifier that cannot resolve keys, build
// signable data, or verify signatures could not fail closed correctly, so
// mis-wiring must surface loudly at construction rather than silently accept or
// silently reject at runtime.
func NewEd25519RouteReplyVerifier(
	resolver RouteReplyKeyResolver,
	signable signableDataComputer,
	verifier ed25519Verifier,
) *Ed25519RouteReplyVerifier {
	if resolver == nil {
		panic("routing: Ed25519RouteReplyVerifier resolver must not be nil")
	}
	if signable == nil {
		panic("routing: Ed25519RouteReplyVerifier signable computer must not be nil")
	}
	if verifier == nil {
		panic("routing: Ed25519RouteReplyVerifier verifier must not be nil")
	}
	return &Ed25519RouteReplyVerifier{resolver: resolver, signable: signable, verifier: verifier}
}

// Verify returns true only when rrep carries a valid Ed25519 signature from the
// node named by its SourceUhid. Every failure path returns (false, nil) — this
// verifier fails closed.
func (v *Ed25519RouteReplyVerifier) Verify(ctx context.Context, rrep *protocol.MeshPacket) (bool, error) {
	if rrep == nil {
		return false, nil
	}

	// No signature -> cannot be trusted. (MeshPacket.Signature is nil by default.)
	if len(rrep.Signature) == 0 {
		return false, nil
	}

	// Resolve the claimed source's public key. Unknown signer -> reject
	// (fail-closed): an unresolvable key can never produce a signature we accept.
	publicKey := v.resolver.ResolvePublicKey(rrep.SourceUhid)
	if len(publicKey) == 0 {
		return false, nil
	}

	// Verify the Ed25519 signature over the canonical signable bytes — the SAME
	// layout the source signed and every other language implementation shares.
	signableData := v.signable.ComputeSignableData(
		rrep.PacketNonce,
		rrep.TimestampMs,
		byte(rrep.Type),
		rrep.SourceUhid,
		rrep.DestinationUhid,
		rrep.Payload,
		rrep.Ttl,
		rrep.Priority,
	)

	return v.verifier.Verify(publicKey, signableData, rrep.Signature), nil
}
