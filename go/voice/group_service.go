// SPDX-License-Identifier: MIT

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

// GroupVoiceSignalingMessage is the JSON-encoded payload for group voice
// signaling packets (VoiceSignaling with group semantics).
type GroupVoiceSignalingMessage struct {
	Kind          string   `json:"kind"`
	CallID        string   `json:"call_id"`
	FromUhid      string   `json:"from_uhid"`
	ToUhid        string   `json:"to_uhid"`
	InvitedUhids  []string `json:"invited_uhids,omitempty"`
	KickedUhid    string   `json:"kicked_uhid,omitempty"`
	KeyGeneration uint32   `json:"key_generation,omitempty"`
}

// ──────────────────────────────────────────────
// Session state
// ──────────────────────────────────────────────

// GroupVoiceCallSession holds per-call runtime state for a group call.
type GroupVoiceCallSession struct {
	CallID        uuid.UUID
	HostUhid      string
	Members       map[string]struct{} // uhid -> present
	State         VoiceCallState
	KeyGeneration uint32
	Sequence      uint32
	mu            sync.Mutex
	CreatedAt     time.Time
}

// ──────────────────────────────────────────────
// Service
// ──────────────────────────────────────────────

// GroupVoiceCallService manages group voice calls over the mesh.
// Up to constants.MaxGroupVoiceMembers participants are supported.
type GroupVoiceCallService struct {
	sender    routing.MeshSender
	localUhid string
	calls     sync.Map // uuid.UUID -> *GroupVoiceCallSession

	// Event callbacks — optional.
	OnInviteReceived    func(session *GroupVoiceCallSession, fromUhid string)
	OnMemberJoined      func(session *GroupVoiceCallSession, uhid string)
	OnMemberLeft        func(session *GroupVoiceCallSession, uhid string)
	OnMemberKicked      func(session *GroupVoiceCallSession, uhid string)
	OnCallEnded         func(session *GroupVoiceCallSession)
	OnKeyRotated        func(session *GroupVoiceCallSession)
	OnFrameReceived     func(session *GroupVoiceCallSession, frame *GroupVoiceFrame)
}

// GroupVoiceFrame is the decoded form of a binary group voice payload.
type GroupVoiceFrame struct {
	CallID         uuid.UUID
	Sequence       uint32
	TimestampMs    int64
	IsSilence      bool
	KeyGeneration  uint32
	EncodedPayload []byte
}

// NewGroupVoiceCallService constructs a GroupVoiceCallService.
func NewGroupVoiceCallService(sender routing.MeshSender) *GroupVoiceCallService {
	if sender == nil {
		panic("voice: group sender must not be nil")
	}
	return &GroupVoiceCallService{
		sender:    sender,
		localUhid: sender.LocalUhid(),
	}
}

// Invite sends group call invitations to the listed member UHIDs. The caller
// becomes host. Returns the new call ID.
func (s *GroupVoiceCallService) Invite(ctx context.Context, callID uuid.UUID, memberUhids []string) error {
	if len(memberUhids) == 0 {
		return errors.New("voice: memberUhids must not be empty")
	}
	if int32(len(memberUhids)) > constants.MaxGroupVoiceMembers {
		return fmt.Errorf("voice: exceeds MaxGroupVoiceMembers (%d)", constants.MaxGroupVoiceMembers)
	}

	session := s.getOrCreateSession(callID, s.localUhid)
	session.mu.Lock()
	for _, m := range memberUhids {
		session.Members[m] = struct{}{}
	}
	session.mu.Unlock()

	msg := GroupVoiceSignalingMessage{
		Kind:         "invite",
		CallID:       callID.String(),
		FromUhid:     s.localUhid,
		InvitedUhids: memberUhids,
	}
	for _, m := range memberUhids {
		msg.ToUhid = m
		if err := s.sendGroupSignaling(ctx, m, msg); err != nil {
			return err
		}
	}
	return nil
}

// Join sends a "join" signal to the host / all current members.
func (s *GroupVoiceCallService) Join(ctx context.Context, callID uuid.UUID) error {
	session, err := s.getGroupSession(callID)
	if err != nil {
		return err
	}
	session.mu.Lock()
	session.State = VoiceCallStateActive
	members := s.memberSnapshot(session)
	session.mu.Unlock()

	msg := GroupVoiceSignalingMessage{
		Kind:     "join",
		CallID:   callID.String(),
		FromUhid: s.localUhid,
	}
	return s.broadcastGroupSignaling(ctx, members, msg)
}

// Leave exits a group call and notifies the remaining members.
func (s *GroupVoiceCallService) Leave(ctx context.Context, callID uuid.UUID) error {
	session, err := s.getGroupSession(callID)
	if err != nil {
		return err
	}
	session.mu.Lock()
	session.State = VoiceCallStateEnded
	delete(session.Members, s.localUhid)
	members := s.memberSnapshot(session)
	session.mu.Unlock()

	msg := GroupVoiceSignalingMessage{
		Kind:     "leave",
		CallID:   callID.String(),
		FromUhid: s.localUhid,
	}
	s.calls.Delete(callID)
	return s.broadcastGroupSignaling(ctx, members, msg)
}

