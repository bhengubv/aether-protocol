// SPDX-License-Identifier: MIT
//! aether-space: geo-pinned community noticeboards (Phase-2 extension).
//!
//! Nodes drop breadcrumbs at geohash coordinates; passing devices auto-pull and
//! re-host them for other passersby — fully offline. Port of the C# reference
//! (AetherNet.Space). Wire format: JSON, transmitted as PacketType::SpaceBreadcrumb (40).

use std::collections::HashMap;
use std::time::{SystemTime, UNIX_EPOCH};

/// Category of a geo-pinned breadcrumb.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BreadcrumbType {
    Notice = 0,
    Emergency = 1,
    Commerce = 2,
    Event = 3,
    JobPosting = 4,
}

/// Fixed TTL applied to Emergency breadcrumbs.
pub const EMERGENCY_TTL_HOURS: i32 = 720;
/// Bounds for a non-emergency breadcrumb's lifetime (hours).
pub const MIN_TTL_HOURS: i32 = 1;
pub const MAX_TTL_HOURS: i32 = 168;

fn now_secs() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

/// A geo-pinned digital notice dropped at a physical location. Content is
/// addressed by hash; the breadcrumb carries only metadata.
#[derive(Clone, Debug)]
pub struct SpaceBreadcrumb {
    pub content_hash: String,
    pub geo_hash: String,
    pub anchor_uhid: String,
    /// Unix-epoch seconds of creation.
    pub created_at_secs: u64,
    pub ttl_hours: i32,
    pub crumb_type: BreadcrumbType,
    /// Ed25519 signature over (content_hash + geo_hash + created_at); empty if unsigned.
    pub signature: Vec<u8>,
}

impl SpaceBreadcrumb {
    /// Unix-epoch seconds of expiry = created_at_secs + ttl_hours.
    pub fn expires_at_secs(&self) -> u64 {
        self.created_at_secs + (self.ttl_hours.max(0) as u64) * 3600
    }

    /// True once the breadcrumb's TTL has passed.
    pub fn is_expired(&self) -> bool {
        now_secs() >= self.expires_at_secs()
    }
}

/// Optional callback fired on breadcrumb received / expired.
type Callback = Box<dyn Fn(&SpaceBreadcrumb)>;

/// In-memory aether-space breadcrumb store for testing / single-node use; state
/// is lost on drop. Proximity matching uses a geohash-prefix heuristic.
#[derive(Default)]
pub struct InMemorySpaceService {
    store: HashMap<String, SpaceBreadcrumb>, // key = content_hash
    pub on_breadcrumb_received: Option<Callback>,
    pub on_breadcrumb_expired: Option<Callback>,
}

impl InMemorySpaceService {
    pub fn new() -> Self {
        Self::default()
    }

    /// Create a new breadcrumb at `geo_hash`. `ttl_hours` is clamped to [1,168];
    /// Emergency breadcrumbs are fixed at 720 h.
    pub fn drop_crumb(
        &mut self,
        geo_hash: &str,
        content_hash: &str,
        anchor_uhid: &str,
        crumb_type: BreadcrumbType,
        ttl_hours: i32,
    ) -> SpaceBreadcrumb {
        let effective_ttl = if crumb_type == BreadcrumbType::Emergency {
            EMERGENCY_TTL_HOURS
        } else {
            ttl_hours.clamp(MIN_TTL_HOURS, MAX_TTL_HOURS)
        };
        let crumb = SpaceBreadcrumb {
            content_hash: content_hash.to_string(),
            geo_hash: geo_hash.to_string(),
            anchor_uhid: anchor_uhid.to_string(),
            created_at_secs: now_secs(),
            ttl_hours: effective_ttl,
            crumb_type,
            signature: Vec::new(),
        };
        self.store.insert(content_hash.to_string(), crumb.clone());
        if let Some(cb) = &self.on_breadcrumb_received {
            cb(&crumb);
        }
        crumb
    }

    /// Return active (non-expired) breadcrumbs near `center_geo_hash`.
    pub fn scan(&self, center_geo_hash: &str, radius_cells: i32) -> Vec<SpaceBreadcrumb> {
        // Prefix-based proximity: match the first (6 - radius_cells) chars.
        let prefix_len = (6 - radius_cells).clamp(1, 6) as usize;
        let prefix = if center_geo_hash.len() >= prefix_len {
            &center_geo_hash[..prefix_len]
        } else {
            center_geo_hash
        }
        .to_lowercase();
        self.store
            .values()
            .filter(|c| !c.is_expired() && c.geo_hash.to_lowercase().starts_with(&prefix))
            .cloned()
            .collect()
    }

    /// Cache and re-host a breadcrumb received from a peer.
    pub fn pin(&mut self, breadcrumb: SpaceBreadcrumb) {
        let key = breadcrumb.content_hash.clone();
        self.store.insert(key.clone(), breadcrumb);
        if let Some(cb) = &self.on_breadcrumb_received {
            cb(&self.store[&key]);
        }
    }

    /// Creator-only delete: succeeds only if `requestor_uhid` is the breadcrumb's anchor_uhid.
    pub fn delete(&mut self, breadcrumb: &SpaceBreadcrumb, requestor_uhid: &str) -> bool {
        match self.store.get(&breadcrumb.content_hash) {
            Some(stored) if stored.anchor_uhid == requestor_uhid => {
                self.store.remove(&breadcrumb.content_hash);
                true
            }
            _ => false,
        }
    }

    /// Drop every expired breadcrumb; returns the count removed.
    pub fn prune_expired(&mut self) -> i32 {
        let expired: Vec<SpaceBreadcrumb> =
            self.store.values().filter(|c| c.is_expired()).cloned().collect();
        for crumb in &expired {
            self.store.remove(&crumb.content_hash);
            if let Some(cb) = &self.on_breadcrumb_expired {
                cb(crumb);
            }
        }
        expired.len() as i32
    }
}
