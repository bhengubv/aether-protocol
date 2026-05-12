// SPDX-License-Identifier: MIT

// Package streaming implements live stream publishing, subscription, and segment
// relay for the Aether mesh. VideoCallService and WatchTogetherService are in
// the same package.
//
// Wire formats:
//
//	StreamAnnounce    → JSON (StreamAnnouncePayload)
//	StreamSubscribe   → JSON (StreamSubscribePayload)
//	StreamUnsubscribe → JSON (StreamUnsubscribePayload)
//	StreamSegment     → binary StreamSegment
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
	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
)

// ──────────────────────────────────────────────
// JSON wire types
// ──────────────────────────────────────────────

// StreamAnnouncePayload is the JSON body for StreamAnnounce packets.
type StreamAnnouncePayload struct {
	StreamID          string `json:"stream_id"`
	Title             string `json:"title"`
	ContentType       string `json:"content_type"`
	Codec             string `json:"codec"`
	SegmentDurationMs int    `json:"segment_duration_ms"`
	State             string `json:"state"` // "live" | "ended"
	StartedAtMs       int64  `json:"started_at_ms"`
}

// StreamSubscribePayload is the JSON body for StreamSubscribe packets.
type StreamSubscribePayload struct {
	StreamID string `json:"stream_id"`
	LiveOnly bool   `json:"live_only"`
}

// StreamUnsubscribePayload is the JSON body for StreamUnsubscribe packets.
type StreamUnsubscribePayload struct {
	StreamID string `json:"stream_id"`
}

// ──────────────────────────────────────────────
// Local session state
// ──────────────────────────────────────────────

// StreamSession holds runtime state for an active outbound stream.
type StreamSession struct {
	StreamID          uuid.UUID
	Title             string
	ContentType       string
	Codec             string
	SegmentDurationMs int
	Sequence          uint32
	StartedAt         time.Time
	subscribers       sync.Map // uhid -> struct{}
}

// ──────────────────────────────────────────────
// Service
// ──────────────────────────────────────────────

// StreamingService manages live audio/video stream publishing and relaying.
type StreamingService struct {
	sender    routing.MeshSender
	localUhid string
	streams   sync.Map // uuid.UUID -> *StreamSession

	// subscribers maps streamID -> *sync.Map (uhid -> struct{})
	// used by the relay side to track remote subscribers.
	subscribers sync.Map

	// Event callbacks — optional.
	OnStreamAnnounced    func(payload *StreamAnnouncePayload, fromUhid string)
	OnStreamEnded        func(streamID uuid.UUID, fromUhid string)
	OnSegmentReceived    func(streamID uuid.UUID, segment *StreamSegmentFrame)
	OnSubscribed         func(streamID uuid.UUID, fromUhid string)
	OnUnsubscribed       func(streamID uuid.UUID, fromUhid string)
}

// StreamSegmentFrame is the decoded form of a binary StreamSegment payload.
type StreamSegmentFrame struct {
	StreamID       uuid.UUID
	Sequence       uint32
	TimestampMs    int64
	IsKeyframe     bool
	EncodedPayload []byte
}

// NewStreamingService constructs a StreamingService.
func NewStreamingService(sender routing.MeshSender) *StreamingService {
	if sender == nil {
		panic("streaming: sender must not be nil")
	}
	return &StreamingService{
		sender:    sender,
		localUhid: sender.LocalUhid(),
	}
}

// StartStream announces a new live stream on the mesh. Returns the new stream ID.
func (s *StreamingService) StartStream(ctx context.Context, title, contentType, codec string, segmentDurationMs int) (uuid.UUID, error) {
	if title == "" {
		return uuid.Nil, errors.New("streaming: title must not be empty")
	}
	if segmentDurationMs <= 0 {
		segmentDurationMs = int(constants.DefaultSegmentDurationMs)
	}

	streamID := uuid.New()
	session := &StreamSession{
		StreamID:          streamID,
		Title:             title,
		ContentType:       contentType,
		Codec:             codec,
		SegmentDurationMs: segmentDurationMs,
		StartedAt:         time.Now(),
	}
	s.streams.Store(streamID, session)

	payload := StreamAnnouncePayload{
		StreamID:          streamID.String(),
		Title:             title,
		ContentType:       contentType,
		Codec:             codec,
		SegmentDurationMs: segmentDurationMs,
		State:             "live",
		StartedAtMs:       session.StartedAt.UnixMilli(),
	}
	return streamID, s.broadcastAnnounce(ctx, payload)
}

