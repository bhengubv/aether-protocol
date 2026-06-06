// SPDX-License-Identifier: MIT
//! Integration tests for the GroupVoiceCallService.

#[path = "common.rs"]
mod common;

use std::sync::Arc;
use uuid::Uuid;

use aethermesh_protocol::{
    extensibility::NoopIncentiveProvider,
    protocol::{MeshPacket, PacketType},
    routing::{verifier::AcceptAllRouteReplyVerifier, InMemoryRouteStore, RoutingService},
    voice::{GroupVoiceCallService, GroupVoiceSignalingMessage},
};
use common::FakeMeshSender;

const LOCAL: &str = "alice";

async fn new_group_voice_svc() -> (GroupVoiceCallService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(LOCAL);
    let store = Arc::new(InMemoryRouteStore::new());
    let routing = Arc::new(RoutingService::with_dependencies(
        sender.clone(),
        store,
        Arc::new(AcceptAllRouteReplyVerifier),
        Arc::new(NoopIncentiveProvider),
    ));
    let svc = GroupVoiceCallService::new(sender.clone(), routing);
    (svc, sender)
}

/// Build a VoiceSignaling packet carrying a GroupVoiceSignalingMessage.
fn group_sig_pkt(from: &str, to: &str, sig: &GroupVoiceSignalingMessage) -> MeshPacket {
    let body = serde_json::to_vec(sig).expect("serialize group voice signaling");
    let mut pkt = MeshPacket::new(PacketType::VoiceSignaling, from.to_string());
    pkt.destination_uhid = to.to_string();
    pkt.payload = body;
    pkt
}

// ── invite ─────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn invite_sends_invite_signaling_to_each_member() {
    let (svc, sender) = new_group_voice_svc().await;
    let call_id = Uuid::new_v4();
    svc.invite(call_id, &["bob".to_string(), "carol".to_string()])
        .await
        .unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 2, "invite must send to each invitee");
    let targets: std::collections::HashSet<_> =
        unicasts.iter().map(|u| u.next_hop_uhid.as_str()).collect();
    assert!(targets.contains("bob"));
    assert!(targets.contains("carol"));
    for u in &unicasts {
        assert_eq!(u.packet.packet_type, PacketType::VoiceSignaling);
    }
}

#[tokio::test]
async fn invite_empty_members_returns_error() {
    let (svc, _) = new_group_voice_svc().await;
    let result = svc.invite(Uuid::new_v4(), &[]).await;
    assert!(result.is_err(), "empty member_uhids must return Err");
}

#[tokio::test]
async fn invite_payload_contains_kind_invite_and_invited_uhids() {
    let (svc, sender) = new_group_voice_svc().await;
    let call_id = Uuid::new_v4();
    svc.invite(call_id, &["bob".to_string()]).await.unwrap();

    let unicasts = sender.unicasts();
    let msg: GroupVoiceSignalingMessage =
        serde_json::from_slice(&unicasts[0].packet.payload).expect("parse group signaling");
    assert_eq!(msg.kind, "invite");
    assert_eq!(msg.call_id, call_id);
    assert_eq!(msg.invited_uhids, Some(vec!["bob".to_string()]));
}

// ── join ───────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn join_sends_join_signaling_to_existing_members() {
    let (svc, sender) = new_group_voice_svc().await;
    let call_id = Uuid::new_v4();
    svc.invite(call_id, &["bob".to_string()]).await.unwrap();
    sender.clear();

    svc.join(call_id).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1, "join must send to bob");
    assert_eq!(unicasts[0].next_hop_uhid, "bob");
    let msg: GroupVoiceSignalingMessage =
        serde_json::from_slice(&unicasts[0].packet.payload).unwrap();
    assert_eq!(msg.kind, "join");
}

#[tokio::test]
async fn join_unknown_call_returns_error() {
    let (svc, _) = new_group_voice_svc().await;
    let result = svc.join(Uuid::new_v4()).await;
    assert!(result.is_err(), "join on unknown call_id must fail");
}

