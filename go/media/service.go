// SPDX-License-Identifier: MIT

// Package media implements the VoicePtt (PacketType 15) and ScreenShare
// (PacketType 32) media-frame bindings for the Aether mesh — directed
// push-to-talk audio and screen-share video frames sent to a single peer.
//
// Both frame types share one 29-byte binary header, so a node can treat them
// uniformly (the same header the VoiceCall(16)/VideoFrame(31) frames use):
//
//	[0..15]  call_id       16 bytes, RFC-4122 BIG-ENDIAN (network order —
//	                       uuid.MarshalBinary(), NOT the .NET mixed-endian
//	                       Guid.ToByteArray() layout)
//	[16..19] sequence      u32 LITTLE-ENDIAN
//	[20..27] timestamp_ms  i64 LITTLE-ENDIAN
//	[28]     flag          u8 (VoicePtt: is_silence; ScreenShare: is_keyframe)
//	[29..]   payload       opaque encoded audio/video bytes
//
// The call_id is big-endian (network order), the same convention the DTN bundle
// id uses (go/dtn) — reuse of uuid.MarshalBinary/UnmarshalBinary keeps it
// byte-identical across every language SDK. Byte-identity gate:
// fixtures/media/vectors.json (expected_hex). Mirrors the C#
// AetherNet.Media.MediaFrameCodec / VoicePttService / ScreenShareService.
package media

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"

	"github.com/google/uuid"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
)

// headerLength is the fixed size of the shared media-frame header (bytes 0..28).
const headerLength = 29

// VoicePttFrame is a push-to-talk audio frame (PacketType.VoicePtt = 15 body).
// Mirrors the C# VoicePttFrame.
type VoicePttFrame struct {
	// CallId identifies the call/session this frame belongs to.
	CallId uuid.UUID
	// Sequence is the monotonically increasing frame counter (u32).
	Sequence uint32
	// TimestampMs is the capture time in Unix milliseconds (i64).
	TimestampMs int64
	// IsSilence marks a comfort-noise / silence frame.
	IsSilence bool
	// EncodedPayload is the opaque encoded audio payload.
	EncodedPayload []byte
}

// ScreenShareFrame is a screen-share video frame (PacketType.ScreenShare = 32
// body). Mirrors the C# ScreenShareFrame.
type ScreenShareFrame struct {
	// CallId identifies the call/session this frame belongs to.
	CallId uuid.UUID
	// Sequence is the monotonically increasing frame counter (u32).
	Sequence uint32
	// TimestampMs is the capture time in Unix milliseconds (i64).
	TimestampMs int64
	// IsKeyframe marks a full keyframe (vs a delta frame).
	IsKeyframe bool
	// EncodedPayload is the opaque encoded video payload.
	EncodedPayload []byte
}

// SerializeVoicePtt encodes a VoicePttFrame to its canonical binary wire form.
// Mirrors the C# MediaFrameCodec.SerializeVoicePtt.
func SerializeVoicePtt(f *VoicePttFrame) ([]byte, error) {
	if f == nil {
		return nil, errors.New("media: frame must not be nil")
	}
	return serializeFrame(f.CallId, f.Sequence, f.TimestampMs, f.IsSilence, f.EncodedPayload)
}

// SerializeScreenShare encodes a ScreenShareFrame to its canonical binary wire
// form. Mirrors the C# MediaFrameCodec.SerializeScreenShare.
func SerializeScreenShare(f *ScreenShareFrame) ([]byte, error) {
	if f == nil {
		return nil, errors.New("media: frame must not be nil")
	}
	return serializeFrame(f.CallId, f.Sequence, f.TimestampMs, f.IsKeyframe, f.EncodedPayload)
}

