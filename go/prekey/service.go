// SPDX-License-Identifier: MIT

// Package prekey implements mesh transport of a Signal pre-key bundle over
// PacketType.PreKeyRequest (25) and PacketType.PreKeyResponse (26). It closes the
// "how does a peer get another peer's PreKeyBundle over the mesh" gap the messaging
// layer previously left out-of-band.
//
// A node publishes its current bundle via SetLocalBundle (the host produces it with
// the Signal service's pre-key generation). A peer asks for it with RequestBundle,
// which mints a request id and directed-sends a PreKeyRequest; the responder replies
// with its bundle in a PreKeyResponse; the requester caches the received bundle
// (keyed by the sender's UHID) and fires OnBundleReceived.
//
// This service is the mesh TRANSPORT of bundles only — the host performs the actual
// X3DH by feeding the received bundle to the Signal service (Signal-canonical: no
// key agreement happens here). Directed request/response — never broadcast — so
// bundle requests do not leak identity-interest to the whole mesh. Mirrors the C#
// AetherNet.PreKeys.PreKeyExchangeService.
package prekey

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"sync"

	"github.com/google/uuid"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
	"github.com/bhengubv/aether-protocol/go/security"
)

// BundleReceived is surfaced when a peer's pre-key bundle arrives in a
// PreKeyResponse. Feed Bundle to the Signal service's ProcessPreKeyBundle to run
// X3DH. Mirrors the C# PreKeyBundleReceivedEventArgs.
type BundleReceived struct {
	// RequestId echoed from the original PreKeyRequest (uuid.Nil if unsolicited).
	RequestId uuid.UUID
	// FromUhid is the UHID of the peer that sent the bundle (the packet source).
	FromUhid string
	// Bundle is the received pre-key bundle.
	Bundle security.PreKeyBundle
}

// Service is the default mesh pre-key exchange service. It publishes this node's
// bundle in reply to inbound requests and caches bundles received from peers.
// Directed (not broadcast) to avoid leaking identity-interest to the whole mesh.
type Service struct {
	sender routing.MeshSender

	mu       sync.Mutex
	local    *security.PreKeyBundle
	received map[string]security.PreKeyBundle

	// OnBundleReceived fires when a peer's pre-key bundle arrives in a
	// PreKeyResponse. Mirrors the C# BundleReceived event.
	OnBundleReceived func(evt BundleReceived)
}

// NewService constructs a Service. Panics if sender is nil.
func NewService(sender routing.MeshSender) *Service {
	if sender == nil {
		panic("prekey: sender must not be nil")
	}
	return &Service{
		sender:   sender,
		received: make(map[string]security.PreKeyBundle),
	}
}

// SetLocalBundle sets (or replaces) this node's published bundle — served in reply
// to inbound PreKeyRequests.
func (s *Service) SetLocalBundle(bundle security.PreKeyBundle) {
	s.mu.Lock()
	b := bundle
	s.local = &b
	s.mu.Unlock()
}

// GetLocalBundle returns the currently-published local bundle and true, or the zero
// bundle and false if none has been set.
func (s *Service) GetLocalBundle() (security.PreKeyBundle, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.local == nil {
		return security.PreKeyBundle{}, false
	}
	return *s.local, true
}

