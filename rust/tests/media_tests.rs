// SPDX-License-Identifier: MIT
//! Integration tests for the VoicePtt(15) + ScreenShare(32) media-frame mesh
//! bindings ([`VoicePttService`] / [`ScreenShareService`]).
//!
//! Mirrors the behaviour cases in the C# `MediaFrameTests`: directed send emits
//! a directed frame of the right packet type + inbound handle raises a
//! frame-received event carrying the source peer; wrong packet type → false;
//! short (`< 29`-byte) body → false. The frame byte-identity gates (against
//! `fixtures/media/vectors.json`) live in-lib (`src/media/codec.rs`).

#[path = "common.rs"]
mod common;

use std::sync::Arc;

use aethernet_protocol::{
    media::{ScreenShareFrame, ScreenShareService, VoicePttFrame, VoicePttService},
    protocol::{MeshPacket, PacketType},
};
use uuid::Uuid;

use common::FakeMeshSender;

const CALL_ID: &str = "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f";
const LOCAL: &str = "aether:local:01";

fn new_voice_ptt(local: &str) -> (VoicePttService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(local);
    let svc = VoicePttService::new(sender.clone());
    (svc, sender)
}

fn new_screen_share(local: &str) -> (ScreenShareService, Arc<FakeMeshSender>) {
    let sender = FakeMeshSender::new(local);
    let svc = ScreenShareService::new(sender.clone());
    (svc, sender)
}

// ─── VoicePtt(15) behaviour ─────────────────────────────────────────────────

#[tokio::test]
async fn voice_ptt_send_emits_directed_frame_and_handle_raises_event() {
    let (svc, sender) = new_voice_ptt("aether:alice:01");
    let frame = VoicePttFrame {
        call_id: Uuid::parse_str(CALL_ID).unwrap(),
        sequence: 42,
        timestamp_ms: 1_700_000_000_000,
        is_silence: false,
        encoded_payload: vec![0xAA, 0xBB, 0xCC],
    };

    assert!(svc.send_frame("aether:bob:02", &frame).await);

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1, "expected exactly one directed send");
    let mut sent = unicasts[0].clone();
    assert_eq!(sent.packet.packet_type, PacketType::VoicePtt);
    assert_eq!(sent.next_hop_uhid, "aether:bob:02");
    assert_eq!(sent.packet.destination_uhid, "aether:bob:02");

    let mut events = svc.subscribe_frame_received();
    sent.packet.source_uhid = "aether:alice:01".to_string();
    assert!(svc.handle(&sent.packet).await);

    let got = events.try_recv().expect("expected a frame-received event");
    assert_eq!(got.frame.sequence, 42);
    assert_eq!(got.from_uhid, "aether:alice:01");
    assert_eq!(got.frame.encoded_payload, vec![0xAA, 0xBB, 0xCC]);
}

// ─── ScreenShare(32) behaviour ──────────────────────────────────────────────

#[tokio::test]
async fn screen_share_send_emits_directed_frame_and_handle_raises_event() {
    let (svc, sender) = new_screen_share("aether:alice:01");
    let frame = ScreenShareFrame {
        call_id: Uuid::parse_str(CALL_ID).unwrap(),
        sequence: 7,
        timestamp_ms: 1_700_000_000_000,
        is_keyframe: true,
        encoded_payload: vec![0x11, 0x22, 0x33, 0x44],
    };

    assert!(svc.send_frame("aether:bob:02", &frame).await);

    let unicasts = sender.unicasts();
    assert_eq!(unicasts.len(), 1, "expected exactly one directed send");
    let sent = unicasts[0].clone();
    assert_eq!(sent.packet.packet_type, PacketType::ScreenShare);

    let mut events = svc.subscribe_frame_received();
    assert!(svc.handle(&sent.packet).await);

    let got = events.try_recv().expect("expected a frame-received event");
    assert!(got.frame.is_keyframe);
    assert_eq!(got.frame.sequence, 7);
}

// ─── Guards ─────────────────────────────────────────────────────────────────

#[tokio::test]
async fn handle_wrong_type_returns_false() {
    let (vp, _) = new_voice_ptt(LOCAL);
    let (ss, _) = new_screen_share(LOCAL);

    let mut wrong = MeshPacket::new(PacketType::Data, "aether:x:01".to_string());
    wrong.payload = vec![0u8; 40];
    assert!(!vp.handle(&wrong).await);
    assert!(!ss.handle(&wrong).await);
}

#[tokio::test]
async fn handle_short_frame_returns_false() {
    let (vp, _) = new_voice_ptt(LOCAL);

    let mut short = MeshPacket::new(PacketType::VoicePtt, "aether:x:01".to_string());
    short.payload = vec![0u8; 10];
    assert!(!vp.handle(&short).await);
}
