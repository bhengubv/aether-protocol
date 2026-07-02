// SPDX-License-Identifier: MIT

//! ERID-announce directed transport over [`crate::protocol::PacketType::EridAnnounce`]
//! (56) — thin carriage of an already-Signal-encrypted ERID announcement to an
//! established peer.

pub mod service;

pub use service::{EridAnnounceReceivedEvent, EridAnnounceService};
