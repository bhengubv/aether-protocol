// SPDX-License-Identifier: MIT

package bandwidth

import (
	"context"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"testing"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

// wire_test.go mirrors tests/AetherNet.Core.Tests/BandwidthWireTests.cs op-for-op:
// the binary little-endian byte-identity gates for Probe(53)/Ack(54)/Gossip(55)
// plus the send/handle behaviour. The expected_hex vectors are cross-checked
// against the SHARED corpus fixtures/bandwidth/vectors.json — every language SDK
// must serialize to exactly these bytes.

const local = "aether:local:01"

// wireFakeSender is a routing.MeshSender that records both directed sends and
// broadcasts — no transport needed. Mirrors the C# FakeMeshSender in
// BandwidthWireTests.cs (SendAsync captures packet+nextHop→true; BroadcastAsync
// captures packet and returns 3).
type wireFakeSender struct {
	uhid       string
	peers      []models.PeerInfo
	Sends      []wireSent
	Broadcasts []*protocol.MeshPacket
}

type wireSent struct {
	Packet  *protocol.MeshPacket
	NextHop string
}

func newWireFakeSender(uhid string) *wireFakeSender { return &wireFakeSender{uhid: uhid} }

func (f *wireFakeSender) LocalUhid() string                 { return f.uhid }
func (f *wireFakeSender) LocalGeohash() string              { return "" }
func (f *wireFakeSender) ConnectedPeers() []models.PeerInfo { return f.peers }

func (f *wireFakeSender) Send(ctx context.Context, packet *protocol.MeshPacket, nextHopUhid string) (bool, error) {
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.Sends = append(f.Sends, wireSent{Packet: &c, NextHop: nextHopUhid})
	return true, nil
}

func (f *wireFakeSender) Broadcast(ctx context.Context, packet *protocol.MeshPacket) (int, error) {
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.Broadcasts = append(f.Broadcasts, &c)
	return 3, nil
}

func hexOf(b []byte) string { return hex.EncodeToString(b) }

// ── Shared-corpus cross-check ──────────────────────────────────────────────────

// wireVectors mirrors the shape of fixtures/bandwidth/vectors.json.
type wireVectors struct {
	Vectors []struct {
		Name            string `json:"name"`
		Kind            string `json:"kind"`
		Sequence        uint32 `json:"sequence"`
		SenderSendUs    int64  `json:"sender_send_us"`
		ReceiverReceive int64  `json:"receiver_receive_us"`
		ReceiverSend    int64  `json:"receiver_send_us"`
		ProbeBytes      int    `json:"probe_bytes"`
		BtlBwBps        int64  `json:"btlbw_bps"`
		RtPropUs        int64  `json:"rtprop_us"`
		Confidence      int    `json:"confidence"`
		ExpectedHex     string `json:"expected_hex"`
	} `json:"vectors"`
}

// loadWireVectors walks up from this test file to fixtures/bandwidth/vectors.json,
// mirroring the fixture_test.go corpus walk-up.
func loadWireVectors(t *testing.T) wireVectors {
	t.Helper()
	_, thisFile, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatalf("runtime.Caller failed; cannot locate vectors.json")
	}
	dir := filepath.Dir(thisFile)
	for {
		candidate := filepath.Join(dir, "fixtures", "bandwidth", "vectors.json")
		if _, err := os.Stat(candidate); err == nil {
			data, readErr := os.ReadFile(candidate)
			if readErr != nil {
				t.Fatalf("reading vectors %s: %v", candidate, readErr)
			}
			var v wireVectors
			if jsonErr := json.Unmarshal(data, &v); jsonErr != nil {
				t.Fatalf("parsing vectors %s: %v", candidate, jsonErr)
			}
			return v
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			break
		}
		dir = parent
	}
	t.Fatalf("fixtures/bandwidth/vectors.json not found walking up from %s", filepath.Dir(thisFile))
	return wireVectors{}
}

