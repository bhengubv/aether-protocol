// SPDX-License-Identifier: MIT

package voice

import (
	"context"
	"encoding/json"
	"sync"
	"testing"

	"github.com/google/uuid"
	"github.com/bhengubv/aether-protocol/go/models"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

// ── fakeSender ────────────────────────────────────────────────────────────────

type unicastRecord struct {
	Packet      *protocol.MeshPacket
	NextHopUhid string
}

type fakeSender struct {
	mu         sync.Mutex
	uhid       string
	Broadcasts []*protocol.MeshPacket
	Unicasts   []unicastRecord
}

func newFakeSender(uhid string) *fakeSender { return &fakeSender{uhid: uhid} }

func (f *fakeSender) LocalUhid() string                 { return f.uhid }
func (f *fakeSender) LocalGeohash() string              { return "" }
func (f *fakeSender) ConnectedPeers() []models.PeerInfo { return nil }

func (f *fakeSender) Send(_ context.Context, packet *protocol.MeshPacket, nextHopUhid string) (bool, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.Unicasts = append(f.Unicasts, unicastRecord{Packet: &c, NextHopUhid: nextHopUhid})
	return true, nil
}

func (f *fakeSender) Broadcast(_ context.Context, packet *protocol.MeshPacket) (int, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	c := *packet
	c.Payload = append([]byte(nil), packet.Payload...)
	f.Broadcasts = append(f.Broadcasts, &c)
	return 1, nil
}

func (f *fakeSender) unicastsTo(uhid string) []*protocol.MeshPacket {
	f.mu.Lock()
	defer f.mu.Unlock()
	var out []*protocol.MeshPacket
	for _, r := range f.Unicasts {
		if r.NextHopUhid == uhid {
			out = append(out, r.Packet)
		}
	}
	return out
}

func (f *fakeSender) clearUnicasts() {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.Unicasts = nil
}

// ── Helpers ───────────────────────────────────────────────────────────────────

func buildSignalingPacket(t *testing.T, from, to string, msg VoiceSignalingMessage) *protocol.MeshPacket {
	t.Helper()
	payload, err := json.Marshal(msg)
	if err != nil {
		t.Fatalf("marshal signaling: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VoiceSignaling
	pkt.SourceUhid = from
	pkt.DestinationUhid = to
	pkt.Payload = payload
	return pkt
}

func buildGroupSignalingPacket(t *testing.T, from, to string, msg GroupVoiceSignalingMessage) *protocol.MeshPacket {
	t.Helper()
	payload, err := json.Marshal(msg)
	if err != nil {
		t.Fatalf("marshal group signaling: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VoiceSignaling
	pkt.SourceUhid = from
	pkt.DestinationUhid = to
	pkt.Payload = payload
	return pkt
}

func buildGroupVoiceCallPacket(t *testing.T, from string, callID uuid.UUID) *protocol.MeshPacket {
	t.Helper()
	// Build minimal valid group voice frame binary payload.
	payload, err := marshalGroupVoiceFrame(callID, 1, 12345678, false, 0, []byte{0xAA, 0xBB})
	if err != nil {
		t.Fatalf("marshal group voice frame: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VoiceCall
	pkt.SourceUhid = from
	pkt.Payload = payload
	return pkt
}

func buildVoiceCallPacket(t *testing.T, from string, callID uuid.UUID) *protocol.MeshPacket {
	t.Helper()
	payload, err := marshalVoiceFrame(callID, 1, 12345678, false, []byte{0xCC, 0xDD})
	if err != nil {
		t.Fatalf("marshal voice frame: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VoiceCall
	pkt.SourceUhid = from
	pkt.Payload = payload
	return pkt
}

// ── VoiceCallService — SendOffer ──────────────────────────────────────────────

func TestVoiceSendOffer_SendsSignalingToCallee(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVoiceCallService(sender)

	callID, err := svc.SendOffer(context.Background(), "bob", []string{"opus"}, 48000)
	if err != nil {
		t.Fatalf("SendOffer: %v", err)
	}
	if callID == uuid.Nil {
		t.Fatal("expected non-nil call ID")
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected VoiceSignaling unicast to bob")
	}
	if toBob[0].Type != protocol.VoiceSignaling {
		t.Errorf("expected VoiceSignaling, got %s", toBob[0].Type)
	}
	var msg VoiceSignalingMessage
	if err := json.Unmarshal(toBob[0].Payload, &msg); err != nil {
		t.Fatalf("unmarshal signaling: %v", err)
	}
	if msg.Kind != "offer" {
		t.Errorf("expected kind=offer, got %q", msg.Kind)
	}
	if msg.FromUhid != "alice" {
		t.Errorf("expected from=alice, got %q", msg.FromUhid)
	}
	if msg.ToUhid != "bob" {
		t.Errorf("expected to=bob, got %q", msg.ToUhid)
	}
}

func TestVoiceSendOffer_EmptyToUhid_ReturnsError(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVoiceCallService(sender)

	_, err := svc.SendOffer(context.Background(), "", []string{"opus"}, 48000)
	if err == nil {
		t.Fatal("expected error for empty toUhid")
	}
}

// ── VoiceCallService — HandlePacket / inbound signaling ───────────────────────

func TestVoiceHandlePacket_InboundOffer_FiresOnCallOffered(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVoiceCallService(sender)

	callID := uuid.New()
	var gotSession *VoiceCallSession
	svc.OnCallOffered = func(s *VoiceCallSession) { gotSession = s }

	offerMsg := VoiceSignalingMessage{
		Kind:           "offer",
		CallID:         callID.String(),
		FromUhid:       "bob",
		ToUhid:         "alice",
		ProposedCodecs: []string{"opus"},
		SampleRateHz:   48000,
	}
	pkt := buildSignalingPacket(t, "bob", "alice", offerMsg)
	if err := svc.HandlePacket(context.Background(), pkt); err != nil {
		t.Fatalf("HandlePacket: %v", err)
	}

	if gotSession == nil {
		t.Fatal("OnCallOffered was not fired")
	}
	if gotSession.State != VoiceCallStateRinging {
		t.Errorf("expected state=Ringing, got %v", gotSession.State)
	}
	if gotSession.PeerUhid != "bob" {
		t.Errorf("expected peer=bob, got %q", gotSession.PeerUhid)
	}
	if gotSession.CallID != callID {
		t.Errorf("expected callID=%v, got %v", callID, gotSession.CallID)
	}
}

func TestVoiceHandlePacket_InboundAnswer_FiresOnCallAccepted(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVoiceCallService(sender)

	// Alice sent an offer; now receives an answer.
	callID, _ := svc.SendOffer(context.Background(), "bob", []string{"opus"}, 48000)

	var gotSession *VoiceCallSession
	svc.OnCallAccepted = func(s *VoiceCallSession) { gotSession = s }

	answerMsg := VoiceSignalingMessage{
		Kind:     "answer",
		CallID:   callID.String(),
		FromUhid: "bob",
		ToUhid:   "alice",
	}
	pkt := buildSignalingPacket(t, "bob", "alice", answerMsg)
	if err := svc.HandlePacket(context.Background(), pkt); err != nil {
		t.Fatalf("HandlePacket answer: %v", err)
	}

	if gotSession == nil {
		t.Fatal("OnCallAccepted was not fired")
	}
	if gotSession.State != VoiceCallStateActive {
		t.Errorf("expected state=Active, got %v", gotSession.State)
	}
}

func TestVoiceHandlePacket_InboundHangup_FiresOnCallEnded(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVoiceCallService(sender)

	// Receive an inbound offer to create a session.
	callID := uuid.New()
	offerMsg := VoiceSignalingMessage{Kind: "offer", CallID: callID.String(), FromUhid: "bob", ToUhid: "alice"}
	_ = svc.HandlePacket(context.Background(), buildSignalingPacket(t, "bob", "alice", offerMsg))

	var gotReason string
	svc.OnCallEnded = func(_ *VoiceCallSession, reason string) { gotReason = reason }

	hangupMsg := VoiceSignalingMessage{Kind: "hangup", CallID: callID.String(), FromUhid: "bob", ToUhid: "alice"}
	if err := svc.HandlePacket(context.Background(), buildSignalingPacket(t, "bob", "alice", hangupMsg)); err != nil {
		t.Fatalf("HandlePacket hangup: %v", err)
	}

	if gotReason != "hangup" {
		t.Errorf("expected reason=hangup, got %q", gotReason)
	}
}

// ── VoiceCallService — AcceptCall ─────────────────────────────────────────────

func TestVoiceAcceptCall_SendsAnswerSignaling(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVoiceCallService(sender)

	// Receive inbound offer → Ringing.
	callID := uuid.New()
	offerMsg := VoiceSignalingMessage{Kind: "offer", CallID: callID.String(), FromUhid: "bob", ToUhid: "alice"}
	_ = svc.HandlePacket(context.Background(), buildSignalingPacket(t, "bob", "alice", offerMsg))

	sender.clearUnicasts()

	if err := svc.AcceptCall(context.Background(), callID); err != nil {
		t.Fatalf("AcceptCall: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected answer unicast to bob")
	}
	var msg VoiceSignalingMessage
	if err := json.Unmarshal(toBob[0].Payload, &msg); err != nil {
		t.Fatalf("unmarshal answer: %v", err)
	}
	if msg.Kind != "answer" {
		t.Errorf("expected kind=answer, got %q", msg.Kind)
	}
}

func TestVoiceAcceptCall_NotRinging_ReturnsError(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVoiceCallService(sender)

	// Outbound offer → Offering state, not Ringing.
	callID, _ := svc.SendOffer(context.Background(), "bob", nil, 48000)

	err := svc.AcceptCall(context.Background(), callID)
	if err == nil {
		t.Fatal("expected error accepting call not in ringing state")
	}
}

// ── VoiceCallService — HangUp ─────────────────────────────────────────────────

func TestVoiceHangUp_SendsHangupSignaling_AndFiresCallback(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVoiceCallService(sender)

	callID, _ := svc.SendOffer(context.Background(), "bob", nil, 48000)
	sender.clearUnicasts()

	var endedCalled bool
	svc.OnCallEnded = func(_ *VoiceCallSession, _ string) { endedCalled = true }

	if err := svc.HangUp(context.Background(), callID); err != nil {
		t.Fatalf("HangUp: %v", err)
	}

	if !endedCalled {
		t.Error("OnCallEnded was not fired")
	}
	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected hangup unicast to bob")
	}
	var msg VoiceSignalingMessage
	if err := json.Unmarshal(toBob[0].Payload, &msg); err != nil {
		t.Fatalf("unmarshal hangup: %v", err)
	}
	if msg.Kind != "hangup" {
		t.Errorf("expected kind=hangup, got %q", msg.Kind)
	}
}

// ── VoiceCallService — SendFrame ──────────────────────────────────────────────

func TestVoiceSendFrame_ActiveCall_SendsVoiceCallPacket(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVoiceCallService(sender)

	// Inbound offer → accept → Active.
	callID := uuid.New()
	offerMsg := VoiceSignalingMessage{Kind: "offer", CallID: callID.String(), FromUhid: "bob", ToUhid: "alice"}
	_ = svc.HandlePacket(context.Background(), buildSignalingPacket(t, "bob", "alice", offerMsg))
	_ = svc.AcceptCall(context.Background(), callID)
	sender.clearUnicasts()

	audio := []byte{0x01, 0x02, 0x03, 0x04}
	if err := svc.SendFrame(context.Background(), callID, audio, false); err != nil {
		t.Fatalf("SendFrame: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected VoiceCall unicast to bob")
	}
	if toBob[0].Type != protocol.VoiceCall {
		t.Errorf("expected VoiceCall, got %s", toBob[0].Type)
	}
	if len(toBob[0].Payload) == 0 {
		t.Error("expected non-empty frame payload")
	}
}

func TestVoiceSendFrame_OfferingState_ReturnsError(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVoiceCallService(sender)

	// Outbound offer → Offering state (not Active).
	callID, _ := svc.SendOffer(context.Background(), "bob", nil, 48000)

	err := svc.SendFrame(context.Background(), callID, []byte{1, 2, 3}, false)
	if err == nil {
		t.Fatal("expected error sending frame on non-active call")
	}
}

// ── VoiceCallService — HandlePacket / VoiceCall frame ─────────────────────────

func TestVoiceHandlePacket_InboundFrame_FiresOnFrameReceived(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVoiceCallService(sender)

	// Need an active session first.
	callID := uuid.New()
	offerMsg := VoiceSignalingMessage{Kind: "offer", CallID: callID.String(), FromUhid: "bob", ToUhid: "alice"}
	_ = svc.HandlePacket(context.Background(), buildSignalingPacket(t, "bob", "alice", offerMsg))
	_ = svc.AcceptCall(context.Background(), callID)

	var gotFrame *VoiceFrame
	svc.OnFrameReceived = func(_ *VoiceCallSession, f *VoiceFrame) { gotFrame = f }

	if err := svc.HandlePacket(context.Background(), buildVoiceCallPacket(t, "bob", callID)); err != nil {
		t.Fatalf("HandlePacket frame: %v", err)
	}
	if gotFrame == nil {
		t.Fatal("OnFrameReceived was not fired")
	}
	if gotFrame.CallID != callID {
		t.Errorf("expected callID=%v, got %v", callID, gotFrame.CallID)
	}
}

// ── GroupVoiceCallService — Invite ────────────────────────────────────────────

func TestGroupInvite_SendsInviteToEachMember(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewGroupVoiceCallService(sender)

	callID := uuid.New()
	if err := svc.Invite(context.Background(), callID, []string{"bob", "carol"}); err != nil {
		t.Fatalf("Invite: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	toCarol := sender.unicastsTo("carol")
	if len(toBob) == 0 {
		t.Error("expected invite unicast to bob")
	}
	if len(toCarol) == 0 {
		t.Error("expected invite unicast to carol")
	}

	var msg GroupVoiceSignalingMessage
	if err := json.Unmarshal(toBob[0].Payload, &msg); err != nil {
		t.Fatalf("unmarshal invite: %v", err)
	}
	if msg.Kind != "invite" {
		t.Errorf("expected kind=invite, got %q", msg.Kind)
	}
}

func TestGroupInvite_EmptyMembers_ReturnsError(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewGroupVoiceCallService(sender)

	err := svc.Invite(context.Background(), uuid.New(), nil)
	if err == nil {
		t.Fatal("expected error for empty memberUhids")
	}
}

// ── GroupVoiceCallService — HandlePacket / signaling ─────────────────────────

func TestGroupHandlePacket_InviteReceived_FiresCallback(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewGroupVoiceCallService(sender)

	callID := uuid.New()
	var gotFrom string
	svc.OnInviteReceived = func(_ *GroupVoiceCallSession, fromUhid string) { gotFrom = fromUhid }

	inviteMsg := GroupVoiceSignalingMessage{
		Kind:         "invite",
		CallID:       callID.String(),
		FromUhid:     "bob",
		ToUhid:       "alice",
		InvitedUhids: []string{"alice", "carol"},
	}
	pkt := buildGroupSignalingPacket(t, "bob", "alice", inviteMsg)
	if err := svc.HandlePacket(context.Background(), pkt); err != nil {
		t.Fatalf("HandlePacket invite: %v", err)
	}
	if gotFrom != "bob" {
		t.Errorf("expected from=bob, got %q", gotFrom)
	}
}

func TestGroupHandlePacket_Join_FiresOnMemberJoined(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewGroupVoiceCallService(sender)

	// Create session via Invite so getGroupSession works.
	callID := uuid.New()
	_ = svc.Invite(context.Background(), callID, []string{"bob"})

	var joinedUhid string
	svc.OnMemberJoined = func(_ *GroupVoiceCallSession, uhid string) { joinedUhid = uhid }

	joinMsg := GroupVoiceSignalingMessage{Kind: "join", CallID: callID.String(), FromUhid: "carol"}
	pkt := buildGroupSignalingPacket(t, "carol", "alice", joinMsg)
	if err := svc.HandlePacket(context.Background(), pkt); err != nil {
		t.Fatalf("HandlePacket join: %v", err)
	}
	if joinedUhid != "carol" {
		t.Errorf("expected joined=carol, got %q", joinedUhid)
	}
}

func TestGroupHandlePacket_Leave_FiresOnMemberLeft(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewGroupVoiceCallService(sender)

	callID := uuid.New()
	_ = svc.Invite(context.Background(), callID, []string{"bob", "carol"})

	var leftUhid string
	svc.OnMemberLeft = func(_ *GroupVoiceCallSession, uhid string) { leftUhid = uhid }

	leaveMsg := GroupVoiceSignalingMessage{Kind: "leave", CallID: callID.String(), FromUhid: "bob"}
	pkt := buildGroupSignalingPacket(t, "bob", "alice", leaveMsg)
	if err := svc.HandlePacket(context.Background(), pkt); err != nil {
		t.Fatalf("HandlePacket leave: %v", err)
	}
	if leftUhid != "bob" {
		t.Errorf("expected left=bob, got %q", leftUhid)
	}
}

func TestGroupHandlePacket_Kick_FiresOnMemberKicked(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewGroupVoiceCallService(sender)

	callID := uuid.New()
	_ = svc.Invite(context.Background(), callID, []string{"bob", "carol"})

	var kickedUhid string
	svc.OnMemberKicked = func(_ *GroupVoiceCallSession, uhid string) { kickedUhid = uhid }

	kickMsg := GroupVoiceSignalingMessage{
		Kind:       "kick",
		CallID:     callID.String(),
		FromUhid:   "alice",
		KickedUhid: "bob",
	}
	pkt := buildGroupSignalingPacket(t, "alice", "bob", kickMsg)
	if err := svc.HandlePacket(context.Background(), pkt); err != nil {
		t.Fatalf("HandlePacket kick: %v", err)
	}
	if kickedUhid != "bob" {
		t.Errorf("expected kicked=bob, got %q", kickedUhid)
	}
}

// ── GroupVoiceCallService — Leave ─────────────────────────────────────────────

func TestGroupLeave_SendsLeaveToMembers(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewGroupVoiceCallService(sender)

	callID := uuid.New()
	_ = svc.Invite(context.Background(), callID, []string{"bob"})
	sender.clearUnicasts()

	if err := svc.Leave(context.Background(), callID); err != nil {
		t.Fatalf("Leave: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected leave unicast to bob")
	}
	var msg GroupVoiceSignalingMessage
	if err := json.Unmarshal(toBob[0].Payload, &msg); err != nil {
		t.Fatalf("unmarshal leave: %v", err)
	}
	if msg.Kind != "leave" {
		t.Errorf("expected kind=leave, got %q", msg.Kind)
	}
}

// ── GroupVoiceCallService — SendFrame ─────────────────────────────────────────

func TestGroupSendFrame_FansOutToAllMembers(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewGroupVoiceCallService(sender)

	callID := uuid.New()
	_ = svc.Invite(context.Background(), callID, []string{"bob", "carol"})
	sender.clearUnicasts()

	if err := svc.SendFrame(context.Background(), callID, []byte{1, 2, 3}, false, 1); err != nil {
		t.Fatalf("SendFrame: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	toCarol := sender.unicastsTo("carol")
	if len(toBob) == 0 {
		t.Error("bob should receive group voice frame")
	}
	if len(toCarol) == 0 {
		t.Error("carol should receive group voice frame")
	}
	if len(toBob) > 0 && toBob[0].Type != protocol.VoiceCall {
		t.Errorf("expected VoiceCall, got %s", toBob[0].Type)
	}
}

// ── GroupVoiceCallService — HandlePacket / VoiceCall frame ───────────────────

func TestGroupHandlePacket_InboundFrame_FiresOnFrameReceived(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewGroupVoiceCallService(sender)

	callID := uuid.New()
	_ = svc.Invite(context.Background(), callID, []string{"bob"})

	var gotFrame *GroupVoiceFrame
	svc.OnFrameReceived = func(_ *GroupVoiceCallSession, f *GroupVoiceFrame) { gotFrame = f }

	pkt := buildGroupVoiceCallPacket(t, "bob", callID)
	if err := svc.HandlePacket(context.Background(), pkt); err != nil {
		t.Fatalf("HandlePacket group frame: %v", err)
	}
	if gotFrame == nil {
		t.Fatal("OnFrameReceived was not fired")
	}
	if gotFrame.CallID != callID {
		t.Errorf("expected callID=%v, got %v", callID, gotFrame.CallID)
	}
}
