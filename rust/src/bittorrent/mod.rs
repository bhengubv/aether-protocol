// SPDX-License-Identifier: MIT
//! AetherNet BitTorrent — a from-scratch, interoperable BitTorrent implementation
//! (BEP-3 and friends), byte-identical to every other AetherNet language SDK.

pub mod bencode;
pub mod dht;
pub mod extensions;
pub mod krpc;
pub mod logic;
pub mod merkle;
pub mod metainfo;
pub mod utp;
pub mod wire;