// TestWire_SharedCorpus_ByteIdentity drives the Go codec through the shared hex
// vectors and asserts each expected_hex byte-for-byte.
func TestWire_SharedCorpus_ByteIdentity(t *testing.T) {
	v := loadWireVectors(t)
	if len(v.Vectors) == 0 {
		t.Fatal("no wire vectors loaded")
	}
	for _, vec := range v.Vectors {
		vec := vec
		t.Run(vec.Name, func(t *testing.T) {
			var got string
			switch vec.Kind {
			case "probe":
				got = hexOf(SerializeProbe(BandwidthProbe{
					Sequence:     vec.Sequence,
					SenderSendUs: vec.SenderSendUs,
				}))
			case "ack":
				got = hexOf(SerializeAck(BandwidthProbeAck{
					Sequence:          vec.Sequence,
					SenderSendUs:      vec.SenderSendUs,
					ReceiverReceiveUs: vec.ReceiverReceive,
					ReceiverSendUs:    vec.ReceiverSend,
					SenderReceiveUs:   0,
					ProbeBytes:        vec.ProbeBytes,
				}))
			case "gossip":
				got = hexOf(SerializeGossip(BandwidthGossipPayload{
					BtlBwBps:   vec.BtlBwBps,
					RtPropUs:   vec.RtPropUs,
					Confidence: BandwidthConfidence(vec.Confidence),
				}))
			default:
				t.Fatalf("unknown vector kind %q", vec.Kind)
			}
			if got != vec.ExpectedHex {
				t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", got, vec.ExpectedHex)
			}
		})
	}
}

// ── Byte-identity gates (literal, mirroring the C# [Fact]s) ─────────────────────

func TestWire_Probe_SerializesToCanonicalBytes(t *testing.T) {
	got := hexOf(SerializeProbe(BandwidthProbe{Sequence: 42, SenderSendUs: 1700000000000000}))
	if want := "2a00000000401e18240a0600"; got != want {
		t.Fatalf("probe hex = %s, want %s", got, want)
	}
}

func TestWire_Ack_SerializesToCanonicalBytes(t *testing.T) {
	// SenderReceiveUs (999) is local-only and must NOT change the wire bytes.
	ack := BandwidthProbeAck{
		Sequence:          42,
		SenderSendUs:      1700000000000000,
		ReceiverReceiveUs: 1700000000012345,
		ReceiverSendUs:    1700000000013000,
		SenderReceiveUs:   999,
		ProbeBytes:        1200,
	}
	got := hexOf(SerializeAck(ack))
	if want := "2a00000000401e18240a060039701e18240a0600c8721e18240a0600b0040000"; got != want {
		t.Fatalf("ack hex = %s, want %s", got, want)
	}
}

func TestWire_Gossip_SerializesToCanonicalBytes(t *testing.T) {
	// PeerUhid/TransportName/MeasuredAt are not on the wire.
	g := BandwidthGossipPayload{
		PeerUhid:      "peer",
		TransportName: "tp",
		BtlBwBps:      5000000,
		RtPropUs:      25000,
		Confidence:    ConfidenceMedium,
	}
	got := hexOf(SerializeGossip(g))
	if want := "404b4c0000000000a861000002"; got != want {
		t.Fatalf("gossip hex = %s, want %s", got, want)
	}
}

