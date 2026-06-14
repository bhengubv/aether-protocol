// SPDX-License-Identifier: MIT
//
// Default MeshTipService. Sends and receives generic PacketType.TipPacket (24) packets. Go port of
// AetherNet.Security.Services.MeshTipService.
//
// Send path: build a TipPacketPayload → sign the payload's canonical bytes with the local identity
// key (real Ed25519) → serialise as snake_case JSON → wrap in a MeshPacket → sign the enclosing packet
// → route toward the recipient (unicast over a discovered route, falling back to broadcast).
//
// Receive path: deserialise the payload → best-effort signature check (Ed25519 signature must be
// present and well-formed = 64 bytes) → hand to the host's MeshTipSettlementProvider → relay the
// packet onward toward its addressed recipient. A malformed or unverifiable payload is logged and
// dropped, never returned as an error.
//
// This service is purely a protocol mechanism. It attaches NO value semantics to the amount and
// performs NO settlement — settlement is entirely the host's business, expressed through the injected
// provider. A bare node (default no-op provider) accepts and relays tips but settles nothing.
package incentive

import (
	"context"
	"encoding/json"

	"github.com/google/uuid"

	"github.com/bhengubv/aether-protocol/go/protocol"
)

// ed25519SignatureLength is the Ed25519 signature length in bytes — used for the best-effort inbound
// check.
const ed25519SignatureLength = 64

// MeshSender is the minimal mesh transport surface needed by MeshTipService.
type MeshSender interface {
	// LocalUhid returns the UHID of the local node.
	LocalUhid() string
	// Send delivers pkt toward nextHopUhid. Returns (true, nil) on success.
	Send(ctx context.Context, pkt *protocol.MeshPacket, nextHopUhid string) (bool, error)
	// Broadcast sends pkt to every directly-connected peer and returns the fan-out count.
	Broadcast(ctx context.Context, pkt *protocol.MeshPacket) (int, error)
}

// PacketSigner handles signing and verification of the enclosing MeshPacket envelope.
type PacketSigner interface {
	// SignPacket returns a copy of pkt with the Signature/nonce/timestamp fields populated.
	SignPacket(pkt *protocol.MeshPacket) (*protocol.MeshPacket, error)
}

// IdentitySigner signs the tip payload's canonical bytes with the local node's Ed25519 identity key.
type IdentitySigner interface {
	// SignData produces a 64-byte Ed25519 signature over data using the local identity key.
	SignData(data []byte) ([]byte, error)
}

// RouteResolver resolves a next-hop toward a destination UHID. It returns (nextHop, true) when a
// route is known, or ("", false) to fall back to broadcast.
type RouteResolver interface {
	FindNextHop(destinationUhid string) (string, bool)
}

// MeshTipSettlementProvider is the host's settlement hook — the Go analog of the C#
// IAetherNetIncentiveProvider.SettleMeshTip. It receives the full signed TipPacketPayload off the
// mesh and decides how (if at all) to interpret its value. The default no-op settles nothing.
type MeshTipSettlementProvider interface {
	// SettleMeshTip is invoked for every inbound, well-formed tip payload. Implementations
	// (e.g. SDPKT / BhenguPay) wire their wallet settlement here. Returning an error is logged by
	// the caller but never propagated to the wire — a settlement failure must not break relaying.
	SettleMeshTip(ctx context.Context, payload *TipPacketPayload) error
}

// NoopMeshTipSettlementProvider is the default no-op settlement provider — accepts the tip and
// settles nothing. A bare node carries the tip signal but never moves value.
type NoopMeshTipSettlementProvider struct{}

// SettleMeshTip does nothing and returns nil.
func (NoopMeshTipSettlementProvider) SettleMeshTip(ctx context.Context, payload *TipPacketPayload) error {
	return nil
}

// Logger is an optional sink for diagnostic messages.
type Logger interface {
	Printf(format string, args ...interface{})
}

// MeshTipService builds, signs, sends, and handles mesh tip packets.
type MeshTipService struct {
	sender    MeshSender
	signer    PacketSigner
	identity  IdentitySigner
	routing   RouteResolver
	settle    MeshTipSettlementProvider
	logger    Logger
	defaultTtl int32
}

// NewMeshTipService constructs a MeshTipService. Pass nil for settle to receive the default no-op
// settlement provider; pass nil for routing to always broadcast; pass nil for logger to disable
// diagnostics.
func NewMeshTipService(
	sender MeshSender,
	signer PacketSigner,
	identity IdentitySigner,
	routing RouteResolver,
	settle MeshTipSettlementProvider,
) *MeshTipService {
	if settle == nil {
		settle = NoopMeshTipSettlementProvider{}
	}
	return &MeshTipService{
		sender:     sender,
		signer:     signer,
		identity:   identity,
		routing:    routing,
		settle:     settle,
		defaultTtl: 7, // ProtocolConstants.DefaultTtl
	}
}

// WithLogger attaches a logger to the service (call after construction if needed).
func (s *MeshTipService) WithLogger(l Logger) *MeshTipService {
	s.logger = l
	return s
}

