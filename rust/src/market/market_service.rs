// SPDX-License-Identifier: MIT

//! Offline-capable P2P marketplace (aether-market Phase-2 extension). Rust port of
//! `AetherNet.Market.IMarketService` / `InMemoryMarketService` and the listing/escrow
//! models. Listings are geo-pinned (distributed via aether-space) and may carry a
//! [`VaultManifest`] escrow for document-backed sales; trades run a two-party confirm
//! state machine. Requires aether-space and aether-vault.

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use uuid::Uuid;

use crate::market::PoVScore;
use crate::vault::VaultManifest;

/// Category of a [`MarketListing`].
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MarketCategory {
    Goods = 0,
    Services = 1,
    Labour = 2,
    Land = 3,
    Documents = 4,
}

/// Role of the node confirming a trade step.
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TradeRole {
    Buyer = 0,
    Seller = 1,
}

/// State machine for a [`TradeEscrow`].
#[repr(u8)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TradeState {
    Initiated = 0,
    BuyerConfirmed = 1,
    SellerConfirmed = 2,
    Complete = 3,
    Disputed = 4,
}

const THIRTY_DAYS_MS: i64 = 30 * 24 * 60 * 60 * 1000;

fn now_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

/// A geo-pinned market listing dropped by a verified seller. May include a [`VaultManifest`] escrow
/// for document-backed sales (land deeds, certificates).
#[derive(Debug, Clone)]
pub struct MarketListing {
    pub listing_id: String,
    pub seller_uhid: String,
    pub seller_pov_score: Option<PoVScore>,
    pub title: String,
    pub description: String,
    pub price_zar: f64, // South African Rand
    pub geohash: String,
    pub category: MarketCategory,
    pub escrow_manifest: Option<VaultManifest>,
    pub created_at_ms: i64,
    pub expires_at_ms: i64,
}

impl MarketListing {
    /// Whether the listing has reached its expiry.
    pub fn is_expired(&self) -> bool {
        now_ms() >= self.expires_at_ms
    }
}

/// Tracks the lifecycle of a marketplace trade.
#[derive(Debug, Clone)]
pub struct TradeEscrow {
    pub escrow_id: String,
    pub listing_id: String,
    pub buyer_uhid: String,
    pub seller_uhid: String,
    pub state: TradeState,
    pub vault_manifest: Option<VaultManifest>,
    pub created_at_ms: i64,
}

/// The offline-capable P2P marketplace.
pub trait MarketService {
    fn create_listing(&self, seller_uhid: &str, title: &str, description: &str, price_zar: f64,
                      geohash: &str, category: MarketCategory) -> MarketListing;
    fn browse_nearby(&self, center_geohash: &str, radius_cells: i32) -> Vec<MarketListing>;
    fn search(&self, query: &str, category: Option<MarketCategory>) -> Vec<MarketListing>;
    fn initiate_trade(&self, listing: &MarketListing, buyer_uhid: &str) -> TradeEscrow;
    fn confirm_trade(&self, escrow: &TradeEscrow, role: TradeRole) -> TradeEscrow;
    fn dispute(&self, escrow: &TradeEscrow, reason: &str) -> TradeEscrow;
}

type ListingCallback = Box<dyn Fn(&MarketListing) + Send + Sync>;

/// In-memory [`MarketService`] for testing / single-node use; state lost on restart.
#[derive(Default)]
pub struct InMemoryMarketService {
    listings: Mutex<HashMap<String, MarketListing>>,
    escrows: Mutex<HashMap<String, TradeEscrow>>,
    on_listing_received: Mutex<Option<ListingCallback>>,
}

impl InMemoryMarketService {
    /// Construct an empty in-memory market service.
    pub fn new() -> Self {
        Self::default()
    }

    /// Register a callback fired when a new listing is created locally / received from the mesh.
    pub fn set_on_listing_received<F: Fn(&MarketListing) + Send + Sync + 'static>(&self, cb: F) {
        *self.on_listing_received.lock().expect("market callback poisoned") = Some(Box::new(cb));
    }
}

impl MarketService for InMemoryMarketService {
    fn create_listing(&self, seller_uhid: &str, title: &str, description: &str, price_zar: f64,
                      geohash: &str, category: MarketCategory) -> MarketListing {
        let now = now_ms();
        let listing = MarketListing {
            listing_id: Uuid::new_v4().to_string(),
            seller_uhid: seller_uhid.to_string(),
            seller_pov_score: None,
            title: title.to_string(),
            description: description.to_string(),
            price_zar,
            geohash: geohash.to_string(),
            category,
            escrow_manifest: None,
            created_at_ms: now,
            expires_at_ms: now + THIRTY_DAYS_MS,
        };
        self.listings
            .lock()
            .expect("listings poisoned")
            .insert(listing.listing_id.clone(), listing.clone());

        if let Some(cb) = self.on_listing_received.lock().expect("callback poisoned").as_ref() {
            cb(&listing);
        }
        listing
    }

