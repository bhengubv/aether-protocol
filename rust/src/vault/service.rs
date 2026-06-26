// SPDX-License-Identifier: MIT

//! In-memory aether-vault service (Phase-2 extension): erasure-coded distributed
//! backup over this module's [`ReedSolomonCodec`]. Port of the C# reference
//! (`AetherNet.Vault.InMemoryVaultService`) — K=10 / M=4, shard layout
//! byte-identical so a shard set produced here is decodable by any other node.

use std::collections::BTreeMap;
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};

use sha2::{Digest, Sha256};

use crate::vault::{ReedSolomonCodec, VaultError};

/// Data shards in the default vault scheme.
pub const VAULT_K: usize = 10;
/// Parity shards in the default vault scheme.
pub const VAULT_M: usize = 4;

/// The only thing the owner must retain to reconstruct a vaulted file.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VaultManifest {
    /// SHA-256 hex of the plaintext.
    pub content_hash: String,
    /// SHA-256 hex of each of the K+M shards, in shard-index order.
    pub shard_hashes: Vec<String>,
    /// Data shards (default 10).
    pub k: usize,
    /// Parity shards (default 4).
    pub m: usize,
    /// Original plaintext size in bytes.
    pub size_bytes: u64,
    /// Caller-supplied label.
    pub label: String,
    /// Creation time, milliseconds since the Unix epoch (UTC).
    pub created_at_ms: i64,
}

impl VaultManifest {
    /// Total shards for this manifest (K + M).
    pub fn total_shards(&self) -> usize {
        self.k + self.m
    }
}

/// A current reachability report for a vaulted file.
#[derive(Debug, Clone, PartialEq)]
pub struct VaultHealth {
    /// K + M.
    pub total_shards: usize,
    /// How many of the manifest's shards are currently reachable.
    pub reachable_shards: usize,
    /// Whether at least K shards are reachable.
    pub is_recoverable: bool,
    /// reachable / total in [0, 1].
    pub redundancy_score: f64,
}

/// The aether-vault erasure-coded backup store.
pub trait VaultService {
    /// Erasure-code `data`, persist the shards, and return the manifest the owner must keep.
    fn store(&self, data: &[u8], label: &str) -> Result<VaultManifest, VaultError>;
    /// Reconstruct the original file from any K available shards.
    fn recover(&self, manifest: &VaultManifest) -> Result<Vec<u8>, VaultError>;
    /// Report how many shards are reachable and whether recovery is possible.
    fn check_health(&self, manifest: &VaultManifest) -> VaultHealth;
    /// Re-replicate shards toward `target_redundancy` (no-op in the in-memory implementation).
    fn replicate(&self, manifest: &VaultManifest, target_redundancy: usize)
        -> Result<(), VaultError>;
}

fn sha256_hex(data: &[u8]) -> String {
    Sha256::digest(data).iter().map(|b| format!("{:02x}", b)).collect()
}

fn now_unix_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

/// In-memory [`VaultService`] for testing / single-node use; shards live in a
/// hash-keyed map and are lost on restart.
#[derive(Default)]
pub struct InMemoryVaultService {
    shards: Mutex<BTreeMap<String, Vec<u8>>>, // shard content hash -> bytes
}

impl InMemoryVaultService {
    /// Construct an empty in-memory vault service.
    pub fn new() -> Self {
        Self::default()
    }
}

impl VaultService for InMemoryVaultService {
    fn store(&self, data: &[u8], label: &str) -> Result<VaultManifest, VaultError> {
        let content_hash = sha256_hex(data);
        let codec = ReedSolomonCodec::new(VAULT_K, VAULT_M)?;

        let shards = if data.is_empty() {
            // Empty file: K zero-padded 1-byte data shards (mirrors the C# shardSize = 1 case).
            let data_shards = vec![vec![0u8; 1]; VAULT_K];
            codec.encode(&data_shards)?
        } else {
            codec.encode_data(data)?
        };

        let mut shard_hashes = Vec::with_capacity(shards.len());
        {
            let mut store = self.shards.lock().expect("vault shard store poisoned");
            for sh in &shards {
                let h = sha256_hex(sh);
                store.insert(h.clone(), sh.clone());
                shard_hashes.push(h);
            }
        }

        Ok(VaultManifest {
            content_hash,
            shard_hashes,
            k: VAULT_K,
            m: VAULT_M,
            size_bytes: data.len() as u64,
            label: label.to_string(),
            created_at_ms: now_unix_ms(),
        })
    }