func TestWire_Ack_RoundTrips_SenderReceiveUsZeroed(t *testing.T) {
	back, err := DeserializeAck(SerializeAck(BandwidthProbeAck{
		Sequence:          7,
		SenderSendUs:      100,
		ReceiverReceiveUs: 200,
		ReceiverSendUs:    300,
		SenderReceiveUs:   400,
		ProbeBytes:        512,
	}))
	if err != nil {
		t.Fatalf("deserialize: %v", err)
	}
	if back.Sequence != 7 {
		t.Errorf("Sequence = %d, want 7", back.Sequence)
	}
	if back.SenderSendUs != 100 {
		t.Errorf("SenderSendUs = %d, want 100", back.SenderSendUs)
	}
	if back.ReceiverReceiveUs != 200 {
		t.Errorf("ReceiverReceiveUs = %d, want 200", back.ReceiverReceiveUs)
	}
	if back.ReceiverSendUs != 300 {
		t.Errorf("ReceiverSendUs = %d, want 300", back.ReceiverSendUs)
	}
	if back.SenderReceiveUs != 0 {
		t.Errorf("SenderReceiveUs = %d, want 0 (not on wire)", back.SenderReceiveUs)
	}
	if back.ProbeBytes != 512 {
		t.Errorf("ProbeBytes = %d, want 512", back.ProbeBytes)
	}
}

// ── Behaviour ───────────────────────────────────────────────────────────────────

