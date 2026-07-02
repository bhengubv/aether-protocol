// SPDX-License-Identifier: MIT

package media

import (
	"bytes"
	"context"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"runtime"
	"testing"

	"github.com/google/uuid"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

const local = "aether:local:01"

// callID is the shared test call id used across the byte-identity vectors.
// Mirrors the C# MediaFrameTests.CallId.
var callID = uuid.MustParse("0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f")

// fakeSender is a routing.MeshSender that records directed sends — no transport
// needed. Mirrors the C# FakeMeshSender in MediaFrameTests.cs (SendAsync captures
// the packet + next hop and returns true).
type fakeSender struct {
	uhid  string
	peers []models.PeerInfo
	Sends []sentPacket
}

type sentPacket struct {
	Packet  *protocol.MeshPacket
	NextHop string
}

func newFakeSender(uhid string) *fakeSender { return &fakeSender{uhid: uhid} }

func (f *fakeSender) LocalUhid() string                 { return f.uhid }
func (f *fakeSender) LocalGeohash() string              { return "" }
func (f *fakeSender) ConnectedPeers() []models.PeerInfo { return f.peers }

func (f *fakeSender) Send(ctx context.Context, packet *protocol.MeshPacket, nextHopUhid string) (bool, error) {
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.Sends = append(f.Sends, sentPacket{Packet: &c, NextHop: nextHopUhid})
	return true, nil
}

func (f *fakeSender) Broadcast(ctx context.Context, packet *protocol.MeshPacket) (int, error) {
	return 0, nil
}

func hexOf(b []byte) string { return hex.EncodeToString(b) }

// ─── Byte-identity gates ──────────────────────────────────
// Lock the VoicePtt(15) + ScreenShare(32) frame wire encoding to the canonical
// hex in fixtures/media/vectors.json. call_id big-endian, sequence/timestamp
// little-endian, flag byte.

func TestVoicePtt_Frame_SerializesToCanonicalBytes(t *testing.T) {
	f := &VoicePttFrame{CallId: callID, Sequence: 42, TimestampMs: 1700000000000, IsSilence: false, EncodedPayload: []byte{0xAA, 0xBB, 0xCC}}
	got, err := SerializeVoicePtt(f)
	if err != nil {
		t.Fatalf("serialize: %v", err)
	}
	const want = "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2a0000000068e5cf8b01000000aabbcc"
	if hexOf(got) != want {
		t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", hexOf(got), want)
	}
}

func TestVoicePtt_SilenceEmpty_SerializesToCanonicalBytes(t *testing.T) {
	f := &VoicePttFrame{CallId: callID, Sequence: 43, TimestampMs: 1700000000020, IsSilence: true, EncodedPayload: []byte{}}
	got, err := SerializeVoicePtt(f)
	if err != nil {
		t.Fatalf("serialize: %v", err)
	}
	const want = "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2b0000001468e5cf8b01000001"
	if hexOf(got) != want {
		t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", hexOf(got), want)
	}
}

func TestScreenShare_Keyframe_SerializesToCanonicalBytes(t *testing.T) {
	f := &ScreenShareFrame{CallId: callID, Sequence: 7, TimestampMs: 1700000000000, IsKeyframe: true, EncodedPayload: []byte{0x11, 0x22, 0x33, 0x44}}
	got, err := SerializeScreenShare(f)
	if err != nil {
		t.Fatalf("serialize: %v", err)
	}
	const want = "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f070000000068e5cf8b0100000111223344"
	if hexOf(got) != want {
		t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", hexOf(got), want)
	}
}

func TestScreenShare_DeltaEmpty_SerializesToCanonicalBytes(t *testing.T) {
	f := &ScreenShareFrame{CallId: uuid.Nil, Sequence: 0, TimestampMs: 0, IsKeyframe: false, EncodedPayload: []byte{}}
	got, err := SerializeScreenShare(f)
	if err != nil {
		t.Fatalf("serialize: %v", err)
	}
	const want = "0000000000000000000000000000000000000000000000000000000000"
	if hexOf(got) != want {
		t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", hexOf(got), want)
	}
}

func TestVoicePtt_RoundTrips(t *testing.T) {
	f := &VoicePttFrame{CallId: callID, Sequence: 99, TimestampMs: 123456789, IsSilence: true, EncodedPayload: []byte{1, 2, 3, 4, 5}}
	buf, err := SerializeVoicePtt(f)
	if err != nil {
		t.Fatalf("serialize: %v", err)
	}
	back, err := DeserializeVoicePtt(buf)
	if err != nil {
		t.Fatalf("deserialize: %v", err)
	}
	if back.CallId != callID {
		t.Errorf("call id: got %s want %s", back.CallId, callID)
	}
	if back.Sequence != 99 {
		t.Errorf("sequence: got %d want 99", back.Sequence)
	}
	if back.TimestampMs != 123456789 {
		t.Errorf("timestamp: got %d want 123456789", back.TimestampMs)
	}
	if !back.IsSilence {
		t.Errorf("is_silence: got false want true")
	}
	if !bytes.Equal(back.EncodedPayload, f.EncodedPayload) {
		t.Errorf("payload: got %x want %x", back.EncodedPayload, f.EncodedPayload)
	}
}

func TestScreenShare_RoundTrips_KeyframeAndCallIdBigEndian(t *testing.T) {
	f := &ScreenShareFrame{CallId: callID, Sequence: 5, TimestampMs: 999, IsKeyframe: true, EncodedPayload: []byte{0xFF}}
	buf, err := SerializeScreenShare(f)
	if err != nil {
		t.Fatalf("serialize: %v", err)
	}
	back, err := DeserializeScreenShare(buf)
	if err != nil {
		t.Fatalf("deserialize: %v", err)
	}
	if back.CallId != callID {
		t.Errorf("call id: got %s want %s", back.CallId, callID)
	}
	if !back.IsKeyframe {
		t.Errorf("is_keyframe: got false want true")
	}
	if !bytes.Equal(back.EncodedPayload, []byte{0xFF}) {
		t.Errorf("payload: got %x want ff", back.EncodedPayload)
	}
}

// ─── Behaviour ────────────────────────────────────────────

func TestVoicePtt_Send_EmitsDirectedFrame_AndHandleRaisesEvent(t *testing.T) {
	s := newFakeSender("aether:alice:01")
	svc := NewVoicePttService(s)
	frame := &VoicePttFrame{CallId: callID, Sequence: 42, TimestampMs: 1700000000000, EncodedPayload: []byte{0xAA, 0xBB, 0xCC}}

	ok, err := svc.SendFrame(context.Background(), "aether:bob:02", frame)
	if err != nil {
		t.Fatalf("send: %v", err)
	}
	if !ok {
		t.Fatalf("expected send ok=true")
	}
	if len(s.Sends) != 1 {
		t.Fatalf("expected 1 directed send, got %d", len(s.Sends))
	}
	sent := s.Sends[0]
	if sent.Packet.Type != protocol.VoicePtt {
		t.Fatalf("expected VoicePtt, got %v", sent.Packet.Type)
	}
	if sent.NextHop != "aether:bob:02" {
		t.Fatalf("expected next hop aether:bob:02, got %s", sent.NextHop)
	}
	if sent.Packet.Ttl != constants.DefaultTtl {
		t.Fatalf("expected ttl=DefaultTtl (%d), got %d", constants.DefaultTtl, sent.Packet.Ttl)
	}

	var got *VoicePttFrameReceived
	svc.OnFrameReceived = func(e VoicePttFrameReceived) { got = &e }
	sent.Packet.SourceUhid = "aether:alice:01"
	if !svc.Handle(sent.Packet) {
		t.Fatalf("expected Handle ok=true")
	}
	if got == nil {
		t.Fatalf("expected OnFrameReceived to fire")
	}
	if got.Frame.Sequence != 42 {
		t.Errorf("sequence: got %d want 42", got.Frame.Sequence)
	}
	if got.FromUhid != "aether:alice:01" {
		t.Errorf("from: got %s want aether:alice:01", got.FromUhid)
	}
	if !bytes.Equal(got.Frame.EncodedPayload, []byte{0xAA, 0xBB, 0xCC}) {
		t.Errorf("payload: got %x want aabbcc", got.Frame.EncodedPayload)
	}
}

func TestScreenShare_Send_EmitsDirectedFrame_AndHandleRaisesEvent(t *testing.T) {
	s := newFakeSender("aether:alice:01")
	svc := NewScreenShareService(s)
	frame := &ScreenShareFrame{CallId: callID, Sequence: 7, TimestampMs: 1700000000000, IsKeyframe: true, EncodedPayload: []byte{0x11, 0x22, 0x33, 0x44}}

	ok, err := svc.SendFrame(context.Background(), "aether:bob:02", frame)
	if err != nil {
		t.Fatalf("send: %v", err)
	}
	if !ok {
		t.Fatalf("expected send ok=true")
	}
	if len(s.Sends) != 1 {
		t.Fatalf("expected 1 directed send, got %d", len(s.Sends))
	}
	sent := s.Sends[0]
	if sent.Packet.Type != protocol.ScreenShare {
		t.Fatalf("expected ScreenShare, got %v", sent.Packet.Type)
	}

	var got *ScreenShareFrameReceived
	svc.OnFrameReceived = func(e ScreenShareFrameReceived) { got = &e }
	if !svc.Handle(sent.Packet) {
		t.Fatalf("expected Handle ok=true")
	}
	if got == nil {
		t.Fatalf("expected OnFrameReceived to fire")
	}
	if !got.Frame.IsKeyframe {
		t.Errorf("is_keyframe: got false want true")
	}
	if got.Frame.Sequence != 7 {
		t.Errorf("sequence: got %d want 7", got.Frame.Sequence)
	}
}

func TestHandle_WrongType_ReturnsFalse(t *testing.T) {
	vp := NewVoicePttService(newFakeSender(local))
	ss := NewScreenShareService(newFakeSender(local))
	wrong := &protocol.MeshPacket{Type: protocol.Data, Payload: make([]byte, 40)}
	if vp.Handle(wrong) {
		t.Fatalf("VoicePtt: expected false for wrong packet type")
	}
	if ss.Handle(wrong) {
		t.Fatalf("ScreenShare: expected false for wrong packet type")
	}
}

func TestHandle_ShortFrame_ReturnsFalse(t *testing.T) {
	vp := NewVoicePttService(newFakeSender(local))
	if vp.Handle(&protocol.MeshPacket{Type: protocol.VoicePtt, Payload: make([]byte, 10)}) {
		t.Fatalf("expected false for short (<29 byte) frame")
	}
}

// ─── Shared cross-language fixture (fixtures/media/vectors.json) ───
// Independently drives every expected_hex vector through the codec so the Go
// port is byte-identical to the C# reference oracle. Do NOT edit the fixture.

type mediaVector struct {
	Name        string `json:"name"`
	CallID      string `json:"call_id"`
	Sequence    uint32 `json:"sequence"`
	TimestampMs int64  `json:"timestamp_ms"`
	IsSilence   bool   `json:"is_silence"`
	IsKeyframe  bool   `json:"is_keyframe"`
	PayloadHex  string `json:"payload_hex"`
	ExpectedHex string `json:"expected_hex"`
}

type mediaVectors struct {
	VoicePttVectors    []mediaVector `json:"voice_ptt_vectors"`
	ScreenShareVectors []mediaVector `json:"screen_share_vectors"`
}

func loadMediaVectors(t *testing.T) mediaVectors {
	t.Helper()
	_, here, _, _ := runtime.Caller(0)
	// here = .../go/media/service_test.go → up three = .../aether-protocol/
	root := filepath.Dir(filepath.Dir(filepath.Dir(here)))
	raw, err := os.ReadFile(filepath.Join(root, "fixtures", "media", "vectors.json"))
	if err != nil {
		t.Fatalf("read media vectors.json: %v", err)
	}
	var v mediaVectors
	if err := json.Unmarshal(raw, &v); err != nil {
		t.Fatalf("parse media vectors.json: %v", err)
	}
	return v
}

func payloadOf(t *testing.T, hexStr string) []byte {
	t.Helper()
	if hexStr == "" {
		return []byte{}
	}
	b, err := hex.DecodeString(hexStr)
	if err != nil {
		t.Fatalf("hex decode %q: %v", hexStr, err)
	}
	return b
}

func TestMediaVectors_SerializeMatchesExpected(t *testing.T) {
	v := loadMediaVectors(t)

	for _, vec := range v.VoicePttVectors {
		t.Run("voice_ptt/"+vec.Name, func(t *testing.T) {
			f := &VoicePttFrame{
				CallId:         uuid.MustParse(vec.CallID),
				Sequence:       vec.Sequence,
				TimestampMs:    vec.TimestampMs,
				IsSilence:      vec.IsSilence,
				EncodedPayload: payloadOf(t, vec.PayloadHex),
			}
			got, err := SerializeVoicePtt(f)
			if err != nil {
				t.Fatalf("serialize: %v", err)
			}
			if hexOf(got) != vec.ExpectedHex {
				t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", hexOf(got), vec.ExpectedHex)
			}
			// Round-trip back from the canonical bytes.
			expBytes, _ := hex.DecodeString(vec.ExpectedHex)
			back, err := DeserializeVoicePtt(expBytes)
			if err != nil {
				t.Fatalf("deserialize: %v", err)
			}
			if back.CallId != f.CallId || back.Sequence != f.Sequence || back.TimestampMs != f.TimestampMs || back.IsSilence != f.IsSilence || !bytes.Equal(back.EncodedPayload, f.EncodedPayload) {
				t.Fatalf("round-trip mismatch for %s", vec.Name)
			}
		})
	}

	for _, vec := range v.ScreenShareVectors {
		t.Run("screen_share/"+vec.Name, func(t *testing.T) {
			f := &ScreenShareFrame{
				CallId:         uuid.MustParse(vec.CallID),
				Sequence:       vec.Sequence,
				TimestampMs:    vec.TimestampMs,
				IsKeyframe:     vec.IsKeyframe,
				EncodedPayload: payloadOf(t, vec.PayloadHex),
			}
			got, err := SerializeScreenShare(f)
			if err != nil {
				t.Fatalf("serialize: %v", err)
			}
			if hexOf(got) != vec.ExpectedHex {
				t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", hexOf(got), vec.ExpectedHex)
			}
			// Round-trip back from the canonical bytes.
			expBytes, _ := hex.DecodeString(vec.ExpectedHex)
			back, err := DeserializeScreenShare(expBytes)
			if err != nil {
				t.Fatalf("deserialize: %v", err)
			}
			if back.CallId != f.CallId || back.Sequence != f.Sequence || back.TimestampMs != f.TimestampMs || back.IsKeyframe != f.IsKeyframe || !bytes.Equal(back.EncodedPayload, f.EncodedPayload) {
				t.Fatalf("round-trip mismatch for %s", vec.Name)
			}
		})
	}
}
