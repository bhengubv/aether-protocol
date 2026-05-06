// SPDX-License-Identifier: MIT

//! Wire payload carried inside a [`crate::protocol::PacketType::Hello`] or
//! [`crate::protocol::PacketType::HelloAck`] packet's `MeshPacket::payload`.
//!
//! JSON shape (snake_case, matching the rest of the Aether wire format):
//!
//! ```json
//! {
//!   "min_version": 1,
//!   "max_version": 2,
//!   "capabilities": ["signal-x3dh", "double-ratchet", "dtn-custody"],
//!   "implementation": "aether-rust/0.1.0"
//! }
//! ```
//!
//! Security note: this payload is NEITHER encrypted NOR authenticated by
//! design — the handshake runs before any Signal session exists. Peer
//! identity is verified later via Ed25519 packet signatures on the data
//! packets the peer subsequently sends. Treat the announced capabilities as
//! a hint, not as a security claim.

use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::time::SystemTime;

/// Wire-form Hello / HelloAck payload — serde-serialised with snake_case
/// field names so the JSON output matches the C# `HelloPayload` exactly.
///
/// Serialised via `serde_json::to_vec` and stuffed into `MeshPacket::payload`.
/// The bytes are UTF-8 JSON; receivers `serde_json::from_slice` it back.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct HelloPayload {
    /// Lowest protocol version the announcer can speak.
    #[serde(rename = "min_version")]
    pub min_version: u8,

    /// Highest protocol version the announcer can speak.
    #[serde(rename = "max_version")]
    pub max_version: u8,

    /// Capability tags advertised by the announcer. Order is not significant;
    /// duplicates are tolerated but discouraged.
    #[serde(rename = "capabilities", default)]
    pub capabilities: Vec<String>,

    /// Free-form implementation banner (e.g. `"aether-rust/0.1.0"`).
    /// Diagnostic only; not used for compatibility decisions.
    #[serde(rename = "implementation", default)]
    pub implementation: String,
}

impl HelloPayload {
    pub fn new(
        min_version: u8,
        max_version: u8,
        capabilities: Vec<String>,
        implementation: String,
    ) -> Self {
        HelloPayload {
            min_version,
            max_version,
            capabilities,
            implementation,
        }
    }
}

/// The negotiated protocol-version + capability set for a remote peer, locked
/// in once the Hello/HelloAck exchange completes (or after the backward-compat
/// timeout for peers that never replied).
///
/// `negotiated_version` is the highest protocol version both sides advertised
/// support for. `capabilities` is the intersection of both sides' advertised
/// capability tags — services should gate optional features (Double Ratchet,
/// DTN custody, voice, etc.) on capability presence rather than on raw
/// protocol-version.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PeerCapabilities {
    /// UHID of the peer this record describes.
    pub peer_uhid: String,
    /// Highest mutually-supported protocol version. Defaults to `1` for peers
    /// that never replied with a HelloAck (backward-compat).
    pub negotiated_version: u8,
    /// Intersection of capability tags both sides claim to support. Empty for
    /// peers that never replied.
    pub capabilities: HashSet<String>,
    /// Free-form implementation banner the peer announced. Empty for peers
    /// that never replied.
    pub implementation_version: String,
    /// UTC timestamp when negotiation completed.
    pub negotiated_at: SystemTime,
}

/// Reasons a remote Hello/HelloAck couldn't be reconciled with the local
/// capability advertisement.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum IncompatibleReason {
    /// The peer announced `min_version > max_version`.
    InvertedVersionRange,
    /// `min(ourMax, theirMax) < max(ourMin, theirMin)` — version ranges
    /// don't overlap, no mutually-supported version exists.
    NoVersionOverlap,
}

impl IncompatibleReason {
    pub fn as_str(&self) -> &'static str {
        match self {
            IncompatibleReason::InvertedVersionRange => "inverted version range",
            IncompatibleReason::NoVersionOverlap => "no version overlap",
        }
    }
}

/// Event emitted when a peer's announced range can't be reconciled with our
/// own. Mirrors C# `IncompatiblePeerEventArgs`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct IncompatiblePeer {
    pub peer_uhid: String,
    pub their_min_version: u8,
    pub their_max_version: u8,
    pub our_min_version: u8,
    pub our_max_version: u8,
    pub reason: IncompatibleReason,
}
