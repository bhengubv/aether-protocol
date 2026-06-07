// SPDX-License-Identifier: MIT

//! Extension seams hosts can wire up to participate in incentive accounting,
//! cloud-relay fallbacks, and feature gating. Default no-op implementations let
//! the protocol layer call through these uniformly.

use async_trait::async_trait;

use crate::models::{DtnBundle, SosAlert};
use crate::protocol::MeshPacket;

/// Records relays for reward calculation; decides whether a packet jumps the priority queue.
#[async_trait]
pub trait IncentiveProvider: Send + Sync {
    async fn record_relay(&self, _local_uhid: &str, _packet: &MeshPacket) {}
    async fn should_prioritize(&self, _packet: &MeshPacket) -> bool {
        false
    }

    /// Called when the local user tips a content author. Distinct from
    /// [`record_relay`] (relay credit — paid to nodes that forward bytes);
    /// this records direct creator → consumer settlement (paid to the user who
    /// AUTHORED the content). Host implementations (e.g. SDPKT, BhenguPay)
    /// wire their settlement logic here. Default no-op does nothing.
    /// Added in v1.2.0 — closes Issue #61 surfaced by Wave 16.
    async fn record_creator_tip(
        &self,
        _creator_uhid: &str,
        _amount: f64,
        _content_hash: &str,
    ) {
    }
}

/// Optional cloud-relay seam. Default returns false everywhere — fully offline mesh.
#[async_trait]
pub trait BackendClient: Send + Sync {
    async fn relay_message(
        &self,
        _sender_uhid: &str,
        _recipient_uhid: &str,
        _encrypted_content: &[u8],
        _priority: u8,
    ) -> bool {
        false
    }
    async fn sync_dtn_bundle(&self, _bundle: &DtnBundle) -> bool {
        false
    }
    async fn sync_sos(&self, _alert: &SosAlert) -> bool {
        false
    }
}

/// Gates protocol features behind remote configuration. Default: every feature enabled.
#[async_trait]
pub trait FeatureFlagProvider: Send + Sync {
    async fn is_enabled(&self, _feature_name: &str) -> bool {
        true
    }
}

/// Default no-op incentive provider.
pub struct NoopIncentiveProvider;

#[async_trait]
impl IncentiveProvider for NoopIncentiveProvider {}

/// Default no-op backend client — every method returns false.
pub struct NoopBackendClient;

#[async_trait]
impl BackendClient for NoopBackendClient {}

/// Default feature-flag provider — every flag enabled.
pub struct NoopFeatureFlagProvider;

#[async_trait]
impl FeatureFlagProvider for NoopFeatureFlagProvider {}
