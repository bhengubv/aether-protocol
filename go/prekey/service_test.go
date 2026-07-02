// SPDX-License-Identifier: MIT

package prekey

import (
	"bytes"
	"context"
	"encoding/json"
	"testing"

	"github.com/google/uuid"

	"github.com/bhengubv/aether-protocol/go/constants"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
	"github.com/bhengubv/aether-protocol/go/security"
)

// fakeSender is a routing.MeshSender that records directed sends — no transport
// needed. Mirrors the C# FakeMeshSender in PreKeyExchangeTests.cs (SendAsync
// captures the packet + next hop and returns true).
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

// fill returns an n-byte slice filled with b. Mirrors the constant-byte key material
// used by the C# SampleBundle (0x11 identity, 0x22 identity_x25519, 0x33 pre_key,
// 0x44 signed_pre_key, 0x55 signature) so a field swap is caught.
func fill(b byte, n int) []byte {
	out := make([]byte, n)
	for i := range out {
		out[i] = b
	}
	return out
}

// sampleBundle mirrors the C# SampleBundle: fixed constant-byte fill so wire vectors
// are reproducible and a field swap surfaces as a byte-identity mismatch.
func sampleBundle(uhid string) security.PreKeyBundle {
	return security.PreKeyBundle{
		Uhid:                  uhid,
		IdentityKey:           fill(0x11, 32),
		IdentityKeyX25519:     fill(0x22, 32),
		PreKeyID:              4242,
		PreKey:                fill(0x33, 32),
		SignedPreKeyID:        77,
		SignedPreKey:          fill(0x44, 32),
		SignedPreKeySignature: fill(0x55, 64),
	}
}

func mustParse(t *testing.T, s string) uuid.UUID {
	t.Helper()
	id, err := uuid.Parse(s)
	if err != nil {
		t.Fatalf("parse uuid %q: %v", s, err)
	}
	return id
}

// ─── Byte-identity gate ───────────────────────────────────
// Locks the PreKeyRequest/PreKeyResponse wire encoding to fixtures/prekey/vectors.json.

func TestRequestPayload_SerializesToCanonicalBytes(t *testing.T) {
	got, err := json.Marshal(requestWire{
		RequestID:     mustParse(t, "11112222-3333-4444-5555-666677778888"),
		RequesterUhid: "aether:alice:01",
	})
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	const want = `{"request_id":"11112222-3333-4444-5555-666677778888","requester_uhid":"aether:alice:01"}`
	if string(got) != want {
		t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", got, want)
	}
}

func TestResponsePayload_SerializesToCanonicalBytes(t *testing.T) {
	got, err := json.Marshal(responseFromBundle(
		mustParse(t, "7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a"), sampleBundle("aether:bob:02")))
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	const want = `{"request_id":"7a1e9c4d-2b3f-4a5e-8c6d-0f1e2d3c4b5a","uhid":"aether:bob:02",` +
		`"identity_key":"ERERERERERERERERERERERERERERERERERERERERERE=",` +
		`"identity_key_x25519":"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI=",` +
		`"pre_key_id":4242,"pre_key":"MzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzM=",` +
		`"signed_pre_key_id":77,"signed_pre_key":"REREREREREREREREREREREREREREREREREREREREREQ=",` +
		`"signed_pre_key_signature":"VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVQ=="}`
	if string(got) != want {
		t.Fatalf("byte-identity mismatch:\n got: %s\nwant: %s", got, want)
	}
}

func TestResponsePayload_RoundTripsThroughBundle(t *testing.T) {
	original := sampleBundle("aether:bob:02")
	back := responseFromBundle(uuid.New(), original).toBundle()

	if back.Uhid != original.Uhid {
		t.Fatalf("uhid: got %s want %s", back.Uhid, original.Uhid)
	}
	if back.PreKeyID != original.PreKeyID {
		t.Fatalf("pre_key_id: got %d want %d", back.PreKeyID, original.PreKeyID)
	}
	if back.SignedPreKeyID != original.SignedPreKeyID {
		t.Fatalf("signed_pre_key_id: got %d want %d", back.SignedPreKeyID, original.SignedPreKeyID)
	}
	if !bytes.Equal(back.IdentityKey, original.IdentityKey) {
		t.Fatalf("identity_key mismatch")
	}
	if !bytes.Equal(back.SignedPreKeySignature, original.SignedPreKeySignature) {
		t.Fatalf("signed_pre_key_signature mismatch")
	}
}

// ─── Behaviour ────────────────────────────────────────────

func TestRequest_SendsDirectedPreKeyRequest_AndReturnsId(t *testing.T) {
	sender := newFakeSender("aether:alice:01")
	svc := NewService(sender)

	reqID, err := svc.RequestBundle(context.Background(), "aether:bob:02")
	if err != nil {
		t.Fatalf("request: %v", err)
	}
	if reqID == uuid.Nil {
		t.Fatalf("expected non-nil request id")
	}

	if len(sender.Sends) != 1 {
		t.Fatalf("expected 1 directed send, got %d", len(sender.Sends))
	}
	sent := sender.Sends[0]
	if sent.Packet.Type != protocol.PreKeyRequest {
		t.Fatalf("expected PreKeyRequest, got %v", sent.Packet.Type)
	}
	if sent.NextHop != "aether:bob:02" {
		t.Fatalf("expected next hop aether:bob:02, got %s", sent.NextHop)
	}
	if sent.Packet.DestinationUhid != "aether:bob:02" {
		t.Fatalf("expected dest aether:bob:02, got %s", sent.Packet.DestinationUhid)
	}
	if sent.Packet.Ttl != constants.DefaultTtl {
		t.Fatalf("expected ttl=DefaultTtl (%d), got %d", constants.DefaultTtl, sent.Packet.Ttl)
	}

	var body requestWire
	if err := json.Unmarshal(sent.Packet.Payload, &body); err != nil {
		t.Fatalf("unmarshal payload: %v", err)
	}
	if body.RequestID != reqID {
		t.Fatalf("expected request id %s, got %s", reqID, body.RequestID)
	}
	if body.RequesterUhid != "aether:alice:01" {
		t.Fatalf("expected requester aether:alice:01, got %s", body.RequesterUhid)
	}
}

