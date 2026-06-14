// SPDX-License-Identifier: MIT

//! Incentive layer — generic, value-agnostic relay-tip envelopes carried over
//! [`crate::protocol::PacketType::TipPacket`] (24).
//!
//! Rust port of `AetherNet.Incentive` (and the Go `incentive` package). The
//! protocol carries the signal that one node wishes to credit another for some
//! relayed traffic; what (if anything) that signal is worth is entirely the
//! host's business, expressed through a [`MeshTipSettlementProvider`]. A bare
//! node (default [`NoopMeshTipSettlementProvider`]) accepts and relays tips but
//! settles nothing.
//!
//! See [`TipPacketPayload`] for the canonical, cross-language byte-identical
//! signable layout and [`MeshTipService`] for the send/receive/relay flow.

pub mod mesh_tip_service;
pub mod tip_packet_payload;

pub use mesh_tip_service::{
    IdentitySigner, MeshSender, MeshTipService, MeshTipSettlementProvider,
    NoopMeshTipSettlementProvider, PacketSigner, RouteResolver,
};
pub use tip_packet_payload::TipPacketPayload;
