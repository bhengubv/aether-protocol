// SPDX-License-Identifier: MIT

package bandwidth

import (
	"context"
	"encoding/binary"
	"errors"
	"math"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/routing"
)

// wire.go implements the ABMF WIRE bindings: the three PacketType bodies that
// carry bandwidth measurement across the mesh.
//
//	Probe(53)  : sequence u32 | sender_send_us i64                                                                      (12 B)
//	Ack(54)    : sequence u32 | sender_send_us i64 | receiver_receive_us i64 | receiver_send_us i64 | probe_bytes i32   (32 B)
//	Gossip(55) : btlbw_bps i64 | rtprop_us i32 | confidence u8                                                          (13 B)
//
// All multi-byte integers are LITTLE-ENDIAN, matching the packet-serializer
// convention. There is NO version byte — the layouts are exactly the ones on the
// protocol.PacketTypeBandwidth* members. Byte-identity gate:
// fixtures/bandwidth/vectors.json (lowercase hex).
//
// SenderReceiveUs is NOT on the wire — the prober fills it locally on receipt, so
// the serializer omits it and the deserializer sets it to 0. A gossip's
// PeerUhid/TransportName/MeasuredAt are NOT in the body either; the service fills
// PeerUhid from the enclosing packet's source on receipt. Mirrors the C#
// AetherNet.Bandwidth.BandwidthWireCodec + BandwidthWireService.

const (
	probeWireLen  = 12
	ackWireLen    = 32
	gossipWireLen = 13
)

// ── Codec ─────────────────────────────────────────────────────────────────────

// SerializeProbe encodes a BandwidthProbe to its 12-byte little-endian body.
func SerializeProbe(p BandwidthProbe) []byte {
	buf := make([]byte, probeWireLen)
	binary.LittleEndian.PutUint32(buf[0:], p.Sequence)
	binary.LittleEndian.PutUint64(buf[4:], uint64(p.SenderSendUs))
	return buf
}

// DeserializeProbe decodes a BandwidthProbe body. Returns an error if b is shorter
// than 12 bytes.
func DeserializeProbe(b []byte) (BandwidthProbe, error) {
	if len(b) < probeWireLen {
		return BandwidthProbe{}, errors.New("bandwidth: BandwidthProbe payload too short")
	}
	return BandwidthProbe{
		Sequence:     binary.LittleEndian.Uint32(b[0:]),
		SenderSendUs: int64(binary.LittleEndian.Uint64(b[4:])),
	}, nil
}

// SerializeAck encodes a BandwidthProbeAck to its 32-byte little-endian body.
// SenderReceiveUs is local-only and is deliberately NOT written.
func SerializeAck(a BandwidthProbeAck) []byte {
	buf := make([]byte, ackWireLen)
	binary.LittleEndian.PutUint32(buf[0:], a.Sequence)
	binary.LittleEndian.PutUint64(buf[4:], uint64(a.SenderSendUs))
	binary.LittleEndian.PutUint64(buf[12:], uint64(a.ReceiverReceiveUs))
	binary.LittleEndian.PutUint64(buf[20:], uint64(a.ReceiverSendUs))
	binary.LittleEndian.PutUint32(buf[28:], uint32(int32(a.ProbeBytes)))
	return buf
}

// DeserializeAck decodes a BandwidthProbeAck body. SenderReceiveUs is set to 0 —
// it is not carried on the wire and is filled locally by the prober on receipt.
// Returns an error if b is shorter than 32 bytes.
func DeserializeAck(b []byte) (BandwidthProbeAck, error) {
	if len(b) < ackWireLen {
		return BandwidthProbeAck{}, errors.New("bandwidth: BandwidthProbeAck payload too short")
	}
	return BandwidthProbeAck{
		Sequence:          binary.LittleEndian.Uint32(b[0:]),
		SenderSendUs:      int64(binary.LittleEndian.Uint64(b[4:])),
		ReceiverReceiveUs: int64(binary.LittleEndian.Uint64(b[12:])),
		ReceiverSendUs:    int64(binary.LittleEndian.Uint64(b[20:])),
		SenderReceiveUs:   0, // not on the wire — filled by the prober on receipt
		ProbeBytes:        int(int32(binary.LittleEndian.Uint32(b[28:]))),
	}, nil
}

// SerializeGossip encodes a BandwidthGossipPayload to its 13-byte little-endian
// body. RtPropUs is clamped to [0, math.MaxInt32] before being written as an i32,
// matching the C# Math.Clamp(g.RtPropUs, 0, int.MaxValue). PeerUhid/TransportName/
// MeasuredAt are NOT part of the body.
func SerializeGossip(g BandwidthGossipPayload) []byte {
	buf := make([]byte, gossipWireLen)
	binary.LittleEndian.PutUint64(buf[0:], uint64(g.BtlBwBps))
	rtProp := g.RtPropUs
	if rtProp < 0 {
		rtProp = 0
	} else if rtProp > math.MaxInt32 {
		rtProp = math.MaxInt32
	}
	binary.LittleEndian.PutUint32(buf[8:], uint32(int32(rtProp)))
	buf[12] = byte(g.Confidence)
	return buf
}

// DeserializeGossip decodes a BandwidthGossipPayload body. PeerUhid/TransportName
// default to empty and MeasuredAt to the zero time; the service fills PeerUhid from
// the enclosing packet. Returns an error if b is shorter than 13 bytes.
func DeserializeGossip(b []byte) (BandwidthGossipPayload, error) {
	if len(b) < gossipWireLen {
		return BandwidthGossipPayload{}, errors.New("bandwidth: BandwidthGossipPayload payload too short")
	}
	return BandwidthGossipPayload{
		PeerUhid:      "",
		TransportName: "",
		BtlBwBps:      int64(binary.LittleEndian.Uint64(b[0:])),
		RtPropUs:      int64(int32(binary.LittleEndian.Uint32(b[8:]))),
		Confidence:    BandwidthConfidence(b[12]),
	}, nil
}

