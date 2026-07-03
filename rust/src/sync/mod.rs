// SPDX-License-Identifier: MIT

//! Decentralised multi-device sync — no server.
//!
//! Three components, byte-identical to the C# reference and the shared fixture
//! (`fixtures/sync/vectors.json`):
//!
//! * [`record`] — the [`SyncRecord`] binary envelope: one E2E-encrypted state
//!   change a device gossips to a user's other devices.
//! * [`reconciler`] — deterministic last-write-wins: every device converges on
//!   the same winner per item, in any receive order, with no coordinator.
//! * [`device_link`] — a [`DeviceLink`] signed by the user's Ed25519 identity
//!   key that admits a new device into the "self" device set.

pub mod device_link;
pub mod reconciler;
pub mod record;

pub use device_link::{DeviceLink, DeviceLinkError};
pub use reconciler::{compare, merge, winner};
pub use record::{SyncOp, SyncRecord, SyncRecordError, FORMAT_VERSION};
