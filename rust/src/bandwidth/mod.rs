// SPDX-License-Identifier: MIT

//! AetherNet Bandwidth Measurement Framework (ABMF) — W18-5.
//!
//! Ports the C# reference implementation from
//! `src/AetherNet.Core/Bandwidth/` and `src/AetherNet.Transport/Bandwidth/`.
//!
//! # Architecture
//!
//! ```text
//! BandwidthEstimator  (per transport, BBRv3-inspired)
//!        │
//!        ▼
//! BandwidthDirector   (cross-transport matrix + gossip coordinator)
//!        │
//!        ▼
//! NodeActivityMonitor (UI-facing snapshot publisher)
//! ```
//!
//! ## Packet types added (protocol.rs)
//! - `BandwidthProbe  = 53`
//! - `BandwidthAck    = 54`
//! - `BandwidthGossip = 55`

pub mod director;
pub mod estimator;
pub mod models;
pub mod monitor;

pub use director::BandwidthDirector;
pub use estimator::BandwidthEstimator;
pub use models::{
    BandwidthConfidence, BandwidthGossipPayload, BandwidthProbeAck, BandwidthSample,
    NodeActivitySnapshot, NodeActivityState, TransportActivitySnapshot,
};
pub use monitor::NodeActivityMonitor;

#[cfg(test)]
mod tests;

#[cfg(test)]
mod fixture_tests;