// ── Service ───────────────────────────────────────────────────────────────────

// ProbeReceived pairs an inbound probe with the peer that sent it, so the host can
// reply with an ack. Mirrors the C# BandwidthProbeReceived event args.
type ProbeReceived struct {
	Probe    BandwidthProbe
	FromUhid string
}

// WireService binds the three ABMF PacketTypes to the mesh: it sends probes
// (directed) and their acks (directed reply), and broadcasts/receives warm-start
// gossip. Inbound packets surface via the On* callbacks; the host feeds them into a
// BandwidthEstimator (RecordProbeResult / WarmFromGossip) and replies to probes.
// Mirrors the C# AetherNet.Bandwidth.BandwidthWireService.
type WireService struct {
	sender routing.MeshSender

	// OnProbeReceived fires when a BandwidthProbe arrives, with the source peer's
	// UHID so the host can reply with an ack. Mirrors the C# ProbeReceived event.
	OnProbeReceived func(probe BandwidthProbe, fromUhid string)

	// OnAckReceived fires when a BandwidthAck arrives. Mirrors the C# AckReceived event.
	OnAckReceived func(ack BandwidthProbeAck)

	// OnGossipReceived fires when a BandwidthGossip arrives, with PeerUhid filled
	// from the enclosing packet's source. Mirrors the C# GossipReceived event.
	OnGossipReceived func(gossip BandwidthGossipPayload)
}

// NewWireService constructs a WireService. Panics if sender is nil.
func NewWireService(sender routing.MeshSender) *WireService {
	if sender == nil {
		panic("bandwidth: sender must not be nil")
	}
	return &WireService{sender: sender}
}

// SendProbe sends a directed BandwidthProbe (PacketType 53) to peerUhid. Returns
// delivery success. Returns an error if peerUhid is empty.
func (s *WireService) SendProbe(ctx context.Context, peerUhid string, probe BandwidthProbe) (bool, error) {
	if peerUhid == "" {
		return false, errors.New("bandwidth: peerUhid must not be empty")
	}
	return s.sendDirected(ctx, peerUhid, protocol.PacketTypeBandwidthProbe, SerializeProbe(probe))
}

// SendAck sends a directed BandwidthAck (PacketType 54) reply to peerUhid. Returns
// delivery success. Returns an error if peerUhid is empty.
func (s *WireService) SendAck(ctx context.Context, peerUhid string, ack BandwidthProbeAck) (bool, error) {
	if peerUhid == "" {
		return false, errors.New("bandwidth: peerUhid must not be empty")
	}
	return s.sendDirected(ctx, peerUhid, protocol.PacketTypeBandwidthAck, SerializeAck(ack))
}

// sendDirected builds and sends a directed bandwidth packet (dest=peer,
// ttl=constants.DefaultTtl) carrying the given wire body.
func (s *WireService) sendDirected(ctx context.Context, peerUhid string, typ protocol.PacketType, payload []byte) (bool, error) {
	pkt := protocol.NewMeshPacket()
	pkt.Type = typ
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = peerUhid
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = payload
	return s.sender.Send(ctx, pkt, peerUhid)
}

// BroadcastGossip broadcasts a BandwidthGossip (PacketType 55) warm-start estimate
// to all directly connected peers. Returns the number of peers reached.
func (s *WireService) BroadcastGossip(ctx context.Context, gossip BandwidthGossipPayload) (int, error) {
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.PacketTypeBandwidthGossip
	pkt.SourceUhid = s.sender.LocalUhid()
	pkt.DestinationUhid = "*"
	pkt.Ttl = constants.DefaultTtl
	pkt.Payload = SerializeGossip(gossip)
	return s.sender.Broadcast(ctx, pkt)
}

// Handle dispatches an inbound bandwidth packet to the matching callback:
//   - BandwidthProbe  → OnProbeReceived(probe, packet source)
//   - BandwidthAck    → OnAckReceived(ack)
//   - BandwidthGossip → OnGossipReceived(gossip with PeerUhid = packet source)
//
// Returns false (no error) for the wrong packet type or a malformed/short body.
// Returns an error only if the packet is nil.
func (s *WireService) Handle(ctx context.Context, packet *protocol.MeshPacket) (bool, error) {
	if packet == nil {
		return false, errors.New("bandwidth: packet must not be nil")
	}

	switch packet.Type {
	case protocol.PacketTypeBandwidthProbe:
		probe, err := DeserializeProbe(packet.Payload)
		if err != nil {
			// Malformed payload: log-and-drop, not a caller error (mirrors C#).
			return false, nil
		}
		if cb := s.OnProbeReceived; cb != nil {
			cb(probe, packet.SourceUhid)
		}
		return true, nil

	case protocol.PacketTypeBandwidthAck:
		ack, err := DeserializeAck(packet.Payload)
		if err != nil {
			return false, nil
		}
		if cb := s.OnAckReceived; cb != nil {
			cb(ack)
		}
		return true, nil

	case protocol.PacketTypeBandwidthGossip:
		gossip, err := DeserializeGossip(packet.Payload)
		if err != nil {
			return false, nil
		}
		gossip.PeerUhid = packet.SourceUhid // filled from the enclosing packet, not the wire body
		if cb := s.OnGossipReceived; cb != nil {
			cb(gossip)
		}
		return true, nil

	default:
		return false, nil
	}
}
