// SPDX-License-Identifier: MIT
//
// On-mesh Proof-of-Vicinity token exchange — the directed, two-key witness→subject co-presence proof,
// carried over PacketType.PoVTokenExchange (43). Go port of AetherNet.Market.PoVTokenExchangeService.
// Mirrors the AetherNet handler idiom established by MeshTipService (sign payload with the identity
// key → wrap in a signed MeshPacket → send) and ReputationGossipService (verify the enclosing packet
// against the supplied sender public key, which also enforces freshness + nonce replay-dedup).
//
// CRYPTO: signatures are real Ed25519 over the canonical token body (BuildSignableTokenData =
// "SubjectUhid + TimestampTicks + Transport"), byte-identical to every other language implementation,
// so a token exchanged here interoperates on one mesh.
//
// SEPARATION: the resulting PoVScore is a purely local anti-Sybil routing/identity signal. It
// attaches NO value semantics and never touches any money/reward layer.
package market

import (
	"context"
	"encoding/json"
	"sort"
	"sync"
	"time"

	"github.com/bhengubv/aether-protocol/go/protocol"
)

// MeshSender is the minimal mesh transport surface needed by PoVTokenExchangeService.
type MeshSender interface {
	// LocalUhid returns the UHID of the local node.
	LocalUhid() string
	// Send delivers pkt toward subjectUhid (directed — one short-range hop). Returns (true, nil) on
	// success.
	Send(ctx context.Context, pkt *protocol.MeshPacket, subjectUhid string) (bool, error)
}

// PacketSigner signs and verifies the enclosing MeshPacket envelope. VerifyPacket MUST also enforce
// freshness and nonce replay-dedup (mirroring the C# IPacketSigningService), so a replayed or stale
// PoV exchange is rejected here before any crypto on the body.
type PacketSigner interface {
	// SignPacket returns a copy of pkt with the Signature/nonce/timestamp fields populated.
	SignPacket(pkt *protocol.MeshPacket) (*protocol.MeshPacket, error)
	// VerifyPacket verifies pkt's envelope signature against senderPublicKey AND enforces freshness
	// + replay-dedup. Returns (true, nil) only for a fresh, correctly-signed, non-replayed packet.
	VerifyPacket(pkt *protocol.MeshPacket, senderPublicKey []byte) (bool, error)
}

// IdentitySigner signs/verifies canonical token bodies with Ed25519 identity keys.
type IdentitySigner interface {
	// SignData produces a 64-byte Ed25519 signature over data using the local identity key.
	SignData(data []byte) ([]byte, error)
	// VerifySignature verifies sig over data against publicKey.
	VerifySignature(publicKey, data, sig []byte) bool
}

// Logger is an optional sink for diagnostic messages.
type Logger interface {
	Printf(format string, args ...interface{})
}

// PoVTokenExchangeService issues and accepts on-mesh PoV tokens over packet type 43.
type PoVTokenExchangeService struct {
	sender   MeshSender
	signer   PacketSigner
	identity IdentitySigner
	logger   Logger

	mu              sync.Mutex
	tokensBySubject map[string][]*PoVToken

	// OnTokenReceived fires once a counter-signed token has been recorded locally.
	OnTokenReceived func(*PoVToken)
}

// NewPoVTokenExchangeService constructs a PoVTokenExchangeService.
func NewPoVTokenExchangeService(sender MeshSender, signer PacketSigner, identity IdentitySigner) *PoVTokenExchangeService {
	return &PoVTokenExchangeService{
		sender:          sender,
		signer:          signer,
		identity:        identity,
		tokensBySubject: make(map[string][]*PoVToken),
	}
}

// WithLogger attaches a logger to the service (call after construction if needed).
func (s *PoVTokenExchangeService) WithLogger(l Logger) *PoVTokenExchangeService {
	s.logger = l
	return s
}

func (s *PoVTokenExchangeService) log(format string, args ...interface{}) {
	if s.logger != nil {
		s.logger.Printf(format, args...)
	}
}