// Kick removes a member from the call (host-only by convention — enforcement is
// application-layer; the service sends the signal unconditionally).
func (s *GroupVoiceCallService) Kick(ctx context.Context, callID uuid.UUID, targetUhid string) error {
	if targetUhid == "" {
		return errors.New("voice: targetUhid must not be empty")
	}
	session, err := s.getGroupSession(callID)
	if err != nil {
		return err
	}
	session.mu.Lock()
	delete(session.Members, targetUhid)
	members := s.memberSnapshot(session)
	session.mu.Unlock()

	msg := GroupVoiceSignalingMessage{
		Kind:       "kick",
		CallID:     callID.String(),
		FromUhid:   s.localUhid,
		ToUhid:     targetUhid,
		KickedUhid: targetUhid,
	}
	// Notify the kicked member directly.
	if err := s.sendGroupSignaling(ctx, targetUhid, msg); err != nil {
		return err
	}
	// Notify remaining members.
	return s.broadcastGroupSignaling(ctx, members, msg)
}

// SendFrame encodes and transmits a group voice frame to all current members.
//
// Binary layout (GroupVoiceFrame):
//
//	[16] CallId         (UUID, big-endian)
//	[4]  Sequence       (uint32, little-endian)
//	[8]  TimestampMs    (int64, little-endian)
//	[1]  IsSilence      (0 or 1)
//	[4]  KeyGeneration  (uint32, little-endian)
//	[N]  EncodedPayload
func (s *GroupVoiceCallService) SendFrame(ctx context.Context, callID uuid.UUID, audio []byte, isSilence bool, keyGeneration uint32) error {
	session, err := s.getGroupSession(callID)
	if err != nil {
		return err
	}
	session.mu.Lock()
	session.Sequence++
	seq := session.Sequence
	members := s.memberSnapshot(session)
	session.mu.Unlock()

	payload, err := marshalGroupVoiceFrame(callID, seq, time.Now().UnixMilli(), isSilence, keyGeneration, audio)
	if err != nil {
		return err
	}

	for _, m := range members {
		if m == s.localUhid {
			continue
		}
		pkt := protocol.NewMeshPacket()
		pkt.Type = protocol.VoiceCall
		pkt.SourceUhid = s.localUhid
		pkt.DestinationUhid = m
		pkt.Ttl = constants.DefaultTtl
		pkt.Priority = 100
		pkt.Payload = payload
		_, _ = s.sender.Send(ctx, pkt, m)
	}
	return nil
}

// HandlePacket processes inbound group voice signaling or frame packets.
func (s *GroupVoiceCallService) HandlePacket(ctx context.Context, packet *protocol.MeshPacket) error {
	if packet == nil {
		return errors.New("voice: group packet must not be nil")
	}
	switch packet.Type {
	case protocol.VoiceSignaling:
		return s.handleGroupSignaling(ctx, packet)
	case protocol.VoiceCall:
		return s.handleGroupFrame(packet)
	default:
		return fmt.Errorf("voice: unexpected packet type %s", packet.Type)
	}
}

// ──────────────────────────────────────────────
// Internal helpers
// ──────────────────────────────────────────────

func (s *GroupVoiceCallService) handleGroupSignaling(_ context.Context, packet *protocol.MeshPacket) error {
	var msg GroupVoiceSignalingMessage
	if err := json.Unmarshal(packet.Payload, &msg); err != nil {
		return fmt.Errorf("voice: unmarshal group signaling: %w", err)
	}
	callID, err := uuid.Parse(msg.CallID)
	if err != nil {
		return fmt.Errorf("voice: invalid call_id %q: %w", msg.CallID, err)
	}

	switch msg.Kind {
	case "invite":
		session := s.getOrCreateSession(callID, packet.SourceUhid)
		session.mu.Lock()
		for _, m := range msg.InvitedUhids {
			session.Members[m] = struct{}{}
		}
		session.mu.Unlock()
		if cb := s.OnInviteReceived; cb != nil {
			cb(session, packet.SourceUhid)
		}

	case "join":
		if session, err := s.getGroupSession(callID); err == nil {
			session.mu.Lock()
			session.Members[packet.SourceUhid] = struct{}{}
			session.mu.Unlock()
			if cb := s.OnMemberJoined; cb != nil {
				cb(session, packet.SourceUhid)
			}
		}

	case "leave":
		if session, err := s.getGroupSession(callID); err == nil {
			session.mu.Lock()
			delete(session.Members, packet.SourceUhid)
			session.mu.Unlock()
			if cb := s.OnMemberLeft; cb != nil {
				cb(session, packet.SourceUhid)
			}
		}

	case "kick":
		if session, err := s.getGroupSession(callID); err == nil {
			session.mu.Lock()
			delete(session.Members, msg.KickedUhid)
			session.mu.Unlock()
			if cb := s.OnMemberKicked; cb != nil {
				cb(session, msg.KickedUhid)
			}
			if msg.KickedUhid == s.localUhid {
				session.mu.Lock()
				session.State = VoiceCallStateEnded
				session.mu.Unlock()
				s.calls.Delete(callID)
			}
		}

	case "end":
		if session, err := s.getGroupSession(callID); err == nil {
			session.mu.Lock()
			session.State = VoiceCallStateEnded
			session.mu.Unlock()
			if cb := s.OnCallEnded; cb != nil {
				cb(session)
			}
			s.calls.Delete(callID)
		}

	case "key_rotation":
		if session, err := s.getGroupSession(callID); err == nil {
			session.mu.Lock()
			session.KeyGeneration = msg.KeyGeneration
			session.mu.Unlock()
			if cb := s.OnKeyRotated; cb != nil {
				cb(session)
			}
		}
	}
	return nil
}

