// SPDX-License-Identifier: MIT

//! Directed binary media frames over [`crate::protocol::PacketType::VoicePtt`]
//! (15) and [`crate::protocol::PacketType::ScreenShare`] (32).
//!
//! Both frames share the exact 29-byte header used by the existing
//! VoiceCall(16)/VideoFrame(31) frames (call_id 16 B RFC-4122 big-endian,
//! sequence u32 LE, timestamp_ms i64 LE, flag u8, then opaque payload). See
//! [`codec`] for the wire format and [`service`] for the mesh bindings. Mirrors
//! the C# `AetherNet.Media` namespace and the Go / Python / TS / Kotlin / Swift
//! ports; byte-identity gated by `fixtures/media/vectors.json`.

pub mod codec;
pub mod service;

pub use codec::{
    deserialize_screen_share, deserialize_voice_ptt, serialize_screen_share, serialize_voice_ptt,
    ScreenShareFrame, VoicePttFrame, HEADER_LENGTH,
};
pub use service::{
    ScreenShareFrameReceivedEvent, ScreenShareService, VoicePttFrameReceivedEvent, VoicePttService,
};
