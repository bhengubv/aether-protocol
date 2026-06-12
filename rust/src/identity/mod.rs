// SPDX-License-Identifier: MIT

//! Identity primitives for the Aether mesh protocol.
//!
//! Currently exports:
//! - [`AetherNetTag`] — a human-readable, shareable address derived from a node's
//!   Ed25519 public key.
//! - [`ephemeral_routing_id`] — the rotating, key-derived wire address (ERID) that
//!   replaces the stable phone-derived UHID on the public wire.

pub mod aethernet_tag;
pub mod ephemeral_routing_id;

pub use aethernet_tag::{AetherNetTag, AetherNetTagError};
