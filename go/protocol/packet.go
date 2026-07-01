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

	// PoVTokenExchange — on-mesh Proof-of-Vicinity token exchange. A witness node
	// sends a directed, witness-signed PoVToken to the subject one short-range hop
	// away (TTL 1); the subject verifies the witness's Ed25519 signature over the
	// canonical token body, counter-signs as the subject, and records it as a local
	// anti-Sybil routing/identity signal. Payload is a UTF-8 JSON-encoded PoVToken
	// (see the market package). Carries NO value semantics. Mirrors the C#
	// AetherNet.Market.PoVTokenExchangeService.
	PoVTokenExchange PacketType = 43

	// NamePublish — application-layer name resolution. Sent by IDirectoryService
	// to announce a (name -> ContentDescriptor) binding to the mesh, or in
	// response to an inbound NameQuery from a peer that asked for the binding.
	// Payload is a UTF-8 JSON-encoded NamePublishPayload. Added in v1.2.0 —
	// closes Issue #60 surfaced by Wave 16.
	NamePublish PacketType = 38

	// NameQuery — application-layer name resolution. Sent by IDirectoryService
	// when ResolveAsync misses the local cache; flooded across the mesh so any
	// node holding the binding can reply with a NamePublish carrying the
	// matching ContentDescriptor. Payload is a UTF-8 JSON-encoded
	// NameQueryPayload. Added in v1.2.0 — closes Issue #60.
	NameQuery PacketType = 39

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

	// PacketTypeReputationUpdate carries a signed reputation gossip payload.
	// Flooded with TTL=3 so reputation signals propagate across the mesh
	// without overwhelming it. Payload is a JSON-encoded ReputationUpdatePayload.
	PacketTypeReputationUpdate PacketType = 52

	// PacketTypeBandwidthProbe is sent to a target peer to measure RTT and
	// delivery rate. Overhead is kept below 0.5 % of the current BDP estimate
	// (same discipline as QUIC probe-at-1.25×BDP, RFC 9002 §7.7).
	// Payload is a four-timestamp probe descriptor (W18-5 ABMF).
	PacketTypeBandwidthProbe PacketType = 53

	// PacketTypeBandwidthAck is the reply to a PacketTypeBandwidthProbe.
	// Carries four timestamps for clock-sync-free RTT calculation (RFC 5136 §3).
	PacketTypeBandwidthAck PacketType = 54

	// PacketTypeBandwidthGossip carries a BandwidthGossipPayload during peer
	// handshake, pre-warming the new session's BtlBw estimate so it does not
	// cold-start at ~14.6 kB/s (RFC 6928 §2).
	PacketTypeBandwidthGossip PacketType = 55

	// CircuitRelayControl carries one native circuit-relay-v2 hop's frame
	// (reserve/connect/stop/data + responses) as a serialized RelayFrame in the
	// packet body. Wire byte 57 — matches the C# PacketType.CircuitRelayControl so a
	// relayed hop is byte-identical across languages; an un-upgraded node drops the
	// unknown type. The relay Transport processes these via its MeshRelayLink; only a
	// DATA frame delivered to the final destination surfaces as tunnelled app data.
	CircuitRelayControl PacketType = 57
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
	case PoVTokenExchange:
		return "PoVTokenExchange"
	case NamePublish:
		return "NamePublish"
	case NameQuery:
		return "NameQuery"
	case Hello:
		return "Hello"
	case HelloAck:
		return "HelloAck"
	case PacketTypeReputationUpdate:
		return "ReputationUpdate"
	case PacketTypeBandwidthProbe:
		return "BandwidthProbe"
	case PacketTypeBandwidthAck:
		return "BandwidthAck"
	case PacketTypeBandwidthGossip:
		return "BandwidthGossip"
	case CircuitRelayControl:
		return "CircuitRelayControl"
	default:
		return "Unknown"
	}
}
