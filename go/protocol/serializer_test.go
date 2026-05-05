// SPDX-License-Identifier: MIT

package protocol

import (
	"bytes"
	"testing"

	"github.com/google/uuid"
)

// Mirror of swift/Tests/PacketSerializationTests.swift; cross-language byte
// equivalence is anchored separately under fixtures/.

func eightByteNonce(fill byte) []byte {
	out := make([]byte, 8)
	for i := range out {
		out[i] = fill
	}
	return out
}

func TestSerializeDeserialize_RoundTrip(t *testing.T) {
	ps := &PacketSerializer{}
	pkt := &MeshPacket{
		ID:              uuid.New(),
		Type:            Data,
		SourceUhid:      "alice-node",
		DestinationUhid: "bob-node",
		Ttl:             7,
		Priority:        10,
		Payload:         []byte("Hello, Aether!"),
		PacketNonce:     eightByteNonce(0xAB),
		TimestampMs:     1710528000000,
		ProtocolVersion: 2,
	}
	b, err := ps.Serialize(pkt)
	if err != nil {
		t.Fatalf("serialize: %v", err)
	}
	got, err := ps.Deserialize(b)
	if err != nil {
		t.Fatalf("deserialize: %v", err)
	}
	if got.Type != pkt.Type {
		t.Errorf("type: got %v want %v", got.Type, pkt.Type)
	}
	if got.SourceUhid != pkt.SourceUhid {
		t.Errorf("source: got %q want %q", got.SourceUhid, pkt.SourceUhid)
	}
	if got.DestinationUhid != pkt.DestinationUhid {
		t.Errorf("dest: got %q want %q", got.DestinationUhid, pkt.DestinationUhid)
	}
	if got.Ttl != pkt.Ttl {
		t.Errorf("ttl: got %d want %d", got.Ttl, pkt.Ttl)
	}
	if got.Priority != pkt.Priority {
		t.Errorf("priority: got %d want %d", got.Priority, pkt.Priority)
	}
	if !bytes.Equal(got.Payload, pkt.Payload) {
		t.Errorf("payload mismatch")
	}
	if !bytes.Equal(got.PacketNonce, pkt.PacketNonce) {
		t.Errorf("nonce mismatch")
	}
	if got.ProtocolVersion != pkt.ProtocolVersion {
		t.Errorf("version: got %d want %d", got.ProtocolVersion, pkt.ProtocolVersion)
	}
}

func TestEmptyDestinationUhid_RoundTrips(t *testing.T) {
	ps := &PacketSerializer{}
	pkt := &MeshPacket{
		ID: uuid.New(), Type: SosBroadcast, SourceUhid: "node-1",
		DestinationUhid: "", PacketNonce: eightByteNonce(0), ProtocolVersion: 2,
	}
	b, _ := ps.Serialize(pkt)
	got, err := ps.Deserialize(b)
	if err != nil {
		t.Fatalf("deserialize: %v", err)
	}
	if got.SourceUhid != "node-1" || got.DestinationUhid != "" {
		t.Errorf("uhid: got src=%q dst=%q", got.SourceUhid, got.DestinationUhid)
	}
}

func TestEmptyPayload_RoundTrips(t *testing.T) {
	ps := &PacketSerializer{}
	pkt := &MeshPacket{
		ID: uuid.New(), Type: Heartbeat, SourceUhid: "node-1",
		PacketNonce: eightByteNonce(0), Payload: []byte{}, ProtocolVersion: 2,
	}
	b, _ := ps.Serialize(pkt)
	got, _ := ps.Deserialize(b)
	if len(got.Payload) != 0 {
		t.Errorf("payload len: got %d want 0", len(got.Payload))
	}
}

func TestLargePayload_RoundTrips(t *testing.T) {
	ps := &PacketSerializer{}
	payload := bytes.Repeat([]byte{0xFF}, 262144)
	pkt := &MeshPacket{
		ID: uuid.New(), Type: ChunkData, SourceUhid: "node-1", DestinationUhid: "node-2",
		PacketNonce: eightByteNonce(0), Payload: payload, ProtocolVersion: 2,
	}
	b, _ := ps.Serialize(pkt)
	got, _ := ps.Deserialize(b)
	if len(got.Payload) != 262144 {
		t.Errorf("payload len: got %d want 262144", len(got.Payload))
	}
}

func TestUuid_RoundTrips(t *testing.T) {
	ps := &PacketSerializer{}
	expected := uuid.MustParse("550e8400-e29b-41d4-a716-446655440000")
	pkt := &MeshPacket{
		ID: expected, Type: Data, SourceUhid: "node-1",
		PacketNonce: eightByteNonce(0), ProtocolVersion: 2,
	}
	b, _ := ps.Serialize(pkt)
	got, _ := ps.Deserialize(b)
	if got.ID != expected {
		t.Errorf("uuid: got %v want %v", got.ID, expected)
	}
}

