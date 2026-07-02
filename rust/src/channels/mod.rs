// SPDX-License-Identifier: MIT

//! Application-layer named-channel pub/sub for the Aether mesh (PacketType 7).

pub mod service;

pub use service::{ChannelMessageReceivedEvent, ChannelMessageService};
