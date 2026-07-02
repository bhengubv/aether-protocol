// SPDX-License-Identifier: MIT

//! Directed peer-profile exchange for the Aether mesh (PacketType 23).

pub mod service;

pub use service::{ProfileService, ProfileSyncPayload};
