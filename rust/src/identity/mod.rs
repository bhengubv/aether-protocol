// SPDX-License-Identifier: MIT

//! Identity primitives for the Aether mesh protocol.
//!
//! Currently exports:
//! - [`AetherNetTag`] — a human-readable, shareable address derived from a node's
//!   Ed25519 public key.
//! - [`ephemeral_routing_id`] — the rotating, key-derived wire address (ERID) that
//!   replaces the stable phone-derived UHID on the public wire.
//! - [`EridDirectory`] — resolves a peer's rotating ERID to/from its stable UHID for
//!   established relationships (off-wire, shared inside the Signal session).
//! - [`erid_announcement_codec`] — frames the in-session message that shares a routing
//!   key with a peer.

pub mod aethernet_tag;
pub mod ephemeral_routing_id;
pub mod erid_announcement_codec;
pub mod erid_directory;

pub use aethernet_tag::{AetherNetTag, AetherNetTagError};
pub use ephemeral_routing_id::{
    derive, derive_for_epoch, derive_routing_key, epoch_for, EphemeralRoutingIdError,
    DEFAULT_EPOCH_SECONDS, DEFAULT_LENGTH,
};
pub use erid_announcement_codec::{EridAnnouncement, EridAnnouncementError};
pub use erid_directory::EridDirectory;
