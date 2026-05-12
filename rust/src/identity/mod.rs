// SPDX-License-Identifier: MIT

//! Identity primitives for the Aether mesh protocol.
//!
//! Currently exports:
//! - [`AetherTag`] — a human-readable, shareable address derived from a node's
//!   Ed25519 public key.

pub mod aether_tag;

pub use aether_tag::{AetherTag, AetherTagError};
