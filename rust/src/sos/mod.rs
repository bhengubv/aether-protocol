// SPDX-License-Identifier: MIT

//! SOS broadcast origination and re-flooding for the Aether mesh.

pub mod service;

pub use service::{SosAcknowledgedEvent, SosBroadcastService};