// IssueToken mints a witness-signed PoV token for subjectUhid and sends it directed (TTL 1) over
// packet 43. It refuses to mint over a non-short-range transport or to vouch for itself. Returns the
// token that was issued (with an empty subject signature — the subject fills it on receipt), or nil
// when issuance was refused.
func (s *PoVTokenExchangeService) IssueToken(ctx context.Context, subjectUhid string, transport PoVTransportType) (*PoVToken, error) {
	if subjectUhid == "" {
		s.log("PoV issue skipped — empty subject UHID")
		return nil, nil
	}

	// ANTI-REMOTE-MINTING: a vicinity proof is only meaningful over a short-range channel.
	if !transport.IsShortRange() {
		s.log("PoV issue refused — transport %s is not short-range", transport)
		return nil, nil
	}

	localUhid := s.sender.LocalUhid()
	if localUhid == "" {
		s.log("PoV issue skipped — local node not initialized")
		return nil, nil
	}

	// A node cannot vouch for itself — that would be a free, unbounded self-attestation.
	if localUhid == subjectUhid {
		s.log("PoV issue refused — witness and subject are the same node")
		return nil, nil
	}

	timestampTicks := TimeToTicks(time.Now().UTC())

	// Witness signs the canonical token body with the node's REAL Ed25519 identity key.
	witnessSig, err := s.identity.SignData(BuildSignableTokenData(subjectUhid, timestampTicks, transport))
	if err != nil {
		return nil, err
	}

	token := &PoVToken{
		WitnessUhid:      localUhid,
		SubjectUhid:      subjectUhid,
		TimestampTicks:   timestampTicks,
		TransportUsed:    transport,
		WitnessSignature: witnessSig,
		SubjectSignature: nil, // filled by the subject when it counter-signs on receipt.
	}

	body, err := token.ToJSON()
	if err != nil {
		return nil, err
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.PoVTokenExchange
	pkt.SourceUhid = localUhid
	pkt.DestinationUhid = subjectUhid // directed — NOT a broadcast.
	pkt.Ttl = 1                       // co-present: the subject is one short-range hop away.
	pkt.Payload = body

	signed, err := s.signer.SignPacket(pkt)
	if err != nil {
		return nil, err
	}

	sent, err := s.sender.Send(ctx, signed, subjectUhid)
	if err != nil {
		return nil, err
	}

	s.log("PoV token issued: witness=%s subject=%s transport=%s sent=%v",
		localUhid, subjectUhid, transport, sent)
	return token, nil
}

// HandleTokenExchange processes an inbound PoV exchange packet (type 43).
//
// Returns (true, nil) when the token was accepted, counter-signed, and recorded.
// Returns (false, nil) when the packet should be silently discarded (wrong type, bad/stale/replayed
// envelope, malformed payload, self-echo, not addressed to us, missing/invalid witness signature,
// witness == subject). Returns (false, error) only on an internal signing error.
func (s *PoVTokenExchangeService) HandleTokenExchange(ctx context.Context, packet *protocol.MeshPacket, senderPublicKey []byte) (bool, error) {
	if packet == nil || senderPublicKey == nil {
		return false, nil
	}
	if packet.Type != protocol.PoVTokenExchange {
		s.log("PoV exchange: unexpected packet type %s — ignored", packet.Type)
		return false, nil
	}

	// 1. Verify the enclosing MeshPacket signature (also enforces freshness + nonce replay-dedup).
	ok, err := s.signer.VerifyPacket(packet, senderPublicKey)
	if err != nil || !ok {
		s.log("PoV exchange from %s: packet signature invalid/stale/replayed — dropped (ok=%v err=%v)",
			packet.SourceUhid, ok, err)
		return false, nil
	}

	// 2. Deserialise the token body.
	var token PoVToken
	if err := json.Unmarshal(packet.Payload, &token); err != nil {
		s.log("PoV exchange from %s: JSON deserialization failed — dropped: %v", packet.SourceUhid, err)
		return false, nil
	}
	if token.WitnessUhid == "" || token.SubjectUhid == "" {
		s.log("PoV exchange from %s: payload missing required fields — dropped", packet.SourceUhid)
		return false, nil
	}

	// 3. The incoming token must already carry the witness's signature.
	if len(token.WitnessSignature) == 0 {
		s.log("PoV exchange from %s: token has no witness signature — dropped", token.WitnessUhid)
		return false, nil
	}

	localUhid := s.sender.LocalUhid()

	// 4. Ignore our own token echoed back to us (witness == us).
	if localUhid != "" && token.WitnessUhid == localUhid {
		return false, nil
	}

	// 5. The token must be addressed to us — we are the subject being vouched for.
	if localUhid != "" && token.SubjectUhid != localUhid {
		s.log("PoV exchange: token subject %s is not us — ignored", token.SubjectUhid)
		return false, nil
	}

	// 6. Verify the WITNESS's Ed25519 signature over the canonical body, against the verified sender
	//    key (the witness is the packet source, so the envelope and the body share a signing key).
	signable := token.SignableData()
	if !s.identity.VerifySignature(senderPublicKey, signable, token.WitnessSignature) {
		s.log("PoV exchange from %s: witness Ed25519 signature invalid — dropped", token.WitnessUhid)
		return false, nil
	}

	// 6b. A witness must not be vouching for itself — distinct parties is a hard PoV invariant.
	if token.WitnessUhid == token.SubjectUhid {
		s.log("PoV exchange from %s: witness == subject — dropped", token.WitnessUhid)
		return false, nil
	}

	// 7. Counter-sign the SAME canonical body as the subject, with our REAL Ed25519 identity key.
	subjectSig, err := s.identity.SignData(signable)
	if err != nil {
		return false, err
	}
	token.SubjectSignature = subjectSig

	// 8. Record it (increments the witness's contribution to OUR score) and notify.
	s.recordToken(&token)
	if cb := s.OnTokenReceived; cb != nil {
		cb(&token)
	}

	s.log("PoV token accepted: witness=%s subject=%s transport=%s",
		token.WitnessUhid, token.SubjectUhid, token.TransportUsed)
	return true, nil
}

// GetScore returns the local PoV trust score for uhid, derived from recorded tokens.
func (s *PoVTokenExchangeService) GetScore(uhid string) PoVScore {
	s.mu.Lock()
	list := s.tokensBySubject[uhid]
	tokens := make([]*PoVToken, len(list))
	copy(tokens, list)
	s.mu.Unlock()

	witnesses := make(map[string]struct{}, len(tokens))
	for _, t := range tokens {
		witnesses[t.WitnessUhid] = struct{}{}
	}
	unique := len(witnesses)

	weighted := 0.0
	if unique > 0 {
		weighted = float64(unique) / (float64(unique) + 1.0)
	}

	return PoVScore{
		Uhid:            uhid,
		UniqueWitnesses: unique,
		WeightedScore:   weighted,
		LastUpdated:     time.Now().UTC(),
	}
}

func (s *PoVTokenExchangeService) recordToken(token *PoVToken) {
	s.mu.Lock()
	s.tokensBySubject[token.SubjectUhid] = append(s.tokensBySubject[token.SubjectUhid], token)
	s.mu.Unlock()
}

// AcceptedSubjects returns the sorted list of subject UHIDs with at least one recorded token. Mainly
// useful for tests and diagnostics.
func (s *PoVTokenExchangeService) AcceptedSubjects() []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]string, 0, len(s.tokensBySubject))
	for k := range s.tokensBySubject {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}
