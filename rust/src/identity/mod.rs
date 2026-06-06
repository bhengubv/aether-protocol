// SPDX-License-Identifier: MIT

//! Identity primitives for the Aether mesh protocol.
//!
//! Currently exports:
//! - [`AetherMeshTag`] — a human-readable, shareable address derived from a node's
//!   Ed25519 public key.

pub mod aethermesh_tag;

pub use aethermesh_tag::{AetherMeshTag, AetherMeshTagError};
