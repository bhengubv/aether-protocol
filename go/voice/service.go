// SPDX-License-Identifier: MIT

// Package voice implements unicast and group voice-over-mesh for the Aether
// protocol. Point-to-point calls are handled by VoiceCallService; group calls
// (up to constants.MaxGroupVoiceMembers) are handled by GroupVoiceCallService.
//
// Wire formats:
//
//	VoiceSignaling  → JSON (VoiceSignalingMessage)
//	VoiceCall       → binary VoiceFrame
//	GroupVoiceFrame → binary with KeyGeneration field
package voice

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/google/uuid"
	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
)

// ──────────────────────────────────────────────
// JSON signaling types
// ──────────────────────────────────────────────

// VoiceSignalingMessage is the JSON-encoded payload for VoiceSignaling packets.
type VoiceSignalingMessage struct {
	Kind           string   `json:"kind"`
	CallID         string   `json:"call_id"`
	FromUhid       string   `json:"from_uhid"`
	ToUhid         string   `json:"to_uhid"`
	ProposedCodecs []string `json:"proposed_codecs,omitempty"`
	SelectedCodec  string   `json:"selected_codec,omitempty"`
	SampleRateHz   int      `json:"sample_rate_hz,omitempty"`
	Reason         string   `json:"reason,omitempty"`
}

// ──────────────────────────────────────────────
// Session state
// ──────────────────────────────────────────────

// VoiceCallState represents the lifecycle state of a call.
type VoiceCallState int

const (
	VoiceCallStateOffering  VoiceCallState = iota // Offer sent, awaiting answer
	VoiceCallStateRinging                         // Inbound call, awaiting local accept
	VoiceCallStateActive                          // Call in progress
	VoiceCallStateEnded                           // Call ended / hung up
)

// VoiceCallSession holds per-call runtime state.
type VoiceCallSession struct {
	CallID    uuid.UUID
	PeerUhid  string
	State     VoiceCallState
	Sequence  uint32
	CreatedAt time.Time
}

// ──────────────────────────────────────────────
// Service
// ──────────────────────────────────────────────

// VoiceCallService manages unicast voice calls over the mesh.
type VoiceCallService struct {
	sender    routing.MeshSender
	localUhid string
	calls     sync.Map // uuid.UUID (as [16]byte) -> *VoiceCallSession

	// Event callbacks — optional; callers set these before using the service.
	OnCallOffered   func(session *VoiceCallSession)
	OnCallAccepted  func(session *VoiceCallSession)
	OnCallEnded     func(session *VoiceCallSession, reason string)
	OnFrameReceived func(session *VoiceCallSession, frame *VoiceFrame)
}

// VoiceFrame is the decoded form of a binary VoiceCall payload.
type VoiceFrame struct {
	CallID         uuid.UUID
	Sequence       uint32
	TimestampMs    int64
	IsSilence      bool
	EncodedPayload []byte
}

// NewVoiceCallService constructs a VoiceCallService.
func NewVoiceCallService(sender routing.MeshSender) *VoiceCallService {
	if sender == nil {
		panic("voice: sender must not be nil")
	}
	return &VoiceCallService{
		sender:    sender,
		localUhid: sender.LocalUhid(),
	}
}

// SendOffer initiates a call to toUhid. Returns the new call ID.
func (s *VoiceCallService) SendOffer(ctx context.Context, toUhid string, codecs []string, sampleRateHz int) (uuid.UUID, error) {
	if toUhid == "" {
		return uuid.Nil, errors.New("voice: toUhid must not be empty")
	}
	callID := uuid.New()
	session := &VoiceCallSession{
		CallID:    callID,
		PeerUhid:  toUhid,
		State:     VoiceCallStateOffering,
		CreatedAt: time.Now(),
	}
	s.calls.Store(callID, session)

	msg := VoiceSignalingMessage{
		Kind:           "offer",
		CallID:         callID.String(),
		FromUhid:       s.localUhid,
		ToUhid:         toUhid,
		ProposedCodecs: codecs,
		SampleRateHz:   sampleRateHz,
	}
	if err := s.sendSignaling(ctx, toUhid, msg); err != nil {
		s.calls.Delete(callID)
		return uuid.Nil, err
	}
	return callID, nil
}

