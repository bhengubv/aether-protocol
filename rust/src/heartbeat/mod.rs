// SPDX-License-Identifier: MIT

//! Heartbeat liveness beacons for the Aether mesh (PacketType 10).

pub mod service;

pub use service::{HeartbeatService, PeerLiveness, PeerSeenEvent};
