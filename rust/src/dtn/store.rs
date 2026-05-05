// SPDX-License-Identifier: MIT

use async_trait::async_trait;
use std::collections::HashMap;
use std::sync::Mutex;
use uuid::Uuid;

use crate::models::{BundleStatus, CustodyRecord, DtnBundle};

/// Persistent backing store for DTN bundles + custody records.
#[async_trait]
pub trait BundleStore: Send + Sync {
    async fn get(&self, bundle_id: &Uuid) -> Option<DtnBundle>;
    async fn get_active(&self) -> Vec<DtnBundle>;
    async fn save(&self, bundle: DtnBundle);
    async fn remove(&self, bundle_id: &Uuid);
    async fn get_active_count(&self) -> usize;
    async fn save_custody(&self, record: CustodyRecord);
    async fn get_custody_records(&self, bundle_id: &Uuid) -> Vec<CustodyRecord>;
    async fn expire_stale(&self) -> usize;
}

/// Process-local DTN store. Suitable for tests.
pub struct InMemoryBundleStore {
    bundles: Mutex<HashMap<Uuid, DtnBundle>>,
    custody: Mutex<HashMap<Uuid, CustodyRecord>>,
}

impl Default for InMemoryBundleStore {
    fn default() -> Self {
        Self::new()
    }
}

impl InMemoryBundleStore {
    pub fn new() -> Self {
        Self {
            bundles: Mutex::new(HashMap::new()),
            custody: Mutex::new(HashMap::new()),
        }
    }
}

#[async_trait]
impl BundleStore for InMemoryBundleStore {
    async fn get(&self, bundle_id: &Uuid) -> Option<DtnBundle> {
        self.bundles.lock().unwrap().get(bundle_id).cloned()
    }

    async fn get_active(&self) -> Vec<DtnBundle> {
        self.bundles
            .lock()
            .unwrap()
            .values()
            .filter(|b| {
                !b.is_expired()
                    && (b.status == BundleStatus::Pending || b.status == BundleStatus::InCustody)
            })
            .cloned()
            .collect()
    }

    async fn save(&self, bundle: DtnBundle) {
        self.bundles.lock().unwrap().insert(bundle.id, bundle);
    }

    async fn remove(&self, bundle_id: &Uuid) {
        self.bundles.lock().unwrap().remove(bundle_id);
    }

    async fn get_active_count(&self) -> usize {
        self.bundles
            .lock()
            .unwrap()
            .values()
            .filter(|b| {
                !b.is_expired()
                    && (b.status == BundleStatus::Pending || b.status == BundleStatus::InCustody)
            })
            .count()
    }

    async fn save_custody(&self, record: CustodyRecord) {
        self.custody.lock().unwrap().insert(record.id, record);
    }

    async fn get_custody_records(&self, bundle_id: &Uuid) -> Vec<CustodyRecord> {
        self.custody
            .lock()
            .unwrap()
            .values()
            .filter(|r| r.bundle_id == *bundle_id)
            .cloned()
            .collect()
    }

    async fn expire_stale(&self) -> usize {
        let mut bundles = self.bundles.lock().unwrap();
        let mut expired = 0;
        for b in bundles.values_mut() {
            if b.is_expired() && b.status != BundleStatus::Expired {
                b.status = BundleStatus::Expired;
                expired += 1;
            }
        }
        expired
    }
}
