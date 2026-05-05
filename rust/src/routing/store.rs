// SPDX-License-Identifier: MIT

use async_trait::async_trait;
use std::collections::HashMap;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use crate::models::RouteEntry;

/// Persistent backing store for the routing table. Default impl is in-memory;
/// hosts substitute file- or database-backed stores for durability.
#[async_trait]
pub trait RouteStore: Send + Sync {
    async fn get(&self, destination_uhid: &str) -> Option<RouteEntry>;
    async fn get_all(&self) -> Vec<RouteEntry>;
    async fn save(&self, route: RouteEntry);
    async fn remove(&self, destination_uhid: &str);
    async fn prune_expired(&self) -> usize;
}

/// Process-local route store. Loses everything on restart.
pub struct InMemoryRouteStore {
    routes: Mutex<HashMap<String, RouteEntry>>,
}

impl Default for InMemoryRouteStore {
    fn default() -> Self {
        Self::new()
    }
}

impl InMemoryRouteStore {
    pub fn new() -> Self {
        Self {
            routes: Mutex::new(HashMap::new()),
        }
    }
}

#[async_trait]
impl RouteStore for InMemoryRouteStore {
    async fn get(&self, destination_uhid: &str) -> Option<RouteEntry> {
        self.routes.lock().unwrap().get(destination_uhid).cloned()
    }

    async fn get_all(&self) -> Vec<RouteEntry> {
        self.routes.lock().unwrap().values().cloned().collect()
    }

    async fn save(&self, route: RouteEntry) {
        self.routes
            .lock()
            .unwrap()
            .insert(route.destination_uhid.clone(), route);
    }

    async fn remove(&self, destination_uhid: &str) {
        self.routes.lock().unwrap().remove(destination_uhid);
    }

    async fn prune_expired(&self) -> usize {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_secs();
        let mut routes = self.routes.lock().unwrap();
        let before = routes.len();
        routes.retain(|_, r| r.expires_at > now);
        before - routes.len()
    }
}