// AcceptCall sends an "answer" signaling message for an inbound call.
func (s *VoiceCallService) AcceptCall(ctx context.Context, callID uuid.UUID) error {
	session, err := s.getSession(callID)
	if err != nil {
		return err
	}
	if session.State != VoiceCallStateRinging {
		return fmt.Errorf("voice: call %s is not in ringing state", callID)
	}
	session.State = VoiceCallStateActive

	msg := VoiceSignalingMessage{
		Kind:     "answer",
		CallID:   callID.String(),
		FromUhid: s.localUhid,
		ToUhid:   session.PeerUhid,
	}
	return s.sendSignaling(ctx, session.PeerUhid, msg)
}

// HangUp ends a call and notifies the remote peer.
func (s *VoiceCallService) HangUp(ctx context.Context, callID uuid.UUID) error {
	session, err := s.getSession(callID)
	if err != nil {
		return err
	}
	session.State = VoiceCallStateEnded

	msg := VoiceSignalingMessage{
		Kind:     "hangup",
		CallID:   callID.String(),
		FromUhid: s.localUhid,
		ToUhid:   session.PeerUhid,
	}
	if cb := s.OnCallEnded; cb != nil {
		cb(session, "local_hangup")
	}
	s.calls.Delete(callID)
	return s.sendSignaling(ctx, session.PeerUhid, msg)
}

