// SPDX-License-Identifier: MIT

use crate::models::{BundlePriority, DtnBundle, PeerInfo};

/// Capability bit for DTN-carrier peers (mirrors `Capabilities::DTN_CARRIER`).
const CAP_DTN_CARRIER: u16 = 128;

/// Decides which connected peers should receive a copy of a bundle on the next
/// replication pass. Default `GeohashEpidemicStrategy` matches the C# reference.
pub trait ReplicationStrategy: Send + Sync {
    fn select_targets(
        &self,
        bundle: &DtnBundle,
        peers: &[PeerInfo],
        local_geohash: Option<&str>,
    ) -> Vec<String>;
}

/// Default geohash-aware epidemic strategy.
///
/// SOS bundles fan out to every eligible carrier up to the copy cap. Normal
/// bundles prefer peers whose geohash shares a longer prefix with the recipient's
/// last known geohash than the local node — i.e. peers at least as close to the
/// recipient. Ties broken by reliability score.
pub struct GeohashEpidemicStrategy;

impl ReplicationStrategy for GeohashEpidemicStrategy {
    fn select_targets(
        &self,
        bundle: &DtnBundle,
        peers: &[PeerInfo],
        local_geohash: Option<&str>,
    ) -> Vec<String> {
        let slots = (bundle.max_copies - bundle.copy_count).max(0) as usize;
        if slots == 0 {
            return Vec::new();
        }

        let eligible: Vec<&PeerInfo> = peers
            .iter()
            .filter(|p| {
                !p.uhid.is_empty()
                    && p.uhid != bundle.sender_uhid
                    && !p.is_blocked
                    && (p.capabilities & CAP_DTN_CARRIER) != 0
            })
            .collect();

        if eligible.is_empty() {
            return Vec::new();
        }

        if bundle.priority == BundlePriority::Sos {
            return eligible
                .into_iter()
                .take(slots)
                .map(|p| p.uhid.clone())
                .collect();
        }

        if let Some(recipient_geohash) = bundle.recipient_last_geohash.as_deref() {
            let local_prox = shared_prefix(local_geohash, recipient_geohash);
            let mut ranked: Vec<(usize, i32, &PeerInfo)> = eligible
                .iter()
                .map(|p| {
                    let prox = shared_prefix(p.geohash.as_deref(), recipient_geohash);
                    (prox, p.reliability_score, *p)
                })
                .filter(|(prox, _, _)| *prox >= local_prox)
                .collect();
            ranked.sort_by(|a, b| b.0.cmp(&a.0).then_with(|| b.1.cmp(&a.1)));
            return ranked
                .into_iter()
                .take(slots)
                .map(|(_, _, p)| p.uhid.clone())
                .collect();
        }

        let mut ranked: Vec<&PeerInfo> = eligible;
        ranked.sort_by(|a, b| b.reliability_score.cmp(&a.reliability_score));
        ranked
            .into_iter()
            .take(slots)
            .map(|p| p.uhid.clone())
            .collect()
    }
}

fn shared_prefix(a: Option<&str>, b: &str) -> usize {
    let a = match a {
        Some(s) if !s.is_empty() => s,
        _ => return 0,
    };
    let n = a.len().min(b.len());
    let mut i = 0;
    let a_bytes = a.as_bytes();
    let b_bytes = b.as_bytes();
    while i < n && a_bytes[i] == b_bytes[i] {
        i += 1;
    }
    i
}
