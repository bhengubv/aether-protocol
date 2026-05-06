// SPDX-License-Identifier: MIT

package protocol

import (
	"time"

	"github.com/google/uuid"
)

// PacketType defines the type of mesh packet being transmitted.
type PacketType byte

const (
	RouteRequest       PacketType = 1
	RouteReply         PacketType = 2
	Data               PacketType = 3
	Ack                PacketType = 4
	SosBroadcast       PacketType = 5
	SosAck             PacketType = 6
	ChannelMessage     PacketType = 7
	ChunkRequest       PacketType = 8
	ChunkData          PacketType = 9
	Heartbeat          PacketType = 10
	StreamAnnounce     PacketType = 11
	StreamSegment      PacketType = 12
	StreamSubscribe    PacketType = 13
	StreamUnsubscribe  PacketType = 14
	VoicePtt           PacketType = 15
	VoiceCall          PacketType = 16
	VoiceSignaling     PacketType = 17
	DtnBundle          PacketType = 18
	DtnCustodyAck      PacketType = 19
	DtnDeliveryReceipt PacketType = 20
	PresenceBeacon     PacketType = 21
	PresenceQuery      PacketType = 22
	ProfileSync        PacketType = 23
	TipPacket          PacketType = 24
	PreKeyRequest      PacketType = 25
	PreKeyResponse     PacketType = 26
	VideoCallPkt       PacketType = 27
	VideoSignaling     PacketType = 28
	WatchSync          PacketType = 29
	WatchReaction      PacketType = 30
	VideoFrame         PacketType = 31
	ScreenShare        PacketType = 32
	WatchChunkRequest  PacketType = 33
	TorrentMetadata    PacketType = 34

	// Hello — capability handshake. Sender announces supported
	// protocol-version range + capability flags. Sent on first contact with
	// an unknown peer. Payload is a UTF-8 JSON-encoded HelloPayload (see
	// the handshake package). Unauthenticated and unencrypted — peer
	// identity is verified later via Ed25519 packet signatures.
	Hello PacketType = 50

	// HelloAck — reply to a Hello. Receiver echoes back the agreed
	// (highest mutually-supported) protocol version and the intersection
	// of capability flags. Same JSON payload shape as Hello.
	HelloAck PacketType = 51
)

// MeshPacket is the core packet transmitted across the Aether mesh network.
// Every piece of data — route discovery, messages, SOS broadcasts, voice,
// streaming, DTN bundles — travels as a MeshPacket.
type MeshPacket struct {
	// Unique identifier for this packet.
	ID uuid.UUID

	// The type of packet, determining how the payload is interpreted.
	Type PacketType

	// Universal Hardware ID of the source node.
	SourceUhid string

	// Universal Hardware ID of the destination node. Empty for broadcast.
	DestinationUhid string

	// Time-to-live: decremented at each hop. Packet is dropped when TTL reaches 0.
	Ttl int32

	// Priority level (higher = more urgent). SOS packets use priority 999.
	Priority byte

	// The packet payload. Interpretation depends on Type.
	Payload []byte

	// UTC timestamp when this packet was created.
	CreatedAt time.Time

	// Cryptographic signature over the packet contents, produced by the source node.
	Signature []byte

	// Random nonce to prevent replay attacks. Must be unique per packet.
	PacketNonce []byte

	// Unix timestamp in milliseconds, used for age-based deduplication.
	TimestampMs int64

	// Protocol version. Current version is 2.
	ProtocolVersion byte
}

// NewMeshPacket creates a new MeshPacket with default values.
func NewMeshPacket() *MeshPacket {
	now := time.Now().UTC()
	return &MeshPacket{
		ID:              uuid.New(),
		Ttl:             7,
		Priority:        0,
		CreatedAt:       now,
		TimestampMs:     now.UnixMilli(),
		ProtocolVersion: 2,
	}
}

// IsExpired returns true if this packet has exceeded the maximum allowed age.
func (p *MeshPacket) IsExpired(maxAgeSeconds int32) bool {
	ageMs := time.Now().UTC().UnixMilli() - p.TimestampMs
	return ageMs > int64(maxAgeSeconds)*1000
}

// CanForward returns true if the packet can still be forwarded (TTL > 0).
func (p *MeshPacket) CanForward() bool {
	return p.Ttl > 0
}

// String returns a string representation of the packet.
func (p *MeshPacket) String() string {
	return "[" + p.Type.String() + "] " + p.ID.String() + " src=" + p.SourceUhid +
		" dst=" + p.DestinationUhid + " ttl=" + string(rune(p.Ttl)) + " pri=" + string(rune(p.Priority)) +
		" ver=" + string(rune(p.ProtocolVersion))
}

// String returns the string representation of a PacketType.
func (pt PacketType) String() string {
	switch pt {
	case RouteRequest:
		return "RouteRequest"
	case RouteReply:
		return "RouteReply"
	case Data:
		return "Data"
	case Ack:
		return "Ack"
	case SosBroadcast:
		return "SosBroadcast"
	case SosAck:
		return "SosAck"
	case ChannelMessage:
		return "ChannelMessage"
	case ChunkRequest:
		return "ChunkRequest"
	case ChunkData:
		return "ChunkData"
	case Heartbeat:
		return "Heartbeat"
	case StreamAnnounce:
		return "StreamAnnounce"
	case StreamSegment:
		return "StreamSegment"
	case StreamSubscribe:
		return "StreamSubscribe"
	case StreamUnsubscribe:
		return "StreamUnsubscribe"
	case VoicePtt:
		return "VoicePtt"
	case VoiceCall:
		return "VoiceCall"
	case VoiceSignaling:
		return "VoiceSignaling"
	case DtnBundle:
		return "DtnBundle"
	case DtnCustodyAck:
		return "DtnCustodyAck"
	case DtnDeliveryReceipt:
		return "DtnDeliveryReceipt"
	case PresenceBeacon:
		return "PresenceBeacon"
	case PresenceQuery:
		return "PresenceQuery"
	case ProfileSync:
		return "ProfileSync"
	case TipPacket:
		return "TipPacket"
	case PreKeyRequest:
		return "PreKeyRequest"
	case PreKeyResponse:
		return "PreKeyResponse"
	case VideoCallPkt:
		return "VideoCall"
	case VideoSignaling:
		return "VideoSignaling"
	case WatchSync:
		return "WatchSync"
	case WatchReaction:
		return "WatchReaction"
	case VideoFrame:
		return "VideoFrame"
	case ScreenShare:
		return "ScreenShare"
	case WatchChunkRequest:
		return "WatchChunkRequest"
	case TorrentMetadata:
		return "TorrentMetadata"
	case Hello:
		return "Hello"
	case HelloAck:
		return "HelloAck"
	default:
		return "Unknown"
	}
}