// ── leave ──────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn leave_sends_leave_signaling_to_remaining_members() {
    let (svc, sender) = new_group_voice_svc().await;
    let call_id = Uuid::new_v4();
    svc.invite(call_id, &["bob".to_string()]).await.unwrap();
    sender.clear();

    svc.leave(call_id).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1, "leave must notify remaining member bob");
    assert_eq!(unicasts[0].next_hop_uhid, "bob");
    let msg: GroupVoiceSignalingMessage =
        serde_json::from_slice(&unicasts[0].packet.payload).unwrap();
    assert_eq!(msg.kind, "leave");
}

#[tokio::test]
async fn leave_unknown_call_returns_error() {
    let (svc, _) = new_group_voice_svc().await;
    let result = svc.leave(Uuid::new_v4()).await;
    assert!(result.is_err(), "leave on unknown call_id must fail");
}

// ── kick ───────────────────────────────────────────────────────────────────────

#[tokio::test]
async fn kick_sends_kick_to_remaining_members_not_kicked_person() {
    let (svc, sender) = new_group_voice_svc().await;
    let call_id = Uuid::new_v4();
    svc.invite(call_id, &["bob".to_string(), "carol".to_string()])
        .await
        .unwrap();
    sender.clear();

    svc.kick(call_id, "carol").await.unwrap();

    let unicasts = sender.unicasts();
    let targets: std::collections::HashSet<_> =
        unicasts.iter().map(|u| u.next_hop_uhid.as_str()).collect();
    assert!(targets.contains("bob"), "kick must notify remaining member bob");
    assert!(
        !targets.contains("carol"),
        "kick must NOT notify the kicked member carol"
    );
}

#[tokio::test]
async fn kick_payload_contains_kicked_uhid() {
    let (svc, sender) = new_group_voice_svc().await;
    let call_id = Uuid::new_v4();
    svc.invite(call_id, &["bob".to_string(), "carol".to_string()])
        .await
        .unwrap();
    sender.clear();

    svc.kick(call_id, "carol").await.unwrap();

    let unicasts = sender.unicasts();
    for u in &unicasts {
        let msg: GroupVoiceSignalingMessage =
            serde_json::from_slice(&u.packet.payload).unwrap();
        assert_eq!(msg.kind, "kick");
        assert_eq!(msg.kicked_uhid, Some("carol".to_string()));
    }
}

#[tokio::test]
async fn kick_non_host_returns_error() {
    let (svc, _) = new_group_voice_svc().await;
    let call_id = Uuid::new_v4();

    // Receive an inbound invite from bob — bob is the host, alice is not.
    let invite_sig = GroupVoiceSignalingMessage {
        kind: "invite".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        invited_uhids: Some(vec!["bob".into(), LOCAL.into()]),
        kicked_uhid: None,
        key_generation: None,
    };
    svc.handle_packet(&group_sig_pkt("bob", LOCAL, &invite_sig))
        .await
        .unwrap();

    // alice tries to kick bob — must fail because bob is the host.
    let result = svc.kick(call_id, "bob").await;
    assert!(result.is_err(), "non-host kick attempt must fail");
}

// ── send_frame ─────────────────────────────────────────────────────────────────

#[tokio::test]
async fn send_frame_sends_voice_call_to_members_except_self() {
    let (svc, sender) = new_group_voice_svc().await;
    let call_id = Uuid::new_v4();
    svc.invite(call_id, &["bob".to_string(), "carol".to_string()])
        .await
        .unwrap();
    sender.clear();

    svc.send_frame(call_id, &[0xAA, 0xBB], false, 0)
        .await
        .unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 2, "send_frame must reach both members");
    let targets: std::collections::HashSet<_> =
        unicasts.iter().map(|u| u.next_hop_uhid.as_str()).collect();
    assert!(targets.contains("bob"));
    assert!(targets.contains("carol"));
    assert!(!targets.contains(LOCAL), "send_frame must not send to self");
    for u in &unicasts {
        assert_eq!(u.packet.packet_type, PacketType::VoiceCall);
    }
}

