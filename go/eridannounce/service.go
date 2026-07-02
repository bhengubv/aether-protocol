// SPDX-License-Identifier: MIT

// Package eridannounce binds PacketType.EridAnnounce (56) to the Aether mesh: a node
// shares its rotating-address routing key with an established peer by sending the
// (already Signal-encrypted) ERID directory announcement directly. Transport only — the
// plaintext framing (identity.EncodeEridAnnouncement) and the encryption are done by the
// host; this service just carries the opaque encrypted blob as a directed packet and
// surfaces inbound ones via OnAnnounceReceived. Lives in its own package rather than
// go/identity because a directed transport must import go/routing, and go/routing already
// depends (transitively via go/models) on go/identity — putting it in go/identity would
// form an import cycle. Mirrors the C# AetherNet.Identity.EridAnnounceService.
package eridannounce

import (
	"context"
	"errors"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
)

// Service carries encrypted ERID announcements as directed EridAnnounce (56) packets and
// surfaces inbound ones via OnAnnounceReceived. Mirrors the C# EridAnnounceService.
type Service struct {
	sender routing.MeshSender

	// OnAnnounceReceived fires when an ERID announcement arrives from a peer. The
	// first argument is the still-encrypted announcement body; the second is the
	// enclosing packet's SourceUhid. Mirrors the C# AnnounceReceived event.
	OnAnnounceReceived func(encryptedAnnouncement []byte, fromUhid string)
}

// NewService constructs a Service. Panics if sender is nil.
func NewService(sender routing.MeshSender) *Service {
	if sender == nil {
		panic("eridannounce: sender must not be nil")
	}
	return &Service{sender: sender}
}

// SendAnnounce sends an encrypted ERID announcement directly to peerUhid. The bytes are
// carried opaquely as the packet payload — this transport never inspects them. Returns
// delivery success. Returns an error for an empty peerUhid or empty announcement.
// Mirrors the C# SendAnnounceAsync.
func (s *Service) SendAnnounce(ctx context.Context, peerUhid string, encryptedAnnouncement []byte) (bool, error) {
	if peerUhid == "" {
		return false, errors.New("eridannounce: peerUhid cannot be empty")
	}
	if len(encryptedAnnouncement) == 0 {
		return false, errors.New("eridannounce: encryptedAnnouncement cannot be empty")
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.EridAnnounce
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = peerUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = encryptedAnnouncement

	return s.sender.Send(ctx, pkt, peerUhid)
}

// Handle processes an inbound PacketType.EridAnnounce: fire OnAnnounceReceived with the
// opaque body. Returns false (no error) for the wrong packet type or an empty payload.
// Returns an error only if the packet is nil. Mirrors the C# HandleAsync.
func (s *Service) Handle(ctx context.Context, packet *protocol.MeshPacket) (bool, error) {
	if packet == nil {
		return false, errors.New("eridannounce: packet must not be nil")
	}
	if packet.Type != protocol.EridAnnounce {
		return false, nil
	}
	if len(packet.Payload) == 0 {
		return false, nil
	}

	if cb := s.OnAnnounceReceived; cb != nil {
		cb(packet.Payload, packet.SourceUhid)
	}
	return true, nil
}
