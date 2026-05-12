// SPDX-License-Identifier: MIT

package streaming

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/bhengubv/aether-protocol/go/protocol"
)

// ── helpers ───────────────────────────────────────────────────────────────────

func buildVideoSignalingPacket(t *testing.T, from, to string, msg VideoSignalingMessage) *protocol.MeshPacket {
	t.Helper()
	payload, err := json.Marshal(msg)
	if err != nil {
		t.Fatalf("marshal video signaling: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VideoSignaling
	pkt.SourceUhid = from
	pkt.DestinationUhid = to
	pkt.Payload = payload
	return pkt
}

func buildVideoFramePacket(t *testing.T, from string, callID uuid.UUID, isKeyframe bool) *protocol.MeshPacket {
	t.Helper()
	payload, err := marshalVideoFrame(callID, 1, time.Now().UnixMilli(), isKeyframe, []byte{0xDE, 0xAD})
	if err != nil {
		t.Fatalf("marshal video frame: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.VideoFrame
	pkt.SourceUhid = from
	pkt.Payload = payload
	return pkt
}

func buildWatchSyncPacket(t *testing.T, from, to string, payload WatchSyncPayload) *protocol.MeshPacket {
	t.Helper()
	body, err := json.Marshal(payload)
	if err != nil {
		t.Fatalf("marshal watch sync: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.WatchSync
	pkt.SourceUhid = from
	pkt.DestinationUhid = to
	pkt.Payload = body
	return pkt
}

func buildWatchReactionPacket(t *testing.T, from, to string, sessionID uuid.UUID, reaction string) *protocol.MeshPacket {
	t.Helper()
	body, err := json.Marshal(WatchReactionPayload{SessionID: sessionID.String(), Reaction: reaction})
	if err != nil {
		t.Fatalf("marshal watch reaction: %v", err)
	}
	pkt := protocol.NewMeshPacket()
	pkt.Type = protocol.WatchReaction
	pkt.SourceUhid = from
	pkt.DestinationUhid = to
	pkt.Payload = body
	return pkt
}

// ── VideoCallService — SendOffer ──────────────────────────────────────────────

func TestVideoSendOffer_ReturnsNonNilCallID(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVideoCallService(sender)

	callID, err := svc.SendOffer(context.Background(), "bob", []string{"h264"}, 1280, 720, 30, 1000)
	if err != nil {
		t.Fatalf("SendOffer: %v", err)
	}
	if callID == uuid.Nil {
		t.Fatal("expected non-nil call ID")
	}
}

func TestVideoSendOffer_EmptyToUhid_ReturnsError(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVideoCallService(sender)

	_, err := svc.SendOffer(context.Background(), "", []string{"h264"}, 1280, 720, 30, 1000)
	if err == nil {
		t.Fatal("expected error for empty toUhid")
	}
}

func TestVideoSendOffer_SendsVideoSignalingToPeer(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVideoCallService(sender)

	_, err := svc.SendOffer(context.Background(), "bob", []string{"h264"}, 1280, 720, 30, 1000)
	if err != nil {
		t.Fatalf("SendOffer: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected VideoSignaling unicast to bob")
	}
	if toBob[0].Type != protocol.VideoSignaling {
		t.Errorf("expected VideoSignaling, got %s", toBob[0].Type)
	}
	var msg VideoSignalingMessage
	if err := json.Unmarshal(toBob[0].Payload, &msg); err != nil {
		t.Fatalf("unmarshal signaling: %v", err)
	}
	if msg.Kind != "offer" {
		t.Errorf("expected kind=offer, got %q", msg.Kind)
	}
	if msg.ProposedCodecs[0] != "h264" {
		t.Errorf("expected codec h264, got %v", msg.ProposedCodecs)
	}
}

// ── VideoCallService — HandlePacket / inbound signaling ───────────────────────

func TestVideoHandlePacket_InboundOffer_FiresOnCallOffered(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVideoCallService(sender)

	callID := uuid.New()
	var gotSession *VideoCallSession
	svc.OnCallOffered = func(s *VideoCallSession) { gotSession = s }

	offerMsg := VideoSignalingMessage{
		Kind:           "offer",
		CallID:         callID.String(),
		FromUhid:       "bob",
		ToUhid:         "alice",
		ProposedCodecs: []string{"h264"},
		Width:          1280,
		Height:         720,
	}
	pkt := buildVideoSignalingPacket(t, "bob", "alice", offerMsg)
	if err := svc.HandlePacket(context.Background(), pkt); err != nil {
		t.Fatalf("HandlePacket offer: %v", err)
	}
	if gotSession == nil {
		t.Fatal("OnCallOffered was not fired")
	}
	if gotSession.State != VideoCallStateRinging {
		t.Errorf("expected state=Ringing, got %v", gotSession.State)
	}
	if gotSession.PeerUhid != "bob" {
		t.Errorf("expected peer=bob, got %q", gotSession.PeerUhid)
	}
}

func TestVideoHandlePacket_InboundAnswer_TransitionsToActive(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVideoCallService(sender)

	callID, _ := svc.SendOffer(context.Background(), "bob", []string{"h264"}, 1280, 720, 30, 1000)

	var gotSession *VideoCallSession
	svc.OnCallAccepted = func(s *VideoCallSession) { gotSession = s }

	answerMsg := VideoSignalingMessage{
		Kind:     "answer",
		CallID:   callID.String(),
		FromUhid: "bob",
		ToUhid:   "alice",
	}
	if err := svc.HandlePacket(context.Background(), buildVideoSignalingPacket(t, "bob", "alice", answerMsg)); err != nil {
		t.Fatalf("HandlePacket answer: %v", err)
	}
	if gotSession == nil {
		t.Fatal("OnCallAccepted was not fired")
	}
	if gotSession.State != VideoCallStateActive {
		t.Errorf("expected state=Active, got %v", gotSession.State)
	}
}

func TestVideoHandlePacket_InboundHangup_FiresOnCallEnded(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVideoCallService(sender)

	callID := uuid.New()
	offerMsg := VideoSignalingMessage{Kind: "offer", CallID: callID.String(), FromUhid: "bob", ToUhid: "alice"}
	_ = svc.HandlePacket(context.Background(), buildVideoSignalingPacket(t, "bob", "alice", offerMsg))

	var gotReason string
	svc.OnCallEnded = func(_ *VideoCallSession, reason string) { gotReason = reason }

	hangupMsg := VideoSignalingMessage{Kind: "hangup", CallID: callID.String(), FromUhid: "bob", ToUhid: "alice"}
	if err := svc.HandlePacket(context.Background(), buildVideoSignalingPacket(t, "bob", "alice", hangupMsg)); err != nil {
		t.Fatalf("HandlePacket hangup: %v", err)
	}
	if gotReason != "hangup" {
		t.Errorf("expected reason=hangup, got %q", gotReason)
	}
}

// ── VideoCallService — AcceptCall ─────────────────────────────────────────────

func TestVideoAcceptCall_SendsAnswerSignaling(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVideoCallService(sender)

	callID := uuid.New()
	offerMsg := VideoSignalingMessage{Kind: "offer", CallID: callID.String(), FromUhid: "bob", ToUhid: "alice"}
	_ = svc.HandlePacket(context.Background(), buildVideoSignalingPacket(t, "bob", "alice", offerMsg))
	sender.mu.Lock()
	sender.Unicasts = nil
	sender.mu.Unlock()

	if err := svc.AcceptCall(context.Background(), callID); err != nil {
		t.Fatalf("AcceptCall: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected answer unicast to bob")
	}
	var msg VideoSignalingMessage
	if err := json.Unmarshal(toBob[0].Payload, &msg); err != nil {
		t.Fatalf("unmarshal answer: %v", err)
	}
	if msg.Kind != "answer" {
		t.Errorf("expected kind=answer, got %q", msg.Kind)
	}
}

func TestVideoAcceptCall_NotRinging_ReturnsError(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVideoCallService(sender)

	callID, _ := svc.SendOffer(context.Background(), "bob", nil, 0, 0, 0, 0)
	err := svc.AcceptCall(context.Background(), callID)
	if err == nil {
		t.Fatal("expected error accepting call in non-ringing state")
	}
}

// ── VideoCallService — HangUp ─────────────────────────────────────────────────

func TestVideoHangUp_SendsHangupSignaling(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVideoCallService(sender)

	callID, _ := svc.SendOffer(context.Background(), "bob", []string{"h264"}, 1280, 720, 30, 1000)
	sender.mu.Lock()
	sender.Unicasts = nil
	sender.mu.Unlock()

	if err := svc.HangUp(context.Background(), callID); err != nil {
		t.Fatalf("HangUp: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected hangup unicast to bob")
	}
	var msg VideoSignalingMessage
	if err := json.Unmarshal(toBob[0].Payload, &msg); err != nil {
		t.Fatalf("unmarshal hangup: %v", err)
	}
	if msg.Kind != "hangup" {
		t.Errorf("expected kind=hangup, got %q", msg.Kind)
	}
}

// ── VideoCallService — SendFrame ──────────────────────────────────────────────

func TestVideoSendFrame_ActiveCall_SendsVideoFramePacket(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVideoCallService(sender)

	callID := uuid.New()
	offerMsg := VideoSignalingMessage{Kind: "offer", CallID: callID.String(), FromUhid: "bob", ToUhid: "alice"}
	_ = svc.HandlePacket(context.Background(), buildVideoSignalingPacket(t, "bob", "alice", offerMsg))
	_ = svc.AcceptCall(context.Background(), callID)
	sender.mu.Lock()
	sender.Unicasts = nil
	sender.mu.Unlock()

	video := []byte{0xDE, 0xAD, 0xBE, 0xEF}
	if err := svc.SendFrame(context.Background(), callID, video, true); err != nil {
		t.Fatalf("SendFrame: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected VideoFrame unicast to bob")
	}
	if toBob[0].Type != protocol.VideoFrame {
		t.Errorf("expected VideoFrame, got %s", toBob[0].Type)
	}
	// Wire: [16 callId][4 seq][8 ts][1 isKeyframe][N video] = 29-byte header
	if len(toBob[0].Payload) < 29+len(video) {
		t.Errorf("payload too short: %d bytes", len(toBob[0].Payload))
	}
	// isKeyframe byte at offset 28 must be 1
	if toBob[0].Payload[28] != 1 {
		t.Error("expected isKeyframe=1 at offset 28")
	}
}

func TestVideoSendFrame_NotActiveCall_ReturnsError(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVideoCallService(sender)

	callID, _ := svc.SendOffer(context.Background(), "bob", nil, 0, 0, 0, 0)
	// Still Offering — not Active.
	err := svc.SendFrame(context.Background(), callID, []byte{1, 2, 3}, false)
	if err == nil {
		t.Fatal("expected error sending frame on non-active call")
	}
}

// ── VideoCallService — RequestKeyframe / NotifyQualityChange ──────────────────

func TestVideoRequestKeyframe_SendsKeyframeRequestSignaling(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVideoCallService(sender)

	callID, _ := svc.SendOffer(context.Background(), "bob", []string{"h264"}, 1280, 720, 30, 1000)
	sender.mu.Lock()
	sender.Unicasts = nil
	sender.mu.Unlock()

	if err := svc.RequestKeyframe(context.Background(), callID); err != nil {
		t.Fatalf("RequestKeyframe: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected keyframe_request unicast to bob")
	}
	var msg VideoSignalingMessage
	_ = json.Unmarshal(toBob[0].Payload, &msg)
	if msg.Kind != "keyframe_request" {
		t.Errorf("expected kind=keyframe_request, got %q", msg.Kind)
	}
}

func TestVideoNotifyQualityChange_SendsQualityChangeSignaling(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVideoCallService(sender)

	callID, _ := svc.SendOffer(context.Background(), "bob", []string{"h264"}, 1280, 720, 30, 1000)
	sender.mu.Lock()
	sender.Unicasts = nil
	sender.mu.Unlock()

	if err := svc.NotifyQualityChange(context.Background(), callID, 640, 480, 15, 500); err != nil {
		t.Fatalf("NotifyQualityChange: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected quality_change unicast to bob")
	}
	var msg VideoSignalingMessage
	_ = json.Unmarshal(toBob[0].Payload, &msg)
	if msg.Kind != "quality_change" {
		t.Errorf("expected kind=quality_change, got %q", msg.Kind)
	}
	if msg.Width != 640 || msg.Height != 480 || msg.FPS != 15 || msg.BitrateKbps != 500 {
		t.Errorf("unexpected quality params: %dx%d %dfps %dkbps", msg.Width, msg.Height, msg.FPS, msg.BitrateKbps)
	}
}

// ── VideoCallService — HandlePacket / VideoFrame ──────────────────────────────

func TestVideoHandlePacket_InboundFrame_FiresOnFrameReceived(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewVideoCallService(sender)

	callID := uuid.New()
	offerMsg := VideoSignalingMessage{Kind: "offer", CallID: callID.String(), FromUhid: "bob", ToUhid: "alice"}
	_ = svc.HandlePacket(context.Background(), buildVideoSignalingPacket(t, "bob", "alice", offerMsg))
	_ = svc.AcceptCall(context.Background(), callID)

	var gotFrame *VideoFrameData
	svc.OnFrameReceived = func(_ *VideoCallSession, f *VideoFrameData) { gotFrame = f }

	if err := svc.HandlePacket(context.Background(), buildVideoFramePacket(t, "bob", callID, true)); err != nil {
		t.Fatalf("HandlePacket video frame: %v", err)
	}
	if gotFrame == nil {
		t.Fatal("OnFrameReceived was not fired")
	}
	if gotFrame.CallID != callID {
		t.Errorf("expected callID=%v, got %v", callID, gotFrame.CallID)
	}
	if !gotFrame.IsKeyframe {
		t.Error("expected IsKeyframe=true")
	}
}

// ── WatchTogetherService — InviteToSession ────────────────────────────────────

func TestWatchInviteToSession_SendsWatchSyncToEachMember(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewWatchTogetherService(sender)

	sid := uuid.New()
	if err := svc.InviteToSession(context.Background(), sid, "content-1", []string{"bob", "carol"}); err != nil {
		t.Fatalf("InviteToSession: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	toCarol := sender.unicastsTo("carol")
	if len(toBob) == 0 {
		t.Error("expected WatchSync unicast to bob")
	}
	if len(toCarol) == 0 {
		t.Error("expected WatchSync unicast to carol")
	}
	if toBob[0].Type != protocol.WatchSync {
		t.Errorf("expected WatchSync, got %s", toBob[0].Type)
	}
	var payload WatchSyncPayload
	_ = json.Unmarshal(toBob[0].Payload, &payload)
	if payload.Kind != "invite" {
		t.Errorf("expected kind=invite, got %q", payload.Kind)
	}
	if payload.ContentID != "content-1" {
		t.Errorf("expected contentID=content-1, got %q", payload.ContentID)
	}
}

func TestWatchInviteToSession_EmptyMembers_ReturnsError(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewWatchTogetherService(sender)

	err := svc.InviteToSession(context.Background(), uuid.New(), "content-1", nil)
	if err == nil {
		t.Fatal("expected error for empty memberUhids")
	}
}

// ── WatchTogetherService — Play / Pause / Seek / SetSpeed ─────────────────────

func TestWatchPlay_SendsWatchSyncWithKindPlay(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewWatchTogetherService(sender)

	sid := uuid.New()
	_ = svc.InviteToSession(context.Background(), sid, "c1", []string{"bob"})
	sender.mu.Lock()
	sender.Unicasts = nil
	sender.mu.Unlock()

	if err := svc.Play(context.Background(), sid, 5000); err != nil {
		t.Fatalf("Play: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected WatchSync unicast to bob")
	}
	if toBob[0].Type != protocol.WatchSync {
		t.Errorf("expected WatchSync, got %s", toBob[0].Type)
	}
	var payload WatchSyncPayload
	_ = json.Unmarshal(toBob[0].Payload, &payload)
	if payload.Kind != "play" {
		t.Errorf("expected kind=play, got %q", payload.Kind)
	}
	if payload.PositionMs != 5000 {
		t.Errorf("expected position=5000, got %d", payload.PositionMs)
	}
}

func TestWatchPlay_UnknownSession_ReturnsError(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewWatchTogetherService(sender)

	err := svc.Play(context.Background(), uuid.New(), 0)
	if err == nil {
		t.Fatal("expected error for unknown session")
	}
}

func TestWatchPause_SendsWatchSyncWithKindPause(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewWatchTogetherService(sender)

	sid := uuid.New()
	_ = svc.InviteToSession(context.Background(), sid, "c1", []string{"bob"})
	sender.mu.Lock()
	sender.Unicasts = nil
	sender.mu.Unlock()

	if err := svc.Pause(context.Background(), sid, 12000); err != nil {
		t.Fatalf("Pause: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected WatchSync unicast to bob on pause")
	}
	var payload WatchSyncPayload
	_ = json.Unmarshal(toBob[0].Payload, &payload)
	if payload.Kind != "pause" {
		t.Errorf("expected kind=pause, got %q", payload.Kind)
	}
	if payload.PositionMs != 12000 {
		t.Errorf("expected position=12000, got %d", payload.PositionMs)
	}
}

func TestWatchSeek_SendsWatchSyncWithPositionMs(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewWatchTogetherService(sender)

	sid := uuid.New()
	_ = svc.InviteToSession(context.Background(), sid, "c1", []string{"bob"})
	sender.mu.Lock()
	sender.Unicasts = nil
	sender.mu.Unlock()

	if err := svc.Seek(context.Background(), sid, 30000); err != nil {
		t.Fatalf("Seek: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected WatchSync unicast to bob on seek")
	}
	var payload WatchSyncPayload
	_ = json.Unmarshal(toBob[0].Payload, &payload)
	if payload.Kind != "seek" {
		t.Errorf("expected kind=seek, got %q", payload.Kind)
	}
	if payload.PositionMs != 30000 {
		t.Errorf("expected position=30000, got %d", payload.PositionMs)
	}
}

func TestWatchSetSpeed_SendsWatchSyncWithSpeed(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewWatchTogetherService(sender)

	sid := uuid.New()
	_ = svc.InviteToSession(context.Background(), sid, "c1", []string{"bob"})
	sender.mu.Lock()
	sender.Unicasts = nil
	sender.mu.Unlock()

	if err := svc.SetSpeed(context.Background(), sid, 1.5); err != nil {
		t.Fatalf("SetSpeed: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	if len(toBob) == 0 {
		t.Fatal("expected WatchSync unicast to bob on set_speed")
	}
	var payload WatchSyncPayload
	_ = json.Unmarshal(toBob[0].Payload, &payload)
	if payload.Kind != "speed" {
		t.Errorf("expected kind=speed, got %q", payload.Kind)
	}
	if payload.PlaybackSpeed != 1.5 {
		t.Errorf("expected speed=1.5, got %f", payload.PlaybackSpeed)
	}
}

// ── WatchTogetherService — SendReaction ───────────────────────────────────────

func TestWatchSendReaction_SendsWatchReactionToAllMembers(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewWatchTogetherService(sender)

	sid := uuid.New()
	_ = svc.InviteToSession(context.Background(), sid, "c1", []string{"bob", "carol"})
	sender.mu.Lock()
	sender.Unicasts = nil
	sender.mu.Unlock()

	if err := svc.SendReaction(context.Background(), sid, "🔥"); err != nil {
		t.Fatalf("SendReaction: %v", err)
	}

	toBob := sender.unicastsTo("bob")
	toCarol := sender.unicastsTo("carol")
	if len(toBob) == 0 {
		t.Error("bob should receive reaction")
	}
	if len(toCarol) == 0 {
		t.Error("carol should receive reaction")
	}
	if len(toBob) > 0 {
		if toBob[0].Type != protocol.WatchReaction {
			t.Errorf("expected WatchReaction, got %s", toBob[0].Type)
		}
		var rp WatchReactionPayload
		_ = json.Unmarshal(toBob[0].Payload, &rp)
		if rp.Reaction != "🔥" {
			t.Errorf("expected reaction=🔥, got %q", rp.Reaction)
		}
	}
	// Must not send to self
	toSelf := sender.unicastsTo("alice")
	if len(toSelf) > 0 {
		t.Error("reaction must not be sent to self")
	}
}

func TestWatchSendReaction_EmptyReaction_ReturnsError(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewWatchTogetherService(sender)

	sid := uuid.New()
	_ = svc.InviteToSession(context.Background(), sid, "c1", []string{"bob"})

	err := svc.SendReaction(context.Background(), sid, "")
	if err == nil {
		t.Fatal("expected error for empty reaction")
	}
}

func TestWatchSendReaction_UnknownSession_ReturnsError(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewWatchTogetherService(sender)

	err := svc.SendReaction(context.Background(), uuid.New(), "❤️")
	if err == nil {
		t.Fatal("expected error for unknown session")
	}
}

// ── WatchTogetherService — HandlePacket ───────────────────────────────────────

func TestWatchHandlePacket_InboundInvite_FiresOnInviteReceived(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewWatchTogetherService(sender)

	sid := uuid.New()
	var gotFrom string
	svc.OnInviteReceived = func(_ *WatchSession, fromUhid string) { gotFrom = fromUhid }

	invitePayload := WatchSyncPayload{
		SessionID: sid.String(),
		Kind:      "invite",
		ContentID: "movie-42",
		SentAtMs:  time.Now().UnixMilli(),
	}
	pkt := buildWatchSyncPacket(t, "bob", "alice", invitePayload)
	if err := svc.HandlePacket(context.Background(), pkt); err != nil {
		t.Fatalf("HandlePacket invite: %v", err)
	}
	if gotFrom != "bob" {
		t.Errorf("expected OnInviteReceived from=bob, got %q", gotFrom)
	}
}

func TestWatchHandlePacket_InboundPlay_FiresOnSyncReceived(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewWatchTogetherService(sender)

	sid := uuid.New()
	// Create session first.
	_ = svc.InviteToSession(context.Background(), sid, "c1", []string{"bob"})

	var gotPos int64
	svc.OnSyncReceived = func(_ *WatchSession, _ string, posMs int64, _ float64) { gotPos = posMs }

	now := time.Now().UnixMilli()
	playPayload := WatchSyncPayload{
		SessionID:     sid.String(),
		Kind:          "play",
		PositionMs:    10000,
		PlaybackSpeed: 1.0,
		SentAtMs:      now,
	}
	pkt := buildWatchSyncPacket(t, "bob", "alice", playPayload)
	if err := svc.HandlePacket(context.Background(), pkt); err != nil {
		t.Fatalf("HandlePacket play: %v", err)
	}
	// RTT compensation: compensated >= requested position.
	if gotPos < 10000 {
		t.Errorf("compensated position %d must be >= requested 10000", gotPos)
	}
}

func TestWatchHandlePacket_InboundReaction_FiresOnReaction(t *testing.T) {
	sender := newFakeSender("alice")
	svc := NewWatchTogetherService(sender)

	sid := uuid.New()
	_ = svc.InviteToSession(context.Background(), sid, "c1", []string{"bob"})

	var gotFrom, gotReaction string
	svc.OnReaction = func(_ *WatchSession, fromUhid, reaction string) {
		gotFrom = fromUhid
		gotReaction = reaction
	}

	pkt := buildWatchReactionPacket(t, "bob", "alice", sid, "❤️")
	if err := svc.HandlePacket(context.Background(), pkt); err != nil {
		t.Fatalf("HandlePacket reaction: %v", err)
	}
	if gotFrom != "bob" {
		t.Errorf("expected from=bob, got %q", gotFrom)
	}
	if gotReaction != "❤️" {
		t.Errorf("expected reaction=❤️, got %q", gotReaction)
	}
}