func TestHandleRequest_WithLocalBundle_SendsDirectedResponseToRequester(t *testing.T) {
	sender := newFakeSender("aether:bob:02")
	svc := NewService(sender)
	svc.SetLocalBundle(sampleBundle("aether:bob:02"))

	reqID := uuid.New()
	body, err := json.Marshal(requestWire{RequestID: reqID, RequesterUhid: "aether:alice:01"})
	if err != nil {
		t.Fatalf("marshal request: %v", err)
	}
	reqPkt := protocol.NewMeshPacket()
	reqPkt.Type = protocol.PreKeyRequest
	reqPkt.SourceUhid = "aether:alice:01"
	reqPkt.DestinationUhid = "aether:bob:02"
	reqPkt.Payload = body

	ok, err := svc.Handle(context.Background(), reqPkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}

	if len(sender.Sends) != 1 {
		t.Fatalf("expected 1 directed send, got %d", len(sender.Sends))
	}
	sent := sender.Sends[0]
	if sent.Packet.Type != protocol.PreKeyResponse {
		t.Fatalf("expected PreKeyResponse, got %v", sent.Packet.Type)
	}
	if sent.NextHop != "aether:alice:01" {
		t.Fatalf("expected next hop aether:alice:01, got %s", sent.NextHop)
	}

	var resp responseWire
	if err := json.Unmarshal(sent.Packet.Payload, &resp); err != nil {
		t.Fatalf("unmarshal response: %v", err)
	}
	if resp.RequestID != reqID {
		t.Fatalf("expected request id %s, got %s", reqID, resp.RequestID)
	}
	if resp.Uhid != "aether:bob:02" {
		t.Fatalf("expected uhid aether:bob:02, got %s", resp.Uhid)
	}
	if resp.PreKeyID != 4242 {
		t.Fatalf("expected pre_key_id 4242, got %d", resp.PreKeyID)
	}
	if len(resp.SignedPreKeySignature) != 64 {
		t.Fatalf("expected 64-byte signature, got %d", len(resp.SignedPreKeySignature))
	}
}

func TestHandleRequest_NoLocalBundle_ReturnsFalse_AndSendsNothing(t *testing.T) {
	sender := newFakeSender("aether:local:01")
	svc := NewService(sender)

	body, err := json.Marshal(requestWire{RequestID: uuid.New(), RequesterUhid: "aether:alice:01"})
	if err != nil {
		t.Fatalf("marshal request: %v", err)
	}
	reqPkt := protocol.NewMeshPacket()
	reqPkt.Type = protocol.PreKeyRequest
	reqPkt.SourceUhid = "aether:alice:01"
	reqPkt.Payload = body

	ok, err := svc.Handle(context.Background(), reqPkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false when no local bundle set")
	}
	if len(sender.Sends) != 0 {
		t.Fatalf("expected no sends, got %d", len(sender.Sends))
	}
}

func TestHandleResponse_CachesBundle_AndRaisesEvent(t *testing.T) {
	sender := newFakeSender("aether:alice:01")
	svc := NewService(sender)
	var got *BundleReceived
	svc.OnBundleReceived = func(evt BundleReceived) { got = &evt }

	reqID := uuid.New()
	body, err := json.Marshal(responseFromBundle(reqID, sampleBundle("aether:bob:02")))
	if err != nil {
		t.Fatalf("marshal response: %v", err)
	}
	respPkt := protocol.NewMeshPacket()
	respPkt.Type = protocol.PreKeyResponse
	respPkt.SourceUhid = "aether:bob:02"
	respPkt.DestinationUhid = "aether:alice:01"
	respPkt.Payload = body

	ok, err := svc.Handle(context.Background(), respPkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if !ok {
		t.Fatalf("expected ok=true")
	}
	if got == nil {
		t.Fatalf("expected OnBundleReceived to fire")
	}
	if got.RequestId != reqID {
		t.Fatalf("expected request id %s, got %s", reqID, got.RequestId)
	}
	if got.FromUhid != "aether:bob:02" {
		t.Fatalf("expected from aether:bob:02, got %s", got.FromUhid)
	}
	if got.Bundle.Uhid != "aether:bob:02" {
		t.Fatalf("expected bundle uhid aether:bob:02, got %s", got.Bundle.Uhid)
	}

	cached, ok := svc.GetReceivedBundle("aether:bob:02")
	if !ok {
		t.Fatalf("expected cached bundle for aether:bob:02")
	}
	if cached.PreKeyID != 4242 {
		t.Fatalf("expected cached pre_key_id 4242, got %d", cached.PreKeyID)
	}
}

func TestHandle_WrongPacketType_ReturnsFalse(t *testing.T) {
	svc := NewService(newFakeSender("aether:local:01"))
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.Data
	pkt.SourceUhid = "aether:x:01"
	pkt.Payload = []byte{}

	ok, err := svc.Handle(context.Background(), pkt)
	if err != nil {
		t.Fatalf("handle: %v", err)
	}
	if ok {
		t.Fatalf("expected ok=false for wrong packet type")
	}
}

func TestHandle_NilPacket_ReturnsError(t *testing.T) {
	svc := NewService(newFakeSender("aether:local:01"))
	if _, err := svc.Handle(context.Background(), nil); err == nil {
		t.Fatalf("expected error for nil packet")
	}
}