// SendFrame encodes and transmits a single audio frame for an active call.
//
// Binary layout (VoiceFrame):
//
//	[16] CallId        (UUID, big-endian)
//	[4]  Sequence      (uint32, little-endian)
//	[8]  TimestampMs   (int64, little-endian)
//	[1]  IsSilence     (0 or 1)
//	[N]  EncodedPayload
func (s *VoiceCallService) SendFrame(ctx context.Context, callID uuid.UUID, encodedAudio []byte, isSilence bool) error {
	session, err := s.getSession(callID)
	if err != nil {
		return err
	}
	if session.State != VoiceCallStateActive {
		return fmt.Errorf("voice: call %s is not active", callID)
	}

	session.Sequence++
	payload, err := marshalVoiceFrame(callID, session.Sequence, time.Now().UnixMilli(), isSilence, encodedAudio)
	if err != nil {
		return err
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VoiceCall
	pkt.SourceUhid = s.localUhid
	pkt.DestinationUhid = session.PeerUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Priority = 100 // voice traffic elevated
	pkt.Payload = payload

	_, err = s.sender.Send(ctx, pkt, session.PeerUhid)
	return err
}

// HandlePacket processes an inbound VoiceSignaling or VoiceCall packet.
func (s *VoiceCallService) HandlePacket(ctx context.Context, packet *protocol.MeshPacket) error {
	if packet == nil {
		return errors.New("voice: packet must not be nil")
	}
	switch packet.Type {
	case protocol.VoiceSignaling:
		return s.handleSignaling(ctx, packet)
	case protocol.VoiceCall:
		return s.handleFrame(packet)
	default:
		return fmt.Errorf("voice: unexpected packet type %s", packet.Type)
	}
}

// ──────────────────────────────────────────────
// Internal helpers
// ──────────────────────────────────────────────

func (s *VoiceCallService) handleSignaling(_ context.Context, packet *protocol.MeshPacket) error {
	var msg VoiceSignalingMessage
	if err := json.Unmarshal(packet.Payload, &msg); err != nil {
		return fmt.Errorf("voice: unmarshal signaling: %w", err)
	}
	callID, err := uuid.Parse(msg.CallID)
	if err != nil {
		return fmt.Errorf("voice: invalid call_id %q: %w", msg.CallID, err)
	}

	switch msg.Kind {
	case "offer":
		session := &VoiceCallSession{
			CallID:    callID,
			PeerUhid:  packet.SourceUhid,
			State:     VoiceCallStateRinging,
			CreatedAt: time.Now(),
		}
		s.calls.Store(callID, session)
		if cb := s.OnCallOffered; cb != nil {
			cb(session)
		}

	case "answer":
		if session, err := s.getSession(callID); err == nil {
			session.State = VoiceCallStateActive
			if cb := s.OnCallAccepted; cb != nil {
				cb(session)
			}
		}

	case "hangup", "cancel", "timeout":
		if session, err := s.getSession(callID); err == nil {
			session.State = VoiceCallStateEnded
			if cb := s.OnCallEnded; cb != nil {
				cb(session, msg.Kind)
			}
			s.calls.Delete(callID)
		}
	}
	return nil
}

func (s *VoiceCallService) handleFrame(packet *protocol.MeshPacket) error {
	frame, err := unmarshalVoiceFrame(packet.Payload)
	if err != nil {
		return fmt.Errorf("voice: unmarshal frame: %w", err)
	}
	session, err := s.getSession(frame.CallID)
	if err != nil {
		// Unknown call — silently drop.
		return nil
	}
	if cb := s.OnFrameReceived; cb != nil {
		cb(session, frame)
	}
	return nil
}

func (s *VoiceCallService) getSession(callID uuid.UUID) (*VoiceCallSession, error) {
	v, ok := s.calls.Load(callID)
	if !ok {
		return nil, fmt.Errorf("voice: unknown call %s", callID)
	}
	return v.(*VoiceCallSession), nil
}

func (s *VoiceCallService) sendSignaling(ctx context.Context, toUhid string, msg VoiceSignalingMessage) error {
	body, err := json.Marshal(msg)
	if err != nil {
		return fmt.Errorf("voice: marshal signaling: %w", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VoiceSignaling
	pkt.SourceUhid = s.localUhid
	pkt.DestinationUhid = toUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body
	_, err = s.sender.Send(ctx, pkt, toUhid)
	return err
}

// ──────────────────────────────────────────────
// Binary serialisation helpers
// ──────────────────────────────────────────────

// marshalVoiceFrame encodes a VoiceFrame into its canonical binary format.
func marshalVoiceFrame(callID uuid.UUID, seq uint32, tsMs int64, isSilence bool, payload []byte) ([]byte, error) {
	var buf bytes.Buffer
	// [16] CallId — UUID big-endian (RFC4122, same as uuid.UUID bytes)
	buf.Write(callID[:])
	// [4] Sequence — uint32 little-endian
	if err := binary.Write(&buf, binary.LittleEndian, seq); err != nil {
		return nil, err
	}
	// [8] TimestampMs — int64 little-endian
	if err := binary.Write(&buf, binary.LittleEndian, tsMs); err != nil {
		return nil, err
	}
	// [1] IsSilence
	if isSilence {
		buf.WriteByte(1)
	} else {
		buf.WriteByte(0)
	}
	// [N] EncodedPayload
	buf.Write(payload)
	return buf.Bytes(), nil
}

// unmarshalVoiceFrame decodes a binary VoiceFrame payload.
func unmarshalVoiceFrame(data []byte) (*VoiceFrame, error) {
	const fixedSize = 16 + 4 + 8 + 1
	if len(data) < fixedSize {
		return nil, fmt.Errorf("voice: frame too short: %d bytes", len(data))
	}
	r := bytes.NewReader(data)

	var callIDBytes [16]byte
	if _, err := r.Read(callIDBytes[:]); err != nil {
		return nil, err
	}
	callID, err := uuid.FromBytes(callIDBytes[:])
	if err != nil {
		return nil, err
	}

	var seq uint32
	if err := binary.Read(r, binary.LittleEndian, &seq); err != nil {
		return nil, err
	}

	var tsMs int64
	if err := binary.Read(r, binary.LittleEndian, &tsMs); err != nil {
		return nil, err
	}

	silenceByte, err := r.ReadByte()
	if err != nil {
		return nil, err
	}

	encoded := make([]byte, r.Len())
	if _, err := r.Read(encoded); err != nil && r.Len() > 0 {
		return nil, err
	}

	return &VoiceFrame{
		CallID:         callID,
		Sequence:       seq,
		TimestampMs:    tsMs,
		IsSilence:      silenceByte != 0,
		EncodedPayload: encoded,
	}, nil
}
