// SPDX-License-Identifier: MIT
//
// Proof-of-Vicinity (PoV) anti-Sybil trust service (single-node, in-memory). Go port of
// AetherNet.Market.IPoVService / InMemoryPoVService. Two users meet physically; their devices exchange
// a signed token over a short-range transport (BLE/NFC/NearLink). Over time a directed trust graph maps
// how many distinct humans have verified a profile.
//
// Signatures are REAL Ed25519 (crypto/ed25519, RFC 8032 — byte-identical to the C# Ed25519SigningService)
// over the canonical token body (BuildSignableTokenData = "SubjectUhid + TimestampTicks + Transport").
// The single-node service holds one identity key and produces both the witness and subject signatures
// with it; the two-party mesh exchange (each side counter-signs with its own key) is PoVTokenExchangeService.
//
// SEPARATION: the resulting PoVScore is a purely local anti-Sybil routing/identity signal — it attaches
// NO value semantics and never touches any money/reward layer.
package market

import (
	"crypto/ed25519"
	"crypto/rand"
	"sync"
	"time"
)

// PoVService is the Proof-of-Vicinity trust service.
type PoVService interface {
	// IssueToken issues a PoV token to subjectUhid (both signatures from this node's identity key).
	IssueToken(witnessUhid, subjectUhid string, transport PoVTransportType) (*PoVToken, error)
	// AcceptToken records an incoming token iff it cryptographically verifies.
	AcceptToken(token *PoVToken) error
	// GetScore returns the current PoV score for a UHID.
	GetScore(uhid string) PoVScore
	// VerifyToken reports whether the token is structurally and cryptographically valid.
	VerifyToken(token *PoVToken) bool
	// ReportDefection reduces the witness's weighted score by 20%.
	ReportDefection(witnessUhid, defectorUhid string)
}

// InMemoryPoVService is a single-node, in-memory PoVService for testing / single-node scenarios.
type InMemoryPoVService struct {
	mu              sync.Mutex
	tokensBySubject map[string][]*PoVToken // SubjectUhid -> tokens vouching for it
	scoreOverrides  map[string]float64     // WitnessUhid -> overridden score (post-defection)

	privateKey ed25519.PrivateKey
	publicKey  ed25519.PublicKey

	// OnTokenReceived fires when a token is issued or accepted.
	OnTokenReceived func(*PoVToken)
}

// NewInMemoryPoVService constructs a service with a fresh self-contained Ed25519 identity.
func NewInMemoryPoVService() (*InMemoryPoVService, error) {
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		return nil, err
	}
	return &InMemoryPoVService{
		tokensBySubject: make(map[string][]*PoVToken),
		scoreOverrides:  make(map[string]float64),
		privateKey:      priv,
		publicKey:       pub,
	}, nil
}

// IssueToken implements PoVService.
func (s *InMemoryPoVService) IssueToken(witnessUhid, subjectUhid string, transport PoVTransportType) (*PoVToken, error) {
	timestampTicks := TimeToTicks(time.Now().UTC())
	signable := BuildSignableTokenData(subjectUhid, timestampTicks, transport)
	// REAL Ed25519 over the canonical body; both signatures from this node's one key (single-node model).
	sig := ed25519.Sign(s.privateKey, signable)

	token := &PoVToken{
		WitnessUhid:      witnessUhid,
		SubjectUhid:      subjectUhid,
		TimestampTicks:   timestampTicks,
		TransportUsed:    transport,
		WitnessSignature: append([]byte(nil), sig...),
		SubjectSignature: append([]byte(nil), sig...),
	}
	if cb := s.OnTokenReceived; cb != nil {
		cb(token)
	}
	return token, nil
}

// AcceptToken implements PoVService — records only a token that cryptographically verifies.
func (s *InMemoryPoVService) AcceptToken(token *PoVToken) error {
	if !s.VerifyToken(token) {
		return nil // silently ignored, mirroring the C# reference
	}
	s.mu.Lock()
	s.tokensBySubject[token.SubjectUhid] = append(s.tokensBySubject[token.SubjectUhid], token)
	s.mu.Unlock()
	if cb := s.OnTokenReceived; cb != nil {
		cb(token)
	}
	return nil
}

// GetScore implements PoVService.
func (s *InMemoryPoVService) GetScore(uhid string) PoVScore {
	s.mu.Lock()
	list := s.tokensBySubject[uhid]
	tokens := make([]*PoVToken, len(list))
	copy(tokens, list)
	override, hasOverride := s.scoreOverrides[uhid]
	s.mu.Unlock()

	if len(tokens) == 0 {
		// A UHID with no inbound tokens still surfaces a stored defection override (defection penalises
		// witness UHIDs, which may not themselves be subjects).
		o := 0.0
		if hasOverride {
			o = override
		}
		return PoVScore{Uhid: uhid, UniqueWitnesses: 0, WeightedScore: o, LastUpdated: time.Now().UTC()}
	}

	witnesses := make(map[string]struct{}, len(tokens))
	for _, t := range tokens {
		witnesses[t.WitnessUhid] = struct{}{}
	}
	unique := len(witnesses)

	// Sigmoid-ish: w / (w + 1).
	score := float64(unique) / (float64(unique) + 1.0)
	if hasOverride {
		score = override
	}
	return PoVScore{Uhid: uhid, UniqueWitnesses: unique, WeightedScore: score, LastUpdated: time.Now().UTC()}
}

// VerifyToken implements PoVService — both signatures must be valid Ed25519 over the canonical body,
// the parties present and distinct.
func (s *InMemoryPoVService) VerifyToken(token *PoVToken) bool {
	if token == nil {
		return false
	}
	// Structural: both parties signed, both UHIDs present, and distinct.
	if len(token.WitnessSignature) == 0 || len(token.SubjectSignature) == 0 ||
		token.WitnessUhid == "" || token.SubjectUhid == "" ||
		token.WitnessUhid == token.SubjectUhid {
		return false
	}
	// Cryptographic: BOTH signatures valid over the canonical body.
	signable := token.SignableData()
	witnessValid := ed25519.Verify(s.publicKey, signable, token.WitnessSignature)
	subjectValid := ed25519.Verify(s.publicKey, signable, token.SubjectSignature)
	return witnessValid && subjectValid
}

// ReportDefection implements PoVService — reduces the witness's weighted score by 20%.
func (s *InMemoryPoVService) ReportDefection(witnessUhid, defectorUhid string) {
	score := s.GetScore(witnessUhid)
	penalised := score.WeightedScore * 0.8
	s.mu.Lock()
	s.scoreOverrides[witnessUhid] = penalised
	s.mu.Unlock()
}