func TestUuid_WireOrderIsRfc4122BigEndian(t *testing.T) {
	// 16 bytes after [version(1), type(1)] must be UUID in RFC4122 big-endian.
	ps := &PacketSerializer{}
	expected := uuid.MustParse("550e8400-e29b-41d4-a716-446655440000")
	pkt := &MeshPacket{
		ID: expected, Type: Data, SourceUhid: "n",
		PacketNonce: eightByteNonce(0), ProtocolVersion: 2,
	}
	b, _ := ps.Serialize(pkt)
	want := []byte{
		0x55, 0x0e, 0x84, 0x00, 0xe2, 0x9b, 0x41, 0xd4,
		0xa7, 0x16, 0x44, 0x66, 0x55, 0x44, 0x00, 0x00,
	}
	if !bytes.Equal(b[2:18], want) {
		t.Errorf("uuid bytes: got %x want %x", b[2:18], want)
	}
}

func TestTooShort_ReturnsError(t *testing.T) {
	ps := &PacketSerializer{}
	if _, err := ps.Deserialize([]byte{0x01, 0x02}); err == nil {
		t.Errorf("expected error for too-short input")
	}
}

func TestAllPacketTypes_RoundTrip(t *testing.T) {
	ps := &PacketSerializer{}
	for _, ty := range []PacketType{
		RouteRequest, RouteReply, Data, Ack, SosBroadcast, SosAck,
		ChannelMessage, ChunkRequest, ChunkData, Heartbeat,
		StreamAnnounce, StreamSegment, StreamSubscribe, StreamUnsubscribe,
		VoicePtt, VoiceCall, VoiceSignaling,
		DtnBundle, DtnCustodyAck, DtnDeliveryReceipt,
		PresenceBeacon, PresenceQuery, ProfileSync, TipPacket,
		PreKeyRequest, PreKeyResponse,
	} {
		pkt := &MeshPacket{
			ID: uuid.New(), Type: ty, SourceUhid: "n",
			PacketNonce: eightByteNonce(0), ProtocolVersion: 2,
		}
		b, _ := ps.Serialize(pkt)
		got, _ := ps.Deserialize(b)
		if got.Type != ty {
			t.Errorf("type round-trip: got %d want %d", got.Type, ty)
		}
	}
}

func TestTimestamp_PreservedToTheMillisecond(t *testing.T) {
	ps := &PacketSerializer{}
	const ts int64 = 1710528000000 // 2024-03-15 12:00:00 UTC
	pkt := &MeshPacket{
		ID: uuid.New(), Type: Data, SourceUhid: "node-1",
		TimestampMs: ts, PacketNonce: eightByteNonce(0), ProtocolVersion: 2,
	}
	b, _ := ps.Serialize(pkt)
	got, _ := ps.Deserialize(b)
	if got.TimestampMs != ts {
		t.Errorf("timestamp: got %d want %d", got.TimestampMs, ts)
	}
}

func TestUnicodeUhids_RoundTrip(t *testing.T) {
	ps := &PacketSerializer{}
	pkt := &MeshPacket{
		ID: uuid.New(), Type: Data, SourceUhid: "노드-1", DestinationUhid: "узел-2",
		PacketNonce: eightByteNonce(0), ProtocolVersion: 2,
	}
	b, _ := ps.Serialize(pkt)
	got, _ := ps.Deserialize(b)
	if got.SourceUhid != "노드-1" || got.DestinationUhid != "узел-2" {
		t.Errorf("unicode uhids: got src=%q dst=%q", got.SourceUhid, got.DestinationUhid)
	}
}

func TestSignature_Preserved(t *testing.T) {
	ps := &PacketSerializer{}
	sig := bytes.Repeat([]byte{0xAB}, 64)
	pkt := &MeshPacket{
		ID: uuid.New(), Type: Data, SourceUhid: "node-1",
		PacketNonce: eightByteNonce(0), Signature: sig, ProtocolVersion: 2,
	}
	b, _ := ps.Serialize(pkt)
	got, _ := ps.Deserialize(b)
	if !bytes.Equal(got.Signature, sig) {
		t.Errorf("signature mismatch")
	}
}

func TestTtl_FullInt32RangePreserved(t *testing.T) {
	// > UInt8 max — would have wrapped to 0 under the pre-2026-05-02 bug.
	ps := &PacketSerializer{}
	pkt := &MeshPacket{
		ID: uuid.New(), Type: Data, SourceUhid: "n",
		Ttl: 256, PacketNonce: eightByteNonce(0), ProtocolVersion: 2,
	}
	b, _ := ps.Serialize(pkt)
	got, _ := ps.Deserialize(b)
	if got.Ttl != 256 {
		t.Errorf("ttl: got %d want 256", got.Ttl)
	}
}
