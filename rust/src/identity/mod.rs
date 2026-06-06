// SPDX-License-Identifier: MIT

//! Identity primitives for the Aether mesh protocol.
//!
//! Currently exports:
//! - [`AetherNetTag`] — a human-readable, shareable address derived from a node's
//!   Ed25519 public key.

pub mod aethernet_tag;

pub use aethernet_tag::{AetherNetTag, AetherNetTagError};