    fn browse_nearby(&self, center_geohash: &str, radius_cells: i32) -> Vec<MarketListing> {
        let len = center_geohash.chars().count() as i32;
        let prefix_len = (len - radius_cells + 1).max(1).min(len).max(0) as usize;
        let prefix: String = center_geohash.chars().take(prefix_len).collect::<String>().to_lowercase();

        self.listings
            .lock()
            .expect("listings poisoned")
            .values()
            .filter(|l| !l.is_expired() && l.geohash.to_lowercase().starts_with(&prefix))
            .cloned()
            .collect()
    }

    fn search(&self, query: &str, category: Option<MarketCategory>) -> Vec<MarketListing> {
        let q = query.to_lowercase();
        self.listings
            .lock()
            .expect("listings poisoned")
            .values()
            .filter(|l| {
                !l.is_expired()
                    && category.map_or(true, |c| l.category == c)
                    && (l.title.to_lowercase().contains(&q) || l.description.to_lowercase().contains(&q))
            })
            .cloned()
            .collect()
    }

    fn initiate_trade(&self, listing: &MarketListing, buyer_uhid: &str) -> TradeEscrow {
        let escrow = TradeEscrow {
            escrow_id: Uuid::new_v4().to_string(),
            listing_id: listing.listing_id.clone(),
            buyer_uhid: buyer_uhid.to_string(),
            seller_uhid: listing.seller_uhid.clone(),
            state: TradeState::Initiated,
            vault_manifest: listing.escrow_manifest.clone(),
            created_at_ms: now_ms(),
        };
        self.escrows
            .lock()
            .expect("escrows poisoned")
            .insert(escrow.escrow_id.clone(), escrow.clone());
        escrow
    }

    fn confirm_trade(&self, escrow: &TradeEscrow, role: TradeRole) -> TradeEscrow {
        let new_state = if role == TradeRole::Buyer {
            TradeState::BuyerConfirmed
        } else if escrow.state == TradeState::BuyerConfirmed {
            TradeState::Complete
        } else {
            TradeState::SellerConfirmed
        };
        let mut updated = escrow.clone();
        updated.state = new_state;
        self.escrows
            .lock()
            .expect("escrows poisoned")
            .insert(updated.escrow_id.clone(), updated.clone());
        updated
    }

    fn dispute(&self, escrow: &TradeEscrow, _reason: &str) -> TradeEscrow {
        let mut updated = escrow.clone();
        updated.state = TradeState::Disputed;
        self.escrows
            .lock()
            .expect("escrows poisoned")
            .insert(updated.escrow_id.clone(), updated.clone());
        updated
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::market::PoVTransportType;
    use crate::market::pov_service::{InMemoryPoVService, PoVService};

    #[test]
    fn marketplace_lifecycle() {
        let m = InMemoryMarketService::new();
        let l = m.create_listing("seller1", "Bicycle", "Red mountain bike", 1500.0, "k3vf9z", MarketCategory::Goods);
        assert!(!l.listing_id.is_empty());

        assert_eq!(m.browse_nearby("k3vf9z", 2).len(), 1);
        assert_eq!(m.browse_nearby("xxxxxx", 2).len(), 0);
        assert_eq!(m.search("bike", None).len(), 1);
        assert_eq!(m.search("bike", Some(MarketCategory::Services)).len(), 0);

        let e = m.initiate_trade(&l, "buyer1");
        assert_eq!(e.state, TradeState::Initiated);
        let e = m.confirm_trade(&e, TradeRole::Buyer);
        assert_eq!(e.state, TradeState::BuyerConfirmed);
        let e = m.confirm_trade(&e, TradeRole::Seller);
        assert_eq!(e.state, TradeState::Complete);

        let e2 = m.initiate_trade(&l, "buyer2");
        let e2 = m.dispute(&e2, "bad");
        assert_eq!(e2.state, TradeState::Disputed);
    }

    #[test]
    fn pov_score_and_defection() {
        let p = InMemoryPoVService::new();
        let tok = p.issue_token("w1", "A", PoVTransportType::Ble);
        assert!(p.verify_token(&tok));
        p.accept_token(&tok);

        let sc = p.get_score("A");
        assert_eq!(sc.unique_witnesses, 1);
        assert!((sc.weighted_score - 0.5).abs() < 1e-9);

        // Tampering invalidates the signatures.
        let mut bad = tok.clone();
        bad.subject_uhid = "C".to_string();
        assert!(!p.verify_token(&bad));

        // Self-vouch is rejected.
        let self_tok = p.issue_token("x", "x", PoVTransportType::Nfc);
        assert!(!p.verify_token(&self_tok));

        // Defection penalty: 0.5 -> 0.4.
        p.report_defection("A", "victim");
        assert!((p.get_score("A").weighted_score - 0.4).abs() < 1e-9);
    }
}
