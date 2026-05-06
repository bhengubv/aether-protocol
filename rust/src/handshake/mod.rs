// SPDX-License-Identifier: MIT

//! Capability handshake — Hello / HelloAck protocol-version + capability
//! negotiation. Mirror of `Aether.Core.Handshake.HandshakeService` from the
//! C# reference implementation.
//!
//! Two peers exchange a Hello (and a HelloAck reply) carrying their
//! supported protocol-version range and capability tags. The negotiation
//! locks in the highest mutually-supported version and the intersection of
//! advertised capabilities.
//!
//! The wire payload is a UTF-8 JSON document (snake_case to match the rest
//! of the Aether wire format) carried inside a [`crate::protocol::MeshPacket`]
//! whose [`crate::protocol::PacketType`] is [`crate::protocol::PacketType::Hello`]
//! or [`crate::protocol::PacketType::HelloAck`].

pub mod hello_payload;
pub mod service;

pub use hello_payload::{HelloPayload, IncompatiblePeer, IncompatibleReason, PeerCapabilities};
pub use service::{
    default_capabilities, HandshakeEvent, HandshakeService, DEFAULT_IMPLEMENTATION,
};
