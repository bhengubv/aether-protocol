// SPDX-License-Identifier: MIT

// Package channels implements application-layer named-channel pub/sub over the
// Aether mesh (PacketType.ChannelMessage). A named channel is an opaque topic
// ("res-floor-3", a society, a project team). A node subscribes to the channel
// ids it cares about; Publish floods a ChannelMessage across the mesh; subscribed
// receivers surface it via OnMessageReceived. Messages are de-duplicated by their
// message id and re-flooded (TTL-bounded) so they reach subscribers several hops
// away. The original author is carried in the payload's sender_uhid so it survives
// relay hops (the enclosing packet's SourceUhid changes at each hop). Mirrors the
// C# AetherNet.Channels.ChannelMessageService.
package channels

import (
	"context"
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

// MessageReceived is surfaced when a channel message arrives on a channel this
// node is subscribed to (and is not this node's own message). Mirrors the C#
// ChannelMessageReceived event args.
type MessageReceived struct {
	// ChannelId the message was published to.
	ChannelId string
	// MessageId is the unique id of the message.
	MessageId uuid.UUID
	// SenderUhid is the UHID of the original author.
	SenderUhid string
	// Content is the message body.
	Content string
	// SentAtMs is the Unix-ms timestamp the author published the message.
	SentAtMs int64
}

// Service is the default named-channel pub/sub service. Publishing floods a
// ChannelMessage; receivers de-dup by message id, surface messages for subscribed
// channels, and re-flood (TTL-bounded) so the message reaches subscribers multiple
// hops away.
type Service struct {
	sender routing.MeshSender

	mu            sync.Mutex
	subscriptions map[string]struct{}
	seen          map[uuid.UUID]struct{}

	// OnMessageReceived fires when a message arrives on a subscribed channel. It
	// does NOT fire for this node's own messages. Mirrors the C# MessageReceived event.
	OnMessageReceived func(msg MessageReceived)
}

// NewService constructs a Service. Panics if sender is nil.
func NewService(sender routing.MeshSender) *Service {
	if sender == nil {
		panic("channels: sender must not be nil")
	}
	return &Service{
		sender:        sender,
		subscriptions: make(map[string]struct{}),
		seen:          make(map[uuid.UUID]struct{}),
	}
}

// Subscribe subscribes to a channel — messages on it will fire OnMessageReceived.
func (s *Service) Subscribe(channelId string) {
	if channelId == "" {
		return
	}
	s.mu.Lock()
	s.subscriptions[channelId] = struct{}{}
	s.mu.Unlock()
}

// Unsubscribe stops surfacing messages for a channel.
func (s *Service) Unsubscribe(channelId string) {
	s.mu.Lock()
	delete(s.subscriptions, channelId)
	s.mu.Unlock()
}

// GetSubscriptions returns the channels this node is currently subscribed to.
func (s *Service) GetSubscriptions() []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]string, 0, len(s.subscriptions))
	for c := range s.subscriptions {
		out = append(out, c)
	}
	return out
}

// Publish publishes content to channelId: floods a ChannelMessage (dest "*",
// TTL constants.DefaultTtl) to all peers, seeding the dedup set with its own id so
// the message is never re-handled when it floods back. Returns the number of peers
// reached directly. Returns an error if channelId is empty.
func (s *Service) Publish(ctx context.Context, channelId, content string) (int, error) {
	if channelId == "" {
		return 0, errors.New("channels: channelId must not be empty")
	}

	messageID := uuid.New()
	body, err := json.Marshal(channelMessageWire{
		ChannelID:  channelId,
		MessageID:  messageID,
		SenderUhid: s.sender.LocalUhid(),
		Content:    content,
		SentAtMs:   time.Now().UnixMilli(),
	})
	if err != nil {
		return 0, fmt.Errorf("channels: marshal payload: %w", err)
	}

	s.mu.Lock()
	s.seen[messageID] = struct{}{} // never re-handle our own message when it floods back
	s.mu.Unlock()

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.ChannelMessage
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = "*"
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = body

	delivered, err := s.sender.Broadcast(ctx, pkt)
	if err != nil {
		return 0, err
	}
	return delivered, nil
}

// Handle processes an inbound ChannelMessage packet: de-dup by message id, surface
// it if we are subscribed to its channel (and it is not our own), and re-flood while
// TTL allows (and it is not our own). Returns false for the wrong packet type, a
// malformed payload, an empty channel id, or a duplicate. Returns an error only if
// the packet is nil.
func (s *Service) Handle(ctx context.Context, packet *protocol.MeshPacket) (bool, error) {
	if packet == nil {
		return false, errors.New("channels: packet must not be nil")
	}
	if packet.Type != protocol.ChannelMessage {
		return false, nil
	}

	var body channelMessageWire
	if err := json.Unmarshal(packet.Payload, &body); err != nil {
		// Malformed payload: log-and-drop, not a caller error (mirrors C#).
		return false, nil
	}
	if body.ChannelID == "" {
		return false, nil
	}

	// Flood de-duplication: only the first copy of a given message id is processed.
	s.mu.Lock()
	if _, dup := s.seen[body.MessageID]; dup {
		s.mu.Unlock()
		return false, nil
	}
	s.seen[body.MessageID] = struct{}{}
	_, subscribed := s.subscriptions[body.ChannelID]
	s.mu.Unlock()

	isOwn := body.SenderUhid == s.sender.LocalUhid()
	if !isOwn && subscribed {
		if cb := s.OnMessageReceived; cb != nil {
			cb(MessageReceived{
				ChannelId:  body.ChannelID,
				MessageId:  body.MessageID,
				SenderUhid: body.SenderUhid,
				Content:    body.Content,
				SentAtMs:   body.SentAtMs,
			})
		}
	}

	// Re-flood so subscribers further out receive it — even if WE aren't subscribed (pure relay).
	if packet.Ttl > 1 && !isOwn {
		packet.Ttl--
		_, _ = s.sender.Broadcast(ctx, packet)
	}

	return true, nil
}

// channelMessageWire is the snake_case JSON payload for PacketType.ChannelMessage
// packets. Wire format: UTF-8 JSON, snake_case keys, field order channel_id,
// message_id, sender_uhid, content, sent_at_ms, no whitespace, lowercase-dashed
// UUID, sent_at_ms a bare integer. This is the byte-identity gate for named-channel
// pub/sub (fixtures/channels/vectors.json). MessageID is a uuid.UUID so it marshals
// to the canonical lowercase-dashed form across every language port. Mirrors the C#
// ChannelMessagePayload.
type channelMessageWire struct {
	ChannelID  string    `json:"channel_id"`
	MessageID  uuid.UUID `json:"message_id"`
	SenderUhid string    `json:"sender_uhid"`
	Content    string    `json:"content"`
	SentAtMs   int64     `json:"sent_at_ms"`
}
