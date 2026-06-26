// SPDX-License-Identifier: MIT
//! aether-forge: a mesh-native package cache proxy (Phase-2 extension).
//!
//! The first internet pull of a package is cached as Aether content; subsequent
//! pulls by anyone in the mesh are served locally at mesh speeds. Port of the C#
//! reference (AetherNet.Forge). Ecosystems: npm, pip, cargo, go, nuget, git.

use std::collections::HashMap;
use std::time::{SystemTime, UNIX_EPOCH};

fn now_secs() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

/// Metadata record for one cached package artifact. Package IDs use a namespaced
/// "ecosystem:name@version" format (e.g. "npm:react@18.2.0").
#[derive(Clone, Debug)]
pub struct ForgeEntry {
    pub content_hash: String,
    pub package_id: String,
    /// Unix-epoch seconds when first fetched and cached.
    pub fetched_at_secs: u64,
    pub size_bytes: i64,
    pub download_count: i32,
}

/// Aggregate statistics for the local Forge cache.
#[derive(Clone, Debug, Default)]
pub struct ForgeStats {
    pub total_bytes_saved: i64,
    pub total_peers_served: i32,
    pub catalogue_size: i32,
    /// Most-downloaded first, up to 10.
    pub top_packages: Vec<ForgeEntry>,
}

type Callback = Box<dyn Fn(&ForgeEntry)>;

/// In-memory mesh-native package cache for testing / single-node use; state is
/// lost on drop.
#[derive(Default)]
pub struct InMemoryForgeService {
    store: HashMap<String, ForgeEntry>, // key = package_id
    pub on_new_entry_announced: Option<Callback>,
}

impl InMemoryForgeService {
    pub fn new() -> Self {
        Self::default()
    }

    /// Look up a cached entry by package ID; `None` if not cached.
    pub fn query(&self, package_id: &str) -> Option<ForgeEntry> {
        self.store.get(package_id).cloned()
    }

    /// Store a new artifact (idempotent — an existing package_id is returned unchanged).
    pub fn cache(&mut self, package_id: &str, content_hash: &str, size_bytes: i64) -> ForgeEntry {
        if let Some(existing) = self.store.get(package_id) {
            return existing.clone(); // first-write-wins
        }
        let entry = ForgeEntry {
            content_hash: content_hash.to_string(),
            package_id: package_id.to_string(),
            fetched_at_secs: now_secs(),
            size_bytes,
            download_count: 0,
        };
        self.store.insert(package_id.to_string(), entry.clone());
        if let Some(cb) = &self.on_new_entry_announced {
            cb(&entry);
        }
        entry
    }

    /// Increment the download counter and return the entry, or `None` if not cached.
    pub fn fetch(&mut self, package_id: &str) -> Option<ForgeEntry> {
        let entry = self.store.get_mut(package_id)?;
        entry.download_count += 1;
        Some(entry.clone())
    }

    /// Current aggregate cache statistics.
    pub fn get_stats(&self) -> ForgeStats {
        let mut entries: Vec<ForgeEntry> = self.store.values().cloned().collect();
        let total_bytes_saved: i64 = entries
            .iter()
            .map(|e| e.download_count as i64 * e.size_bytes)
            .sum();
        entries.sort_by(|a, b| b.download_count.cmp(&a.download_count));
        let top_packages: Vec<ForgeEntry> = entries.iter().take(10).cloned().collect();
        ForgeStats {
            total_bytes_saved,
            total_peers_served: 0,
            catalogue_size: self.store.len() as i32,
            top_packages,
        }
    }
}
