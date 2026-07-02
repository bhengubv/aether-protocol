// SPDX-License-Identifier: MIT

//! ABMF WIRE bindings for the Aether mesh: `BandwidthProbe` (PacketType 53),
//! `BandwidthAck` (54) and `BandwidthGossip` (55).
//!
//! Ports the C# reference `src/AetherNet.Core/Bandwidth/BandwidthWireService.cs`.
//! The binary wire layouts are little-endian with NO version byte; the canonical
//! byte vectors live in `fixtures/bandwidth/vectors.json` (byte-identity gate).
//!
//! This module is deliberately named `bandwidth_wire` so it does not clash with
//! the existing [`crate::bandwidth`] module (the BBRv3 estimator + director). It
//! reuses that module's ABMF data types ([`crate::bandwidth::BandwidthProbeAck`],
//! [`crate::bandwidth::BandwidthGossipPayload`],
//! [`crate::bandwidth::BandwidthConfidence`]) and adds a small
//! [`BandwidthProbe`] request struct.

pub mod service;

pub use service::{
    BandwidthProbe, BandwidthProbeReceivedEvent, BandwidthWireCodec, BandwidthWireService,
};
