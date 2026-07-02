// SPDX-License-Identifier: MIT

//! Binary codec for the VoicePtt(15) + ScreenShare(32) media frames.
//!
//! Both frames share the exact **29-byte header** used by the existing
//! VoiceCall(16)/VideoFrame(31) frames, so a node can treat them uniformly:
//!
//! ```text
//! [0..15]  call_id       — 16 bytes, RFC-4122 BIG-ENDIAN (Uuid::as_bytes)
//! [16..19] sequence      — u32 LITTLE-ENDIAN
//! [20..27] timestamp_ms  — i64 LITTLE-ENDIAN
//! [28]     flag          — u8 (VoicePtt: is_silence; ScreenShare: is_keyframe)
//! [29..]   payload       — opaque encoded audio/video bytes
//! ```
//!
//! Byte-identity gate: `fixtures/media/vectors.json` (`expected_hex`). The
//! `call_id` is big-endian (network order) — `Uuid::as_bytes()` IS the
//! big-endian/RFC-4122 layout (same as the DTN bundle-id codec), NOT the .NET
//! mixed-endian `Guid.ToByteArray()` layout. Mirrors the C# `MediaFrameCodec`
//! and the Go / Python / TS / Kotlin / Swift ports.

use uuid::Uuid;

/// Shared media-frame header length in bytes (call_id 16 + sequence 4 +
/// timestamp 8 + flag 1). A frame with an empty payload is exactly this long.
pub const HEADER_LENGTH: usize = 29;

/// A push-to-talk audio frame ([`crate::protocol::PacketType::VoicePtt`] = 15 body).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct VoicePttFrame {
    pub call_id: Uuid,
    pub sequence: u32,
    pub timestamp_ms: i64,
    pub is_silence: bool,
    pub encoded_payload: Vec<u8>,
}

/// A screen-share video frame ([`crate::protocol::PacketType::ScreenShare`] = 32 body).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ScreenShareFrame {
    pub call_id: Uuid,
    pub sequence: u32,
    pub timestamp_ms: i64,
    pub is_keyframe: bool,
    pub encoded_payload: Vec<u8>,
}

/// Serialize a [`VoicePttFrame`] to canonical wire bytes.
pub fn serialize_voice_ptt(f: &VoicePttFrame) -> Vec<u8> {
    serialize(
        &f.call_id,
        f.sequence,
        f.timestamp_ms,
        f.is_silence,
        &f.encoded_payload,
    )
}

/// Serialize a [`ScreenShareFrame`] to canonical wire bytes.
pub fn serialize_screen_share(f: &ScreenShareFrame) -> Vec<u8> {
    serialize(
        &f.call_id,
        f.sequence,
        f.timestamp_ms,
        f.is_keyframe,
        &f.encoded_payload,
    )
}

fn serialize(call_id: &Uuid, sequence: u32, timestamp_ms: i64, flag: bool, payload: &[u8]) -> Vec<u8> {
    let mut buf = Vec::with_capacity(HEADER_LENGTH + payload.len());
    buf.extend_from_slice(call_id.as_bytes()); // 16 bytes, RFC-4122 big-endian
    buf.extend_from_slice(&sequence.to_le_bytes());
    buf.extend_from_slice(&timestamp_ms.to_le_bytes());
    buf.push(if flag { 1 } else { 0 });
    buf.extend_from_slice(payload);
    buf
}

/// Deserialize a [`VoicePttFrame`]. Returns `None` when the buffer is shorter
/// than the 29-byte header.
pub fn deserialize_voice_ptt(b: &[u8]) -> Option<VoicePttFrame> {
    let (call_id, sequence, timestamp_ms, flag, payload) = deserialize(b)?;
    Some(VoicePttFrame {
        call_id,
        sequence,
        timestamp_ms,
        is_silence: flag,
        encoded_payload: payload,
    })
}

/// Deserialize a [`ScreenShareFrame`]. Returns `None` when the buffer is shorter
/// than the 29-byte header.
pub fn deserialize_screen_share(b: &[u8]) -> Option<ScreenShareFrame> {
    let (call_id, sequence, timestamp_ms, flag, payload) = deserialize(b)?;
    Some(ScreenShareFrame {
        call_id,
        sequence,
        timestamp_ms,
        is_keyframe: flag,
        encoded_payload: payload,
    })
}

