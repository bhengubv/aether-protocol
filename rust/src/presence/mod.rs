// SPDX-License-Identifier: MIT

//! Presence over [`crate::protocol::PacketType::PresenceBeacon`] (21) and
//! [`crate::protocol::PacketType::PresenceQuery`] (22) — a privacy-preserving
//! "I'm here" / "who's around here?" broadcast service.

pub mod service;

pub use service::{
    PresenceBeaconPayload, PresenceBeaconReceivedEvent, PresenceQueryPayload,
    PresenceQueryReceivedEvent, PresenceService,
};