func TestWire_SendProbe_EmitsDirectedProbe(t *testing.T) {
	s := newWireFakeSender("aether:a:01")
	svc := NewWireService(s)

	ok, err := svc.SendProbe(context.Background(), "aether:b:02", BandwidthProbe{Sequence: 42, SenderSendUs: 1700000000000000})
	if err != nil {
		t.Fatalf("send probe: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if len(s.Sends) != 1 {
		t.Fatalf("expected 1 directed send, got %d", len(s.Sends))
	}
	sent := s.Sends[0]
	if sent.Packet.Type != protocol.PacketTypeBandwidthProbe {
		t.Fatalf("expected BandwidthProbe, got %v", sent.Packet.Type)
	}
	if sent.NextHop != "aether:b:02" {
		t.Fatalf("expected next hop aether:b:02, got %s", sent.NextHop)
	}
	if sent.Packet.DestinationUhid != "aether:b:02" {
		t.Fatalf("expected dest aether:b:02, got %s", sent.Packet.DestinationUhid)
	}
	if sent.Packet.SourceUhid != "aether:a:01" {
		t.Fatalf("expected source aether:a:01, got %s", sent.Packet.SourceUhid)
	}
	if sent.Packet.Ttl != constants.DefaultTtl {
		t.Fatalf("expected ttl=DefaultTtl (%d), got %d", constants.DefaultTtl, sent.Packet.Ttl)
	}
}

func TestWire_SendAck_EmitsDirectedAck(t *testing.T) {
	s := newWireFakeSender(local)
	svc := NewWireService(s)
	ack := BandwidthProbeAck{Sequence: 1, SenderSendUs: 2, ReceiverReceiveUs: 3, ReceiverSendUs: 4, SenderReceiveUs: 5, ProbeBytes: 6}

	ok, err := svc.SendAck(context.Background(), "aether:b:02", ack)
	if err != nil {
		t.Fatalf("send ack: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if len(s.Sends) != 1 {
		t.Fatalf("expected 1 directed send, got %d", len(s.Sends))
	}
	if s.Sends[0].Packet.Type != protocol.PacketTypeBandwidthAck {
		t.Fatalf("expected BandwidthAck, got %v", s.Sends[0].Packet.Type)
	}
}

func TestWire_BroadcastGossip_EmitsGossip_AndHandleRaisesEvent_WithSourcePeer(t *testing.T) {
	s := newWireFakeSender(local)
	svc := NewWireService(s)
	g := BandwidthGossipPayload{BtlBwBps: 5000000, RtPropUs: 25000, Confidence: ConfidenceMedium}

	reached, err := svc.BroadcastGossip(context.Background(), g)
	if err != nil {
		t.Fatalf("broadcast gossip: %v", err)
	}
	if reached != 3 {
		t.Fatalf("expected 3 peers reached, got %d", reached)
	}
	if len(s.Broadcasts) != 1 {
		t.Fatalf("expected 1 broadcast, got %d", len(s.Broadcasts))
	}
	sent := s.Broadcasts[0]
	if sent.Type != protocol.PacketTypeBandwidthGossip {
		t.Fatalf("expected BandwidthGossip, got %v", sent.Type)
	}

	var got *BandwidthGossipPayload
	svc.OnGossipReceived = func(e BandwidthGossipPayload) { got = &e }
	sent.SourceUhid = "aether:peer:09"

	ok, err := svc.Handle(context.Background(), sent)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if got == nil {
		t.Fatalf("expected OnGossipReceived to fire")
	}
	if got.BtlBwBps != 5000000 {
		t.Errorf("BtlBwBps = %d, want 5000000", got.BtlBwBps)
	}
	if got.RtPropUs != 25000 {
		t.Errorf("RtPropUs = %d, want 25000", got.RtPropUs)
	}
	if got.Confidence != ConfidenceMedium {
		t.Errorf("Confidence = %v, want Medium", got.Confidence)
	}
	if got.PeerUhid != "aether:peer:09" {
		t.Errorf("PeerUhid = %q, want aether:peer:09", got.PeerUhid)
	}
}

func TestWire_Handle_Probe_RaisesProbeReceived_WithSource(t *testing.T) {
	svc := NewWireService(newWireFakeSender(local))
	var got *ProbeReceived
	svc.OnProbeReceived = func(probe BandwidthProbe, fromUhid string) {
		got = &ProbeReceived{Probe: probe, FromUhid: fromUhid}
	}

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.PacketTypeBandwidthProbe
	pkt.SourceUhid = "aether:x:01"
	pkt.Payload = SerializeProbe(BandwidthProbe{Sequence: 9, SenderSendUs: 123})

	ok, err := svc.Handle(context.Background(), pkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if got == nil {
		t.Fatalf("expected OnProbeReceived to fire")
	}
	if got.Probe.Sequence != 9 {
		t.Errorf("Probe.Sequence = %d, want 9", got.Probe.Sequence)
	}
	if got.FromUhid != "aether:x:01" {
		t.Errorf("FromUhid = %q, want aether:x:01", got.FromUhid)
	}
}

func TestWire_Handle_Ack_RaisesAckReceived(t *testing.T) {
	svc := NewWireService(newWireFakeSender(local))
	var got *BandwidthProbeAck
	svc.OnAckReceived = func(ack BandwidthProbeAck) { got = &ack }

	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.PacketTypeBandwidthAck
	pkt.SourceUhid = "aether:x:01"
	pkt.Payload = SerializeAck(BandwidthProbeAck{Sequence: 3, SenderSendUs: 10, ReceiverReceiveUs: 20, ReceiverSendUs: 30, SenderReceiveUs: 0, ProbeBytes: 64})

	ok, err := svc.Handle(context.Background(), pkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if got == nil {
		t.Fatalf("expected OnAckReceived to fire")
	}
	if got.Sequence != 3 {
		t.Errorf("Sequence = %d, want 3", got.Sequence)
	}
	if got.ProbeBytes != 64 {
		t.Errorf("ProbeBytes = %d, want 64", got.ProbeBytes)
	}
}

func TestWire_Handle_WrongType_ReturnsFalse(t *testing.T) {
	svc := NewWireService(newWireFakeSender(local))
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.Data
	pkt.Payload = []byte{}

	ok, err := svc.Handle(context.Background(), pkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for wrong packet type")
	}
}

func TestWire_Handle_ShortBuffer_ReturnsFalse(t *testing.T) {
	svc := NewWireService(newWireFakeSender(local))
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.PacketTypeBandwidthProbe
	pkt.Payload = []byte{0x01, 0x02} // < 12 bytes

	ok, err := svc.Handle(context.Background(), pkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for short buffer")
	}
}

func TestWire_Handle_NilPacket_ReturnsError(t *testing.T) {
	svc := NewWireService(newWireFakeSender(local))
	if _, err := svc.Handle(context.Background(), nil); err == nil {
		t.Fatalf("expected error for nil packet")
	}
}
