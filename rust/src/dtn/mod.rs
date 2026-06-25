// SPDX-License-Identifier: MIT

//! Delay-tolerant networking on top of the Aether mesh.

pub mod store;
pub mod strategy;
pub mod envelope;
pub mod service;

pub use store::{BundleStore, InMemoryBundleStore};
pub use strategy::{GeohashEpidemicStrategy, ReplicationStrategy};
pub use service::{DtnBundleReceivedEvent, DtnService};
