// SPDX-License-Identifier: MIT

//! aether-vault shard-request WIRE binding for the Aether mesh (PacketType 42).

pub mod service;

pub use service::{VaultShardRequestReceivedEvent, VaultShardRequestService};
