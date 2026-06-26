// SPDX-License-Identifier: MIT

//! Market — on-mesh Proof-of-Vicinity (PoV) co-presence proofs carried over
//! [`crate::protocol::PacketType::PoVTokenExchange`] (43).
//!
//! Rust port of `AetherNet.Market` (and the Go `market` package). A witness
//! mints a token vouching that a subject was physically near it over a
//! short-range transport (BLE/NFC/NearLink), signs the canonical token body
//! with its real Ed25519 identity key, and sends it directed to the subject,
//! who counter-signs. The two signatures bind both parties so neither can forge
//! a co-presence claim unilaterally.
//!
//! The resulting [`PoVScore`] is a purely local anti-Sybil routing/identity
//! signal — it attaches NO value semantics and never touches any money/reward
//! layer.
//!
//! See [`PoVToken`] / [`build_signable_token_data`] for the canonical,
//! cross-language byte-identical signable layout and [`PoVTokenExchangeService`]
//! for the issue/accept/countersign flow.

pub mod market_service;
pub mod pov_exchange_service;
pub mod pov_service;
pub mod pov_token;

pub use market_service::{
    InMemoryMarketService, MarketCategory, MarketListing, MarketService, TradeEscrow, TradeRole,
    TradeState,
};
pub use pov_exchange_service::{
    IdentitySigner, MeshSender, PacketSigner, PoVTokenExchangeService,
};
pub use pov_service::{InMemoryPoVService, PoVService};
pub use pov_token::{
    build_signable_token_data, ticks_to_unix_ms, unix_ms_to_ticks, PoVScore, PoVToken,
    PoVTransportType,
};