// EndStream stops a stream and notifies subscribers.
func (s *StreamingService) EndStream(ctx context.Context, streamID uuid.UUID) error {
	session, err := s.getStream(streamID)
	if err != nil {
		return err
	}
	s.streams.Delete(streamID)

	payload := StreamAnnouncePayload{
		StreamID:    streamID.String(),
		State:       "ended",
		StartedAtMs: session.StartedAt.UnixMilli(),
	}
	return s.broadcastAnnounce(ctx, payload)
}

// Subscribe sends a StreamSubscribe packet to publisherUhid.
func (s *StreamingService) Subscribe(ctx context.Context, streamID uuid.UUID, publisherUhid string, liveOnly bool) error {
	if publisherUhid == "" {
		return errors.New("streaming: publisherUhid must not be empty")
	}
	body, err := json.Marshal(StreamSubscribePayload{
		StreamID: streamID.String(),
		LiveOnly: liveOnly,
	})
	if err != nil {
		return fmt.Errorf("streaming: marshal subscribe: %w", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.StreamSubscribe
	pkt.SourceUhid = s.localUhid
	pkt.DestinationUhid = publisherUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body
	_, err = s.sender.Send(ctx, pkt, publisherUhid)
	return err
}

// Unsubscribe sends a StreamUnsubscribe packet to publisherUhid.
func (s *StreamingService) Unsubscribe(ctx context.Context, streamID uuid.UUID, publisherUhid string) error {
	if publisherUhid == "" {
		return errors.New("streaming: publisherUhid must not be empty")
	}
	body, err := json.Marshal(StreamUnsubscribePayload{StreamID: streamID.String()})
	if err != nil {
		return fmt.Errorf("streaming: marshal unsubscribe: %w", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.StreamUnsubscribe
	pkt.SourceUhid = s.localUhid
	pkt.DestinationUhid = publisherUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body
	_, err = s.sender.Send(ctx, pkt, publisherUhid)
	return err
}

// PublishSegment encodes and delivers a segment to all current subscribers.
//
// Binary layout (StreamSegment):
//
//	[16] StreamId      (UUID, big-endian)
//	[4]  Sequence      (uint32, little-endian)
//	[8]  TimestampMs   (int64, little-endian)
//	[1]  IsKeyframe    (0 or 1)
//	[N]  EncodedPayload
func (s *StreamingService) PublishSegment(ctx context.Context, streamID uuid.UUID, data []byte, isKeyframe bool) error {
	session, err := s.getStream(streamID)
	if err != nil {
		return err
	}
	session.Sequence++
	payload, err := marshalStreamSegment(streamID, session.Sequence, time.Now().UnixMilli(), isKeyframe, data)
	if err != nil {
		return err
	}

	// Fan out to all registered subscribers for this stream.
	subMap := s.getOrCreateSubMap(streamID)
	subMap.Range(func(key, _ any) bool {
		peerUhid, _ := key.(string)
		pkt := protocol.NewMeshPacket()
		pkt.Type = protocol.StreamSegment
		pkt.SourceUhid = s.localUhid
		pkt.DestinationUhid = peerUhid
		pkt.Ttl = constants.DefaultTtl
		pkt.Payload = payload
		_, _ = s.sender.Send(ctx, pkt, peerUhid)
		return true
	})
	return nil
}

// HandlePacket processes inbound streaming-related packets.
func (s *StreamingService) HandlePacket(ctx context.Context, packet *protocol.MeshPacket) error {
	if packet == nil {
		return errors.New("streaming: packet must not be nil")
	}
	switch packet.Type {
	case protocol.StreamAnnounce:
		return s.handleAnnounce(packet)
	case protocol.StreamSubscribe:
		return s.handleSubscribe(ctx, packet)
	case protocol.StreamUnsubscribe:
		return s.handleUnsubscribe(ctx, packet)
	case protocol.StreamSegment:
		return s.handleSegment(packet)
	default:
		return fmt.Errorf("streaming: unexpected packet type %s", packet.Type)
	}
}

// ──────────────────────────────────────────────
// Internal helpers
// ──────────────────────────────────────────────

func (s *StreamingService) handleAnnounce(packet *protocol.MeshPacket) error {
	var payload StreamAnnouncePayload
	if err := json.Unmarshal(packet.Payload, &payload); err != nil {
		return fmt.Errorf("streaming: unmarshal announce: %w", err)
	}
	switch payload.State {
	case "live":
		if cb := s.OnStreamAnnounced; cb != nil {
			cb(&payload, packet.SourceUhid)
		}
	case "ended":
		streamID, err := uuid.Parse(payload.StreamID)
		if err == nil {
			if cb := s.OnStreamEnded; cb != nil {
				cb(streamID, packet.SourceUhid)
			}
		}
	}
	return nil
}

func (s *StreamingService) handleSubscribe(_ context.Context, packet *protocol.MeshPacket) error {
	var payload StreamSubscribePayload
	if err := json.Unmarshal(packet.Payload, &payload); err != nil {
		return fmt.Errorf("streaming: unmarshal subscribe: %w", err)
	}
	streamID, err := uuid.Parse(payload.StreamID)
	if err != nil {
		return fmt.Errorf("streaming: invalid stream_id: %w", err)
	}
	subMap := s.getOrCreateSubMap(streamID)
	subMap.Store(packet.SourceUhid, struct{}{})
	if cb := s.OnSubscribed; cb != nil {
		cb(streamID, packet.SourceUhid)
	}
	return nil
}

func (s *StreamingService) handleUnsubscribe(_ context.Context, packet *protocol.MeshPacket) error {
	var payload StreamUnsubscribePayload
	if err := json.Unmarshal(packet.Payload, &payload); err != nil {
		return fmt.Errorf("streaming: unmarshal unsubscribe: %w", err)
	}
	streamID, err := uuid.Parse(payload.StreamID)
	if err != nil {
		return fmt.Errorf("streaming: invalid stream_id: %w", err)
	}
	if m, ok := s.subscribers.Load(streamID); ok {
		m.(*sync.Map).Delete(packet.SourceUhid)
	}
	if cb := s.OnUnsubscribed; cb != nil {
		cb(streamID, packet.SourceUhid)
	}
	return nil
}

func (s *StreamingService) handleSegment(packet *protocol.MeshPacket) error {
	frame, err := unmarshalStreamSegment(packet.Payload)
	if err != nil {
		return fmt.Errorf("streaming: unmarshal segment: %w", err)
	}
	if cb := s.OnSegmentReceived; cb != nil {
		cb(frame.StreamID, frame)
	}
	return nil
}

func (s *StreamingService) getStream(streamID uuid.UUID) (*StreamSession, error) {
	v, ok := s.streams.Load(streamID)
	if !ok {
		return nil, fmt.Errorf("streaming: unknown stream %s", streamID)
	}
	return v.(*StreamSession), nil
}

func (s *StreamingService) getOrCreateSubMap(streamID uuid.UUID) *sync.Map {
	v, _ := s.subscribers.LoadOrStore(streamID, &sync.Map{})
	return v.(*sync.Map)
}

func (s *StreamingService) broadcastAnnounce(ctx context.Context, payload StreamAnnouncePayload) error {
	body, err := json.Marshal(payload)
	if err != nil {
		return fmt.Errorf("streaming: marshal announce: %w", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.StreamAnnounce
	pkt.SourceUhid = s.localUhid
	pkt.DestinationUhid = ""
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body
	_, err = s.sender.Broadcast(ctx, pkt)
	return err
}

// ──────────────────────────────────────────────
// Binary serialisation helpers
// ──────────────────────────────────────────────

// marshalStreamSegment encodes a StreamSegment into its canonical binary format.
func marshalStreamSegment(streamID uuid.UUID, seq uint32, tsMs int64, isKeyframe bool, payload []byte) ([]byte, error) {
	var buf bytes.Buffer
	buf.Write(streamID[:])
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

// unmarshalStreamSegment decodes a binary StreamSegment payload.
func unmarshalStreamSegment(data []byte) (*StreamSegmentFrame, error) {
	const fixedSize = 16 + 4 + 8 + 1
	if len(data) < fixedSize {
		return nil, fmt.Errorf("streaming: segment too short: %d bytes", len(data))
	}
	r := bytes.NewReader(data)

	var streamIDBytes [16]byte
	if _, err := r.Read(streamIDBytes[:]); err != nil {
		return nil, err
	}
	streamID, err := uuid.FromBytes(streamIDBytes[:])
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

	return &StreamSegmentFrame{
		StreamID:       streamID,
		Sequence:       seq,
		TimestampMs:    tsMs,
		IsKeyframe:     kfByte != 0,
		EncodedPayload: encoded,
	}, nil
}
