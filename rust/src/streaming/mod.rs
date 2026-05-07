// SPDX-License-Identifier: MIT

//! Streaming and watch-together services for the Aether mesh.

pub mod service;
pub mod video_service;
pub mod watch_together;

pub use service::{
    StreamAnnouncePayload, StreamSubscribePayload, StreamUnsubscribePayload, StreamingService,
};
pub use video_service::{VideoCallEntry, VideoCallService, VideoCallState, VideoSignalingMessage};
pub use watch_together::{WatchReactionPayload, WatchSession, WatchSyncPayload, WatchTogetherService};