// serializeFrame lays out the shared 29-byte header + payload. call_id is
// written big-endian (uuid.MarshalBinary); sequence/timestamp are little-endian.
func serializeFrame(callID uuid.UUID, sequence uint32, timestampMs int64, flag bool, payload []byte) ([]byte, error) {
	idBytes, err := callID.MarshalBinary() // RFC-4122 big-endian, 16 bytes
	if err != nil {
		return nil, fmt.Errorf("media: marshal call id: %w", err)
	}
	buf := make([]byte, headerLength+len(payload))
	copy(buf[0:16], idBytes)
	binary.LittleEndian.PutUint32(buf[16:20], sequence)
	binary.LittleEndian.PutUint64(buf[20:28], uint64(timestampMs))
	if flag {
		buf[28] = 1
	} else {
		buf[28] = 0
	}
	copy(buf[headerLength:], payload)
	return buf, nil
}

// DeserializeVoicePtt decodes a VoicePttFrame from its binary wire form. Returns
// an error if b is shorter than the 29-byte header. Mirrors the C#
// MediaFrameCodec.DeserializeVoicePtt.
func DeserializeVoicePtt(b []byte) (*VoicePttFrame, error) {
	callID, sequence, timestampMs, flag, payload, err := deserializeFrame(b)
	if err != nil {
		return nil, err
	}
	return &VoicePttFrame{
		CallId:         callID,
		Sequence:       sequence,
		TimestampMs:    timestampMs,
		IsSilence:      flag,
		EncodedPayload: payload,
	}, nil
}

// DeserializeScreenShare decodes a ScreenShareFrame from its binary wire form.
// Returns an error if b is shorter than the 29-byte header. Mirrors the C#
// MediaFrameCodec.DeserializeScreenShare.
func DeserializeScreenShare(b []byte) (*ScreenShareFrame, error) {
	callID, sequence, timestampMs, flag, payload, err := deserializeFrame(b)
	if err != nil {
		return nil, err
	}
	return &ScreenShareFrame{
		CallId:         callID,
		Sequence:       sequence,
		TimestampMs:    timestampMs,
		IsKeyframe:     flag,
		EncodedPayload: payload,
	}, nil
}

// deserializeFrame parses the shared 29-byte header + payload. call_id is read
// big-endian (uuid.UnmarshalBinary); sequence/timestamp are little-endian.
func deserializeFrame(b []byte) (callID uuid.UUID, sequence uint32, timestampMs int64, flag bool, payload []byte, err error) {
	if len(b) < headerLength {
		return uuid.Nil, 0, 0, false, nil, fmt.Errorf("media: frame too short: %d bytes, need at least %d", len(b), headerLength)
	}
	if err = callID.UnmarshalBinary(b[0:16]); err != nil {
		return uuid.Nil, 0, 0, false, nil, fmt.Errorf("media: unmarshal call id: %w", err)
	}
	sequence = binary.LittleEndian.Uint32(b[16:20])
	timestampMs = int64(binary.LittleEndian.Uint64(b[20:28]))
	flag = b[28] != 0
	// Copy the payload so callers can't mutate the caller's backing array.
	payload = append([]byte(nil), b[headerLength:]...)
	return callID, sequence, timestampMs, flag, payload, nil
}

// VoicePttFrameReceived is surfaced when an inbound VoicePtt frame arrives from a
// peer. Mirrors the C# VoicePttFrameReceived event args.
type VoicePttFrameReceived struct {
	// Frame is the decoded push-to-talk frame.
	Frame *VoicePttFrame
	// FromUhid is the UHID of the peer that sent the frame (the packet source).
	FromUhid string
}

// ScreenShareFrameReceived is surfaced when an inbound ScreenShare frame arrives
// from a peer. Mirrors the C# ScreenShareFrameReceived event args.
type ScreenShareFrameReceived struct {
	// Frame is the decoded screen-share frame.
	Frame *ScreenShareFrame
	// FromUhid is the UHID of the peer that sent the frame (the packet source).
	FromUhid string
}

// VoicePttService binds PacketType.VoicePtt (15) to the mesh: directed
// push-to-talk audio frames + inbound event. Mirrors the C# VoicePttService.
type VoicePttService struct {
	sender routing.MeshSender

	// OnFrameReceived fires when a VoicePtt frame is received from a peer.
	// Mirrors the C# FrameReceived event.
	OnFrameReceived func(evt VoicePttFrameReceived)
}

