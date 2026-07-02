// SPDX-License-Identifier: MIT

//! aether-forge announce WIRE binding for the Aether mesh (PacketType 41).

pub mod service;

pub use service::{ForgeAnnounceReceivedEvent, ForgeAnnounceService};