// RequestBundle asks peerUhid for its pre-key bundle: mints a request id and sends a
// directed PreKeyRequest (dest peerUhid, TTL constants.DefaultTtl). Returns the new
// request id (echoed by the response) and any send error. Returns an error if
// peerUhid is empty.
func (s *Service) RequestBundle(ctx context.Context, peerUhid string) (uuid.UUID, error) {
	if peerUhid == "" {
		return uuid.Nil, errors.New("prekey: peerUhid must not be empty")
	}

	requestID := uuid.New()
	body, err := json.Marshal(requestWire{
		RequestID:     requestID,
		RequesterUhid: s.sender.LocalUhid(),
	})
	if err != nil {
		return uuid.Nil, fmt.Errorf("prekey: marshal request payload: %w", err)
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.PreKeyRequest
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = peerUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body

	if _, err := s.sender.Send(ctx, pkt, peerUhid); err != nil {
		return uuid.Nil, err
	}
	return requestID, nil
}

// Handle processes an incoming pre-key packet. On PreKeyRequest, it replies with the
// local bundle (if one is set) via a directed PreKeyResponse and returns true; with
// no local bundle set it returns false and sends nothing. On PreKeyResponse, it
// caches the peer bundle and fires OnBundleReceived, returning true. Returns false
// for the wrong packet type, a malformed payload, or a request with no local bundle.
// Returns an error only if the packet is nil.
func (s *Service) Handle(ctx context.Context, packet *protocol.MeshPacket) (bool, error) {
	if packet == nil {
		return false, errors.New("prekey: packet must not be nil")
	}
	switch packet.Type {
	case protocol.PreKeyRequest:
		return s.handleRequest(ctx, packet)
	case protocol.PreKeyResponse:
		return s.handleResponse(packet)
	default:
		return false, nil
	}
}

func (s *Service) handleRequest(ctx context.Context, packet *protocol.MeshPacket) (bool, error) {
	var body requestWire
	if err := json.Unmarshal(packet.Payload, &body); err != nil {
		// Malformed payload: log-and-drop, not a caller error (mirrors C#).
		return false, nil
	}

	local, ok := s.GetLocalBundle()
	if !ok {
		// No local bundle set — nothing to serve; ignore (mirrors C#).
		return false, nil
	}

	replyTo := body.RequesterUhid
	if replyTo == "" {
		replyTo = packet.SourceUhid
	}

	out, err := json.Marshal(responseFromBundle(body.RequestID, local))
	if err != nil {
		return false, fmt.Errorf("prekey: marshal response payload: %w", err)
	}

	reply := protocol.NewMeshPacket()
	reply.Type = protocol.PreKeyResponse
	reply.SourceUhid = s.sender.LocalUhid()
	reply.DestinationUhid = replyTo
	reply.Ttl = constants.DefaultTtl
	reply.Payload = out

	if _, err := s.sender.Send(ctx, reply, replyTo); err != nil {
		return false, err
	}
	return true, nil
}

func (s *Service) handleResponse(packet *protocol.MeshPacket) (bool, error) {
	var body responseWire
	if err := json.Unmarshal(packet.Payload, &body); err != nil {
		// Malformed payload: log-and-drop, not a caller error (mirrors C#).
		return false, nil
	}
	if body.Uhid == "" {
		return false, nil
	}

	bundle := body.toBundle()

	s.mu.Lock()
	s.received[body.Uhid] = bundle
	s.mu.Unlock()

	if cb := s.OnBundleReceived; cb != nil {
		cb(BundleReceived{
			RequestId: body.RequestID,
			FromUhid:  packet.SourceUhid,
			Bundle:    bundle,
		})
	}
	return true, nil
}

// GetReceivedBundle returns the most recently received bundle for uhid and true, or
// the zero bundle and false if none is known.
func (s *Service) GetReceivedBundle(uhid string) (security.PreKeyBundle, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	b, ok := s.received[uhid]
	return b, ok
}

// requestWire is the snake_case JSON payload for PacketType.PreKeyRequest. Wire
// format: UTF-8 JSON, field order request_id, requester_uhid, no whitespace,
// lowercase-dashed UUID. Byte-identity gate: fixtures/prekey/vectors.json. RequestID
// is a uuid.UUID so it marshals to the canonical lowercase-dashed form across every
// language port. Mirrors the C# PreKeyRequestPayload.
type requestWire struct {
	RequestID     uuid.UUID `json:"request_id"`
	RequesterUhid string    `json:"requester_uhid"`
}

// responseWire is the snake_case JSON payload for PacketType.PreKeyResponse. Wire
// format: UTF-8 JSON, field order request_id, uhid, identity_key, identity_key_x25519,
// pre_key_id, pre_key, signed_pre_key_id, signed_pre_key, signed_pre_key_signature;
// no whitespace, lowercase-dashed UUID, integer ids bare, and every byte[] key field
// as STANDARD base64 (RFC 4648, '+/' alphabet, '=' padding — the []byte default in
// both System.Text.Json and Go's encoding/json). Byte-identity gate:
// fixtures/prekey/vectors.json. Mirrors the C# PreKeyResponsePayload.
type responseWire struct {
	RequestID             uuid.UUID `json:"request_id"`
	Uhid                  string    `json:"uhid"`
	IdentityKey           []byte    `json:"identity_key"`
	IdentityKeyX25519     []byte    `json:"identity_key_x25519"`
	PreKeyID              int32     `json:"pre_key_id"`
	PreKey                []byte    `json:"pre_key"`
	SignedPreKeyID        int32     `json:"signed_pre_key_id"`
	SignedPreKey          []byte    `json:"signed_pre_key"`
	SignedPreKeySignature []byte    `json:"signed_pre_key_signature"`
}

// responseFromBundle builds a response payload from a bundle, echoing the
// originating request id. Mirrors the C# PreKeyResponsePayload.FromBundle.
func responseFromBundle(requestID uuid.UUID, b security.PreKeyBundle) responseWire {
	return responseWire{
		RequestID:             requestID,
		Uhid:                  b.Uhid,
		IdentityKey:           b.IdentityKey,
		IdentityKeyX25519:     b.IdentityKeyX25519,
		PreKeyID:              b.PreKeyID,
		PreKey:                b.PreKey,
		SignedPreKeyID:        b.SignedPreKeyID,
		SignedPreKey:          b.SignedPreKey,
		SignedPreKeySignature: b.SignedPreKeySignature,
	}
}

// toBundle projects this wire payload into a security.PreKeyBundle. encoding/json
// has already decoded the []byte-tagged fields from standard base64 by the time this
// runs. Mirrors the C# PreKeyResponsePayload.ToBundle.
func (w responseWire) toBundle() security.PreKeyBundle {
	return security.PreKeyBundle{
		Uhid:                  w.Uhid,
		IdentityKey:           w.IdentityKey,
		IdentityKeyX25519:     w.IdentityKeyX25519,
		PreKeyID:              w.PreKeyID,
		PreKey:                w.PreKey,
		SignedPreKeyID:        w.SignedPreKeyID,
		SignedPreKey:          w.SignedPreKey,
		SignedPreKeySignature: w.SignedPreKeySignature,
	}
}
