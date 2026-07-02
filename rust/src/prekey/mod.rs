// SPDX-License-Identifier: MIT

//! Directed mesh pre-key exchange (PacketType PreKeyRequest 25 / PreKeyResponse 26).
//!
//! Transport of a Signal [`PreKeyBundle`](crate::models::PreKeyBundle) over the
//! mesh so a peer can start X3DH while the responder is offline. No key
//! agreement happens here — the host feeds a received bundle to the Signal
//! service. Mirrors the C# `PreKeyExchangeService`.

pub mod service;

pub use service::{PreKeyBundleReceivedEvent, PreKeyExchangeService};
