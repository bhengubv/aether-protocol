// SPDX-License-Identifier: MIT

//! Directed video call-control (ring / accept / decline / hangup) for the Aether mesh (PacketType 27).

pub mod service;

pub use service::{VideoCallControlService, VideoCallStateChangedEvent};