    fn recover(&self, manifest: &VaultManifest) -> Result<Vec<u8>, VaultError> {
        let total = manifest.shard_hashes.len();
        let k = manifest.k;
        let m = total - k;
        let codec = ReedSolomonCodec::new(k, m)?;

        let mut available: BTreeMap<usize, Vec<u8>> = BTreeMap::new();
        {
            let store = self.shards.lock().expect("vault shard store poisoned");
            for (i, h) in manifest.shard_hashes.iter().enumerate() {
                if let Some(sh) = store.get(h) {
                    available.insert(i, sh.clone());
                }
            }
        }

        if available.len() < k {
            return Err(VaultError::Unrecoverable(format!(
                "cannot recover — only {}/{} shards available",
                available.len(),
                k
            )));
        }
        codec.reconstruct_data(&available, manifest.size_bytes as usize)
    }

    fn check_health(&self, manifest: &VaultManifest) -> VaultHealth {
        let reachable = {
            let store = self.shards.lock().expect("vault shard store poisoned");
            manifest
                .shard_hashes
                .iter()
                .filter(|h| store.contains_key(*h))
                .count()
        };
        let total = manifest.total_shards();
        let redundancy_score = if total > 0 {
            reachable as f64 / total as f64
        } else {
            0.0
        };
        VaultHealth {
            total_shards: total,
            reachable_shards: reachable,
            is_recoverable: reachable >= manifest.k,
            redundancy_score,
        }
    }

    fn replicate(
        &self,
        _manifest: &VaultManifest,
        _target_redundancy: usize,
    ) -> Result<(), VaultError> {
        // No-op in the in-memory implementation.
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn store_then_recover_round_trips() {
        let svc = InMemoryVaultService::new();
        let data: Vec<u8> = (0..=200u8).cycle().take(3333).collect();
        let manifest = svc.store(&data, "doc.bin").unwrap();
        assert_eq!(manifest.shard_hashes.len(), VAULT_K + VAULT_M);
        assert_eq!(manifest.size_bytes, 3333);
        assert_eq!(svc.recover(&manifest).unwrap(), data);
    }

    #[test]
    fn recovers_from_any_k_shards() {
        let svc = InMemoryVaultService::new();
        let data: Vec<u8> = (0..100u8).collect();
        let manifest = svc.store(&data, "x").unwrap();

        // Drop M shards from the store; recovery must still succeed from the surviving K.
        {
            let mut store = svc.shards.lock().unwrap();
            for h in manifest.shard_hashes.iter().take(VAULT_M) {
                store.remove(h);
            }
        }
        let health = svc.check_health(&manifest);
        assert_eq!(health.reachable_shards, VAULT_K);
        assert!(health.is_recoverable);
        assert_eq!(svc.recover(&manifest).unwrap(), data);
    }

    #[test]
    fn unrecoverable_below_k() {
        let svc = InMemoryVaultService::new();
        let manifest = svc.store(&[1, 2, 3, 4, 5], "y").unwrap();
        // Remove M+1 shards -> only K-1 remain -> unrecoverable.
        {
            let mut store = svc.shards.lock().unwrap();
            for h in manifest.shard_hashes.iter().take(VAULT_M + 1) {
                store.remove(h);
            }
        }
        assert!(!svc.check_health(&manifest).is_recoverable);
        assert!(matches!(svc.recover(&manifest), Err(VaultError::Unrecoverable(_))));
    }

    #[test]
    fn empty_data_round_trips() {
        let svc = InMemoryVaultService::new();
        let manifest = svc.store(&[], "empty").unwrap();
        assert_eq!(manifest.size_bytes, 0);
        assert_eq!(svc.recover(&manifest).unwrap(), Vec::<u8>::new());
    }
}
