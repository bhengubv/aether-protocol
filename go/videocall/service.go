// SPDX-License-Identifier: MIT

// Package videocall implements video call-control over PacketType.VideoCall
// (PacketType 27) for the Aether mesh — the caller-intent signalling layer
// (ring / accept / decline / hangup) directed between two peers, distinct from
// the media plane (SDP/ICE negotiation + frames) handled separately by the
// streaming VideoCall service. The caller rings a peer (minting a call id); either
// side then accepts, declines, or hangs up. Each control signal is a directed
// VideoCall packet sent to the peer as the next hop; inbound signals surface via
// OnCallStateChanged. Mirrors the C# AetherNet.VideoCallControl.VideoCallControlService.
package videocall

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"time"

	"github.com/google/uuid"
	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
)

// CallStateChanged is surfaced when a video call-control signal arrives from a
// peer. Mirrors the C# VideoCallStateChanged event args.
type CallStateChanged struct {
	// CallId is the id of the call the signal refers to.
	CallId uuid.UUID
	// Action is the control verb received ("ring" / "accept" / "decline" / "hangup").
	Action string
	// FromUhid is the UHID of the peer that sent the signal (the packet source).
	FromUhid string
}

// Service is the default video call-control service. It sends directed VideoCall
// signals (ring/accept/decline/hangup) and surfaces inbound ones via
// OnCallStateChanged.
type Service struct {
	sender routing.MeshSender

	// OnCallStateChanged fires when a call-control signal is received from a peer.
	// Mirrors the C# CallStateChanged event.
	OnCallStateChanged func(evt CallStateChanged)
}

// NewService constructs a Service. Panics if sender is nil.
func NewService(sender routing.MeshSender) *Service {
	if sender == nil {
		panic("videocall: sender must not be nil")
	}
	return &Service{sender: sender}
}

// Ring rings peerUhid: mints a call id and sends a directed "ring". Returns the
// new call id (and any send error). Returns an error if peerUhid is empty.
func (s *Service) Ring(ctx context.Context, peerUhid string) (uuid.UUID, error) {
	if peerUhid == "" {
		return uuid.Nil, errors.New("videocall: peerUhid must not be empty")
	}
	callID := uuid.New()
	if _, err := s.sendControl(ctx, callID, peerUhid, "ring"); err != nil {
		return uuid.Nil, err
	}
	return callID, nil
}

// Accept sends a directed "accept" for callId to peerUhid. Returns delivery success.
func (s *Service) Accept(ctx context.Context, callID uuid.UUID, peerUhid string) (bool, error) {
	return s.sendControl(ctx, callID, peerUhid, "accept")
}

// Decline sends a directed "decline" for callId to peerUhid. Returns delivery success.
func (s *Service) Decline(ctx context.Context, callID uuid.UUID, peerUhid string) (bool, error) {
	return s.sendControl(ctx, callID, peerUhid, "decline")
}

// Hangup sends a directed "hangup" for callId to peerUhid. Returns delivery success.
func (s *Service) Hangup(ctx context.Context, callID uuid.UUID, peerUhid string) (bool, error) {
	return s.sendControl(ctx, callID, peerUhid, "hangup")
}

// sendControl builds and sends a directed VideoCall control packet (dest=peer,
// ttl=constants.DefaultTtl) carrying {call_id, action, sent_at_ms:now}.
func (s *Service) sendControl(ctx context.Context, callID uuid.UUID, peerUhid, action string) (bool, error) {
	if peerUhid == "" {
		return false, errors.New("videocall: peerUhid must not be empty")
	}

	body, err := json.Marshal(videoCallControlWire{
		CallID:   callID,
		Action:   action,
		SentAtMs: time.Now().UnixMilli(),
	})
	if err != nil {
		return false, fmt.Errorf("videocall: marshal payload: %w", err)
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VideoCallPkt
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = peerUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body

	return s.sender.Send(ctx, pkt, peerUhid)
}

// Handle processes an inbound VideoCall packet: parse and fire OnCallStateChanged
// with (call_id, action, fromUhid=packet source). Returns false for the wrong
// packet type or a malformed payload (empty action counts as malformed). Returns
// an error only if the packet is nil.
func (s *Service) Handle(ctx context.Context, packet *protocol.MeshPacket) (bool, error) {
	if packet == nil {
		return false, errors.New("videocall: packet must not be nil")
	}
	if packet.Type != protocol.VideoCallPkt {
		return false, nil
	}

	var body videoCallControlWire
	if err := json.Unmarshal(packet.Payload, &body); err != nil {
		// Malformed payload: log-and-drop, not a caller error (mirrors C#).
		return false, nil
	}
	if body.Action == "" {
		return false, nil
	}

	if cb := s.OnCallStateChanged; cb != nil {
		cb(CallStateChanged{
			CallId:   body.CallID,
			Action:   body.Action,
			FromUhid: packet.SourceUhid,
		})
	}
	return true, nil
}

// videoCallControlWire is the snake_case JSON payload for PacketType.VideoCall
// call-control packets. Wire format: UTF-8 JSON, snake_case keys, field order
// call_id, action, sent_at_ms, no whitespace, lowercase-dashed UUID, sent_at_ms a
// bare integer, action an ASCII verb. This is the byte-identity gate for video
// call-control (fixtures/videocall/vectors.json). CallID is a uuid.UUID so it
// marshals to the canonical lowercase-dashed form across every language port.
// Mirrors the C# VideoCallControlPayload.
type videoCallControlWire struct {
	CallID   uuid.UUID `json:"call_id"`
	Action   string    `json:"action"`
	SentAtMs int64     `json:"sent_at_ms"`
}