func (s *GroupVoiceCallService) handleGroupFrame(packet *protocol.MeshPacket) error {
	frame, err := unmarshalGroupVoiceFrame(packet.Payload)
	if err != nil {
		return fmt.Errorf("voice: unmarshal group frame: %w", err)
	}
	session, err := s.getGroupSession(frame.CallID)
	if err != nil {
		return nil // unknown call — drop
	}
	if cb := s.OnFrameReceived; cb != nil {
		cb(session, frame)
	}
	return nil
}

func (s *GroupVoiceCallService) getGroupSession(callID uuid.UUID) (*GroupVoiceCallSession, error) {
	v, ok := s.calls.Load(callID)
	if !ok {
		return nil, fmt.Errorf("voice: unknown group call %s", callID)
	}
	return v.(*GroupVoiceCallSession), nil
}

func (s *GroupVoiceCallService) getOrCreateSession(callID uuid.UUID, hostUhid string) *GroupVoiceCallSession {
	v, _ := s.calls.LoadOrStore(callID, &GroupVoiceCallSession{
		CallID:    callID,
		HostUhid:  hostUhid,
		Members:   make(map[string]struct{}),
		State:     VoiceCallStateOffering,
		CreatedAt: time.Now(),
	})
	return v.(*GroupVoiceCallSession)
}

func (s *GroupVoiceCallService) memberSnapshot(session *GroupVoiceCallSession) []string {
	out := make([]string, 0, len(session.Members))
	for m := range session.Members {
		out = append(out, m)
	}
	return out
}

func (s *GroupVoiceCallService) sendGroupSignaling(ctx context.Context, toUhid string, msg GroupVoiceSignalingMessage) error {
	body, err := json.Marshal(msg)
	if err != nil {
		return fmt.Errorf("voice: marshal group signaling: %w", err)
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

func (s *GroupVoiceCallService) broadcastGroupSignaling(ctx context.Context, members []string, msg GroupVoiceSignalingMessage) error {
	for _, m := range members {
		if m == s.localUhid {
			continue
		}
		msg.ToUhid = m
		if err := s.sendGroupSignaling(ctx, m, msg); err != nil {
			return err
		}
	}
	return nil
}

// ──────────────────────────────────────────────
// Binary serialisation helpers
// ──────────────────────────────────────────────

// marshalGroupVoiceFrame encodes a GroupVoiceFrame into its canonical binary format.
func marshalGroupVoiceFrame(callID uuid.UUID, seq uint32, tsMs int64, isSilence bool, keyGen uint32, payload []byte) ([]byte, error) {
	var buf bytes.Buffer
	buf.Write(callID[:])
	if err := binary.Write(&buf, binary.LittleEndian, seq); err != nil {
		return nil, err
	}
	if err := binary.Write(&buf, binary.LittleEndian, tsMs); err != nil {
		return nil, err
	}
	if isSilence {
		buf.WriteByte(1)
	} else {
		buf.WriteByte(0)
	}
	if err := binary.Write(&buf, binary.LittleEndian, keyGen); err != nil {
		return nil, err
	}
	buf.Write(payload)
	return buf.Bytes(), nil
}

// unmarshalGroupVoiceFrame decodes a binary GroupVoiceFrame payload.
func unmarshalGroupVoiceFrame(data []byte) (*GroupVoiceFrame, error) {
	const fixedSize = 16 + 4 + 8 + 1 + 4
	if len(data) < fixedSize {
		return nil, fmt.Errorf("voice: group frame too short: %d bytes", len(data))
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

	var keyGen uint32
	if err := binary.Read(r, binary.LittleEndian, &keyGen); err != nil {
		return nil, err
	}

	encoded := make([]byte, r.Len())
	if _, err := r.Read(encoded); err != nil && r.Len() > 0 {
		return nil, err
	}

	return &GroupVoiceFrame{
		CallID:         callID,
		Sequence:       seq,
		TimestampMs:    tsMs,
		IsSilence:      silenceByte != 0,
		KeyGeneration:  keyGen,
		EncodedPayload: encoded,
	}, nil
}