// NewVoicePttService constructs a VoicePttService. Panics if sender is nil.
func NewVoicePttService(sender routing.MeshSender) *VoicePttService {
	if sender == nil {
		panic("media: sender must not be nil")
	}
	return &VoicePttService{sender: sender}
}

// SendFrame sends a directed VoicePtt frame (dest=peer, ttl=constants.DefaultTtl)
// to peerUhid. Returns delivery success. Returns an error if peerUhid is empty or
// frame is nil. Mirrors the C# VoicePttService.SendFrameAsync.
func (s *VoicePttService) SendFrame(ctx context.Context, peerUhid string, frame *VoicePttFrame) (bool, error) {
	if peerUhid == "" {
		return false, errors.New("media: peerUhid must not be empty")
	}
	if frame == nil {
		return false, errors.New("media: frame must not be nil")
	}
	body, err := SerializeVoicePtt(frame)
	if err != nil {
		return false, err
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VoicePtt
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = peerUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body
	return s.sender.Send(ctx, pkt, peerUhid)
}

// Handle processes an inbound VoicePtt packet: parse and fire OnFrameReceived
// with (frame, fromUhid=packet source). Returns false for the wrong packet type
// or a malformed (too-short) frame. Mirrors the C# VoicePttService.HandleAsync.
func (s *VoicePttService) Handle(packet *protocol.MeshPacket) bool {
	if packet == nil {
		return false
	}
	if packet.Type != protocol.VoicePtt {
		return false
	}
	frame, err := DeserializeVoicePtt(packet.Payload)
	if err != nil {
		// Malformed frame: log-and-drop, not a caller error (mirrors C#).
		return false
	}
	if cb := s.OnFrameReceived; cb != nil {
		cb(VoicePttFrameReceived{Frame: frame, FromUhid: packet.SourceUhid})
	}
	return true
}

// ScreenShareService binds PacketType.ScreenShare (32) to the mesh: directed
// screen-share video frames + inbound event. Mirrors the C# ScreenShareService.
type ScreenShareService struct {
	sender routing.MeshSender

	// OnFrameReceived fires when a ScreenShare frame is received from a peer.
	// Mirrors the C# FrameReceived event.
	OnFrameReceived func(evt ScreenShareFrameReceived)
}

// NewScreenShareService constructs a ScreenShareService. Panics if sender is nil.
func NewScreenShareService(sender routing.MeshSender) *ScreenShareService {
	if sender == nil {
		panic("media: sender must not be nil")
	}
	return &ScreenShareService{sender: sender}
}

// SendFrame sends a directed ScreenShare frame (dest=peer, ttl=constants.DefaultTtl)
// to peerUhid. Returns delivery success. Returns an error if peerUhid is empty or
// frame is nil. Mirrors the C# ScreenShareService.SendFrameAsync.
func (s *ScreenShareService) SendFrame(ctx context.Context, peerUhid string, frame *ScreenShareFrame) (bool, error) {
	if peerUhid == "" {
		return false, errors.New("media: peerUhid must not be empty")
	}
	if frame == nil {
		return false, errors.New("media: frame must not be nil")
	}
	body, err := SerializeScreenShare(frame)
	if err != nil {
		return false, err
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.ScreenShare
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = peerUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body
	return s.sender.Send(ctx, pkt, peerUhid)
}

// Handle processes an inbound ScreenShare packet: parse and fire OnFrameReceived
// with (frame, fromUhid=packet source). Returns false for the wrong packet type
// or a malformed (too-short) frame. Mirrors the C# ScreenShareService.HandleAsync.
func (s *ScreenShareService) Handle(packet *protocol.MeshPacket) bool {
	if packet == nil {
		return false
	}
	if packet.Type != protocol.ScreenShare {
		return false
	}
	frame, err := DeserializeScreenShare(packet.Payload)
	if err != nil {
		// Malformed frame: log-and-drop, not a caller error (mirrors C#).
		return false
	}
	if cb := s.OnFrameReceived; cb != nil {
		cb(ScreenShareFrameReceived{Frame: frame, FromUhid: packet.SourceUhid})
	}
	return true
}