#[allow(clippy::type_complexity)]
fn deserialize(b: &[u8]) -> Option<(Uuid, u32, i64, bool, Vec<u8>)> {
    if b.len() < HEADER_LENGTH {
        return None;
    }
    let mut call_bytes = [0u8; 16];
    call_bytes.copy_from_slice(&b[0..16]);
    let call_id = Uuid::from_bytes(call_bytes);
    let sequence = u32::from_le_bytes(b[16..20].try_into().ok()?);
    let timestamp_ms = i64::from_le_bytes(b[20..28].try_into().ok()?);
    let flag = b[28] != 0;
    let payload = b[HEADER_LENGTH..].to_vec();
    Some((call_id, sequence, timestamp_ms, flag, payload))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hex(b: &[u8]) -> String {
        b.iter().map(|byte| format!("{byte:02x}")).collect()
    }

    const CALL_ID: &str = "0f7e5d3c-1a2b-4c5d-8e9f-0a1b2c3d4e5f";

    // ── Byte-identity gates — VoicePtt (fixtures/media/vectors.json) ─────────

    #[test]
    fn voice_ptt_frame_serializes_to_canonical_bytes() {
        let f = VoicePttFrame {
            call_id: Uuid::parse_str(CALL_ID).unwrap(),
            sequence: 42,
            timestamp_ms: 1_700_000_000_000,
            is_silence: false,
            encoded_payload: vec![0xAA, 0xBB, 0xCC],
        };
        assert_eq!(
            hex(&serialize_voice_ptt(&f)),
            "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2a0000000068e5cf8b01000000aabbcc"
        );
    }

    #[test]
    fn voice_ptt_silence_empty_serializes_to_canonical_bytes() {
        let f = VoicePttFrame {
            call_id: Uuid::parse_str(CALL_ID).unwrap(),
            sequence: 43,
            timestamp_ms: 1_700_000_000_020,
            is_silence: true,
            encoded_payload: vec![],
        };
        assert_eq!(
            hex(&serialize_voice_ptt(&f)),
            "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f2b0000001468e5cf8b01000001"
        );
    }

    // ── Byte-identity gates — ScreenShare (fixtures/media/vectors.json) ──────

    #[test]
    fn screen_share_keyframe_serializes_to_canonical_bytes() {
        let f = ScreenShareFrame {
            call_id: Uuid::parse_str(CALL_ID).unwrap(),
            sequence: 7,
            timestamp_ms: 1_700_000_000_000,
            is_keyframe: true,
            encoded_payload: vec![0x11, 0x22, 0x33, 0x44],
        };
        assert_eq!(
            hex(&serialize_screen_share(&f)),
            "0f7e5d3c1a2b4c5d8e9f0a1b2c3d4e5f070000000068e5cf8b0100000111223344"
        );
    }

    #[test]
    fn screen_share_delta_empty_serializes_to_canonical_bytes() {
        let f = ScreenShareFrame {
            call_id: Uuid::nil(),
            sequence: 0,
            timestamp_ms: 0,
            is_keyframe: false,
            encoded_payload: vec![],
        };
        assert_eq!(
            hex(&serialize_screen_share(&f)),
            "0000000000000000000000000000000000000000000000000000000000"
        );
    }

    // ── Round-trips ─────────────────────────────────────────────────────────

    #[test]
    fn voice_ptt_round_trips() {
        let f = VoicePttFrame {
            call_id: Uuid::parse_str(CALL_ID).unwrap(),
            sequence: 99,
            timestamp_ms: 123_456_789,
            is_silence: true,
            encoded_payload: vec![1, 2, 3, 4, 5],
        };
        let back = deserialize_voice_ptt(&serialize_voice_ptt(&f)).unwrap();
        assert_eq!(back.call_id, f.call_id);
        assert_eq!(back.sequence, 99);
        assert_eq!(back.timestamp_ms, 123_456_789);
        assert!(back.is_silence);
        assert_eq!(back.encoded_payload, f.encoded_payload);
    }

    #[test]
    fn screen_share_round_trips_keyframe_and_call_id_big_endian() {
        let f = ScreenShareFrame {
            call_id: Uuid::parse_str(CALL_ID).unwrap(),
            sequence: 5,
            timestamp_ms: 999,
            is_keyframe: true,
            encoded_payload: vec![0xFF],
        };
        let back = deserialize_screen_share(&serialize_screen_share(&f)).unwrap();
        assert_eq!(back.call_id, f.call_id);
        assert!(back.is_keyframe);
        assert_eq!(back.encoded_payload, vec![0xFF]);
    }

    #[test]
    fn deserialize_short_frame_returns_none() {
        assert!(deserialize_voice_ptt(&[0u8; 10]).is_none());
        assert!(deserialize_screen_share(&[0u8; 28]).is_none());
    }
}
