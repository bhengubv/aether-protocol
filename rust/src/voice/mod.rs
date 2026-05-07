// SPDX-License-Identifier: MIT

//! Voice call services for the Aether mesh: 1-to-1 and group calls.

pub mod service;
pub mod group_service;

pub use service::{CallEntry, CallState, VoiceCallService, VoiceSignalingMessage};
pub use group_service::{GroupCallEntry, GroupVoiceCallService, GroupVoiceSignalingMessage};
