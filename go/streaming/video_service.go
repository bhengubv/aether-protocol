// SPDX-License-Identifier: MIT

package streaming

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
	"github.com/thegeeknetwork/aether-protocol-go/constants"
	"github.com/thegeeknetwork/aether-protocol-go/protocol"
	"github.com/thegeeknetwork/aether-protocol-go/routing"
)

// ──────────────────────────────────────────────
// JSON signaling types
// ──────────────────────────────────────────────

// VideoSignalingMessage is the JSON-encoded payload for VideoSignaling packets.
type VideoSignalingMessage struct {
	Kind           string   `json:"kind"`
	CallID         string   `json:"call_id"`
	FromUhid       string   `json:"from_uhid"`
	ToUhid         string   `json:"to_uhid"`
	ProposedCodecs []string `json:"proposed_codecs,omitempty"`
	SelectedCodec  string   `json:"selected_codec,omitempty"`
	Width          int      `json:"width,omitempty"`
	Height         int      `json:"height,omitempty"`
	FPS            int      `json:"fps,omitempty"`
	BitrateKbps    int      `json:"bitrate_kbps,omitempty"`
	Reason         string   `json:"reason,omitempty"`
}

// ──────────────────────────────────────────────
// Session state
// ──────────────────────────────────────────────

// VideoCallState mirrors the voice call state machine for video.
type VideoCallState int

const (
	VideoCallStateOffering VideoCallState = iota
	VideoCallStateRinging
	VideoCallStateActive
	VideoCallStateEnded
)

// VideoCallSession holds per-call runtime state.
type VideoCallSession struct {
	CallID    uuid.UUID
	PeerUhid  string
	State     VideoCallState
	Sequence  uint32
	CreatedAt time.Time
}

// ──────────────────────────────────────────────
// Service
// ──────────────────────────────────────────────

// VideoCallService manages unicast video calls over the mesh.
type VideoCallService struct {
	sender    routing.MeshSender
	localUhid string
	calls     sync.Map // uuid.UUID -> *VideoCallSession

	// Event callbacks — optional.
	OnCallOffered      func(session *VideoCallSession)
	OnCallAccepted     func(session *VideoCallSession)
	OnCallEnded        func(session *VideoCallSession, reason string)
	OnFrameReceived    func(session *VideoCallSession, frame *VideoFrameData)
	OnKeyframeRequired func(session *VideoCallSession)
	OnQualityChanged   func(session *VideoCallSession, msg *VideoSignalingMessage)
}

// VideoFrameData is the decoded form of a binary VideoFrame payload.
type VideoFrameData struct {
	CallID         uuid.UUID
	Sequence       uint32
	TimestampMs    int64
	IsKeyframe     bool
	EncodedPayload []byte
}

// NewVideoCallService constructs a VideoCallService.
func NewVideoCallService(sender routing.MeshSender) *VideoCallService {
	if sender == nil {
		panic("streaming: video sender must not be nil")
	}
	return &VideoCallService{
		sender:    sender,
		localUhid: sender.LocalUhid(),
	}
}

// SendOffer initiates a video call to toUhid. Returns the new call ID.
func (s *VideoCallService) SendOffer(ctx context.Context, toUhid string, codecs []string, width, height, fps, bitrateKbps int) (uuid.UUID, error) {
	if toUhid == "" {
		return uuid.Nil, errors.New("streaming: toUhid must not be empty")
	}
	callID := uuid.New()
	session := &VideoCallSession{
		CallID:    callID,
		PeerUhid:  toUhid,
		State:     VideoCallStateOffering,
		CreatedAt: time.Now(),
	}
	s.calls.Store(callID, session)

	msg := VideoSignalingMessage{
		Kind:           "offer",
		CallID:         callID.String(),
		FromUhid:       s.localUhid,
		ToUhid:         toUhid,
		ProposedCodecs: codecs,
		Width:          width,
		Height:         height,
		FPS:            fps,
		BitrateKbps:    bitrateKbps,
	}
	if err := s.sendSignaling(ctx, toUhid, msg); err != nil {
		s.calls.Delete(callID)
		return uuid.Nil, err
	}
	return callID, nil
}