func (s *MeshTipService) log(format string, args ...interface{}) {
	if s.logger != nil {
		s.logger.Printf(format, args...)
	}
}

// SendTip builds, signs, and routes a TipPacket(24) addressed to recipientUhid. amount is the
// caller's input verbatim (the invariant decimal string) — the protocol imposes NO policy on it. It
// is signed into the payload and carried as-is. Returns the signed MeshPacket that was routed onto
// the mesh.
func (s *MeshTipService) SendTip(
	ctx context.Context,
	recipientUhid string,
	amount string,
	trafficType string,
	referenceID *uuid.UUID,
	timestampUnixMs int64,
) (*protocol.MeshPacket, error) {
	payload := &TipPacketPayload{
		TipperUhid:      s.sender.LocalUhid(),
		RecipientUhid:   recipientUhid,
		Amount:          amount,
		TrafficType:     trafficType,
		ReferenceID:     referenceID,
		TimestampUnixMs: timestampUnixMs,
	}

	// Sign the payload's canonical bytes with the local identity key (real Ed25519).
	sig, err := s.identity.SignData(payload.BuildCanonicalData())
	if err != nil {
		return nil, err
	}
	payload.Signature = sig

	body, err := payload.ToJSON()
	if err != nil {
		return nil, err
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.TipPacket
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = recipientUhid
	pkt.Ttl = s.defaultTtl
	pkt.Priority = 0
	pkt.Payload = body

	// Sign the enclosing MeshPacket (fills nonce/timestamp + envelope signature).
	signed, err := s.signer.SignPacket(pkt)
	if err != nil {
		return nil, err
	}

	// Route toward the recipient: unicast over a discovered route, else broadcast.
	if s.routing != nil {
		if nextHop, ok := s.routing.FindNextHop(recipientUhid); ok {
			if _, err := s.sender.Send(ctx, signed, nextHop); err != nil {
				return nil, err
			}
			s.log("MeshTip: sent (unicast) to recipient=%s via %s", recipientUhid, nextHop)
			return signed, nil
		}
	}
	if _, err := s.sender.Broadcast(ctx, signed); err != nil {
		return nil, err
	}
	s.log("MeshTip: sent (broadcast) to recipient=%s", recipientUhid)
	return signed, nil
}

// HandleTipPacket processes an inbound TipPacket(24) received off the mesh.
//
// Returns (true, nil) when the payload was accepted and handed to the settlement provider.
// Returns (false, nil) when the packet should be silently discarded (wrong type, malformed payload,
// missing/malformed signature). Returns (false, error) only on an internal send error while relaying.
func (s *MeshTipService) HandleTipPacket(ctx context.Context, packet *protocol.MeshPacket) (bool, error) {
	if packet == nil {
		return false, nil
	}
	if packet.Type != protocol.TipPacket {
		s.log("MeshTip: unexpected packet type %s — ignored", packet.Type)
		return false, nil
	}

	// 1. Deserialise the payload. A malformed payload is logged and dropped.
	var payload TipPacketPayload
	if err := json.Unmarshal(packet.Payload, &payload); err != nil {
		s.log("MeshTip from %s: JSON deserialization failed — dropped: %v", packet.SourceUhid, err)
		return false, nil
	}
	if payload.TipperUhid == "" || payload.RecipientUhid == "" {
		s.log("MeshTip from %s: payload missing required fields — dropped", packet.SourceUhid)
		return false, nil
	}

	// 2. Best-effort signature check: an Ed25519 signature is exactly 64 bytes. A payload carrying
	//    no signature, or a malformed one, is unverifiable — logged and dropped. The host's
	//    settlement provider is responsible for any stronger, key-bound verification it needs.
	if len(payload.Signature) != ed25519SignatureLength {
		s.log("MeshTip from %s: missing or malformed signature — dropped", payload.TipperUhid)
		return false, nil
	}

	// 3. Hand to the host's settlement provider. Default no-op settles nothing. A settlement error
	//    is logged but never breaks relaying.
	if err := s.settle.SettleMeshTip(ctx, &payload); err != nil {
		s.log("MeshTip from %s: settlement provider error: %v", payload.TipperUhid, err)
	}

	// 4. Relay onward toward the addressed recipient if this node is not the destination and the
	//    packet may still be forwarded. The tip is ordinary addressed traffic.
	if packet.DestinationUhid != s.sender.LocalUhid() && packet.CanForward() {
		if s.routing != nil {
			if nextHop, ok := s.routing.FindNextHop(packet.DestinationUhid); ok {
				if _, err := s.sender.Send(ctx, packet, nextHop); err != nil {
					return true, err
				}
				return true, nil
			}
		}
		if _, err := s.sender.Broadcast(ctx, packet); err != nil {
			return true, err
		}
	}

	s.log("MeshTip handled: tipper=%s recipient=%s traffic=%s",
		payload.TipperUhid, payload.RecipientUhid, payload.TrafficType)
	return true, nil
}