#[tokio::test]
async fn send_frame_has_correct_wire_format() {
    let (svc, sender) = new_group_voice_svc().await;
    let call_id = Uuid::new_v4();
    svc.invite(call_id, &["bob".to_string()]).await.unwrap();
    sender.clear();

    let audio = [0xDE, 0xAD, 0xBE, 0xEF];
    svc.send_frame(call_id, &audio, true, 42).await.unwrap();

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1);
    // Wire: [16 callId][4 seq][8 ts][1 isSilence][4 keyGen][N audio] = 33-byte header
    assert!(
        unicasts[0].packet.payload.len() >= 33 + audio.len(),
        "payload too short"
    );
    // isSilence at offset 28
    assert_eq!(
        unicasts[0].packet.payload[28], 1u8,
        "is_silence flag must be 1"
    );
    // key_generation at offset 29..33 (LE u32)
    let key_gen = u32::from_le_bytes([
        unicasts[0].packet.payload[29],
        unicasts[0].packet.payload[30],
        unicasts[0].packet.payload[31],
        unicasts[0].packet.payload[32],
    ]);
    assert_eq!(key_gen, 42, "key_generation must be correctly encoded");
}

#[tokio::test]
async fn send_frame_unknown_call_returns_error() {
    let (svc, _) = new_group_voice_svc().await;
    let result = svc.send_frame(Uuid::new_v4(), &[0x01], false, 0).await;
    assert!(result.is_err(), "send_frame on unknown call_id must fail");
}

// ── handle_packet ──────────────────────────────────────────────────────────────

#[tokio::test]
async fn handle_packet_inbound_invite_registers_call_for_join() {
    let (svc, _) = new_group_voice_svc().await;
    let call_id = Uuid::new_v4();
    let invite_sig = GroupVoiceSignalingMessage {
        kind: "invite".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        invited_uhids: Some(vec!["bob".into(), LOCAL.into()]),
        kicked_uhid: None,
        key_generation: None,
    };
    svc.handle_packet(&group_sig_pkt("bob", LOCAL, &invite_sig))
        .await
        .unwrap();

    // join should succeed if the call was properly registered by the invite handler.
    svc.join(call_id).await.expect("join must succeed after receiving invite");
}

#[tokio::test]
async fn handle_packet_inbound_join_adds_member_for_future_sends() {
    let (svc, sender) = new_group_voice_svc().await;
    let call_id = Uuid::new_v4();
    svc.invite(call_id, &["bob".to_string()]).await.unwrap();

    // Carol sends a join signal — she should be added to the call's member set.
    let join_sig = GroupVoiceSignalingMessage {
        kind: "join".into(),
        call_id,
        from_uhid: "carol".into(),
        to_uhid: LOCAL.into(),
        invited_uhids: None,
        kicked_uhid: None,
        key_generation: None,
    };
    svc.handle_packet(&group_sig_pkt("carol", LOCAL, &join_sig))
        .await
        .unwrap();
    sender.clear();

    // Now leave — carol must receive the leave notification.
    svc.leave(call_id).await.unwrap();
    let targets: std::collections::HashSet<_> =
        sender.unicasts().iter().map(|u| u.next_hop_uhid.clone()).collect();
    assert!(
        targets.contains("carol"),
        "carol joined so she must receive leave notification"
    );
}

#[tokio::test]
async fn handle_packet_inbound_kick_of_self_deactivates_call() {
    let (svc, _) = new_group_voice_svc().await;
    let call_id = Uuid::new_v4();

    // Receive invite from bob so the call is registered.
    let invite_sig = GroupVoiceSignalingMessage {
        kind: "invite".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        invited_uhids: Some(vec!["bob".into(), LOCAL.into()]),
        kicked_uhid: None,
        key_generation: None,
    };
    svc.handle_packet(&group_sig_pkt("bob", LOCAL, &invite_sig))
        .await
        .unwrap();

    // Bob kicks alice (LOCAL).
    let kick_sig = GroupVoiceSignalingMessage {
        kind: "kick".into(),
        call_id,
        from_uhid: "bob".into(),
        to_uhid: LOCAL.into(),
        invited_uhids: None,
        kicked_uhid: Some(LOCAL.into()),
        key_generation: None,
    };
    svc.handle_packet(&group_sig_pkt("bob", LOCAL, &kick_sig))
        .await
        .unwrap();

    // Call is now inactive — send_frame must fail.
    let result = svc.send_frame(call_id, &[0xAA], false, 0).await;
    assert!(
        result.is_err(),
        "send_frame on kicked (inactive) call must fail"
    );
}