// AcceptCall sends an "answer" signaling message for an inbound video call.
func (s *VideoCallService) AcceptCall(ctx context.Context, callID uuid.UUID) error {
	session, err := s.getVideoSession(callID)
	if err != nil {
		return err
	}
	if session.State != VideoCallStateRinging {
		return fmt.Errorf("streaming: video call %s is not in ringing state", callID)
	}
	session.State = VideoCallStateActive

	msg := VideoSignalingMessage{
		Kind:     "answer",
		CallID:   callID.String(),
		FromUhid: s.localUhid,
		ToUhid:   session.PeerUhid,
	}
	return s.sendSignaling(ctx, session.PeerUhid, msg)
}

// HangUp ends a video call and notifies the peer.
func (s *VideoCallService) HangUp(ctx context.Context, callID uuid.UUID) error {
	session, err := s.getVideoSession(callID)
	if err != nil {
		return err
	}
	session.State = VideoCallStateEnded

	msg := VideoSignalingMessage{
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

// SendFrame encodes and transmits a single video frame for an active call.
//
// Binary layout (VideoFrame):
//
//	[16] CallId       (UUID, big-endian)
//	[4]  Sequence     (uint32, little-endian)
//	[8]  TimestampMs  (int64, little-endian)
//	[1]  IsKeyframe   (0 or 1)
//	[N]  EncodedPayload
func (s *VideoCallService) SendFrame(ctx context.Context, callID uuid.UUID, encodedVideo []byte, isKeyframe bool) error {
	session, err := s.getVideoSession(callID)
	if err != nil {
		return err
	}
	if session.State != VideoCallStateActive {
		return fmt.Errorf("streaming: video call %s is not active", callID)
	}

	session.Sequence++
	payload, err := marshalVideoFrame(callID, session.Sequence, time.Now().UnixMilli(), isKeyframe, encodedVideo)
	if err != nil {
		return err
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VideoFrame
	pkt.SourceUhid = s.localUhid
	pkt.DestinationUhid = session.PeerUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Priority = 90 // video slightly below voice SOS
	pkt.Payload = payload

	_, err = s.sender.Send(ctx, pkt, session.PeerUhid)
	return err
}

// RequestKeyframe sends a "keyframe_request" signaling message to the peer.
func (s *VideoCallService) RequestKeyframe(ctx context.Context, callID uuid.UUID) error {
	session, err := s.getVideoSession(callID)
	if err != nil {
		return err
	}
	msg := VideoSignalingMessage{
		Kind:     "keyframe_request",
		CallID:   callID.String(),
		FromUhid: s.localUhid,
		ToUhid:   session.PeerUhid,
	}
	return s.sendSignaling(ctx, session.PeerUhid, msg)
}

// NotifyQualityChange sends a "quality_change" signaling message to inform the
// peer of the desired encoding parameters.
func (s *VideoCallService) NotifyQualityChange(ctx context.Context, callID uuid.UUID, width, height, fps, bitrateKbps int) error {
	session, err := s.getVideoSession(callID)
	if err != nil {
		return err
	}
	msg := VideoSignalingMessage{
		Kind:        "quality_change",
		CallID:      callID.String(),
		FromUhid:    s.localUhid,
		ToUhid:      session.PeerUhid,
		Width:       width,
		Height:      height,
		FPS:         fps,
		BitrateKbps: bitrateKbps,
	}
	return s.sendSignaling(ctx, session.PeerUhid, msg)
}

// HandlePacket processes inbound VideoSignaling or VideoFrame packets.
func (s *VideoCallService) HandlePacket(ctx context.Context, packet *protocol.MeshPacket) error {
	if packet == nil {
		return errors.New("streaming: video packet must not be nil")
	}
	switch packet.Type {
	case protocol.VideoSignaling:
		return s.handleVideoSignaling(ctx, packet)
	case protocol.VideoFrame:
		return s.handleVideoFrame(packet)
	default:
		return fmt.Errorf("streaming: unexpected video packet type %s", packet.Type)
	}
}

// ──────────────────────────────────────────────
// Internal helpers
// ──────────────────────────────────────────────

func (s *VideoCallService) handleVideoSignaling(_ context.Context, packet *protocol.MeshPacket) error {
	var msg VideoSignalingMessage
	if err := json.Unmarshal(packet.Payload, &msg); err != nil {
		return fmt.Errorf("streaming: unmarshal video signaling: %w", err)
	}
	callID, err := uuid.Parse(msg.CallID)
	if err != nil {
		return fmt.Errorf("streaming: invalid call_id %q: %w", msg.CallID, err)
	}

	switch msg.Kind {
	case "offer":
		session := &VideoCallSession{
			CallID:    callID,
			PeerUhid:  packet.SourceUhid,
			State:     VideoCallStateRinging,
			CreatedAt: time.Now(),
		}
		s.calls.Store(callID, session)
		if cb := s.OnCallOffered; cb != nil {
			cb(session)
		}

	case "answer":
		if session, err := s.getVideoSession(callID); err == nil {
			session.State = VideoCallStateActive
			if cb := s.OnCallAccepted; cb != nil {
				cb(session)
			}
		}

	case "hangup", "cancel", "timeout":
		if session, err := s.getVideoSession(callID); err == nil {
			session.State = VideoCallStateEnded
			if cb := s.OnCallEnded; cb != nil {
				cb(session, msg.Kind)
			}
			s.calls.Delete(callID)
		}

	case "keyframe_request":
		if session, err := s.getVideoSession(callID); err == nil {
			if cb := s.OnKeyframeRequired; cb != nil {
				cb(session)
			}
		}

	case "quality_change":
		if session, err := s.getVideoSession(callID); err == nil {
			if cb := s.OnQualityChanged; cb != nil {
				cb(session, &msg)
			}
		}
	}
	return nil
}

func (s *VideoCallService) handleVideoFrame(packet *protocol.MeshPacket) error {
	frame, err := unmarshalVideoFrame(packet.Payload)
	if err != nil {
		return fmt.Errorf("streaming: unmarshal video frame: %w", err)
	}
	session, err := s.getVideoSession(frame.CallID)
	if err != nil {
		return nil // unknown call — drop
	}
	if cb := s.OnFrameReceived; cb != nil {
		cb(session, frame)
	}
	return nil
}

func (s *VideoCallService) getVideoSession(callID uuid.UUID) (*VideoCallSession, error) {
	v, ok := s.calls.Load(callID)
	if !ok {
		return nil, fmt.Errorf("streaming: unknown video call %s", callID)
	}
	return v.(*VideoCallSession), nil
}

func (s *VideoCallService) sendSignaling(ctx context.Context, toUhid string, msg VideoSignalingMessage) error {
	body, err := json.Marshal(msg)
	if err != nil {
		return fmt.Errorf("streaming: marshal video signaling: %w", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VideoSignaling
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

// marshalVideoFrame encodes a VideoFrame into its canonical binary format.
func marshalVideoFrame(callID uuid.UUID, seq uint32, tsMs int64, isKeyframe bool, payload []byte) ([]byte, error) {
	var buf bytes.Buffer
	buf.Write(callID[:])
	if err := binary.Write(&buf, binary.LittleEndian, seq); err != nil {
		return nil, err
	}
	if err := binary.Write(&buf, binary.LittleEndian, tsMs); err != nil {
		return nil, err
	}
	if isKeyframe {
		buf.WriteByte(1)
	} else {
		buf.WriteByte(0)
	}
	buf.Write(payload)
	return buf.Bytes(), nil
}

// unmarshalVideoFrame decodes a binary VideoFrame payload.
func unmarshalVideoFrame(data []byte) (*VideoFrameData, error) {
	const fixedSize = 16 + 4 + 8 + 1
	if len(data) < fixedSize {
		return nil, fmt.Errorf("streaming: video frame too short: %d bytes", len(data))
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

	kfByte, err := r.ReadByte()
	if err != nil {
		return nil, err
	}

	encoded := make([]byte, r.Len())
	if _, err := r.Read(encoded); err != nil && r.Len() > 0 {
		return nil, err
	}

	return &VideoFrameData{
		CallID:         callID,
		Sequence:       seq,
		TimestampMs:    tsMs,
		IsKeyframe:     kfByte != 0,
		EncodedPayload: encoded,
	}, nil
}
