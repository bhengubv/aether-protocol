// SPDX-License-Identifier: MIT

//! Persistent storage for the long-term identity keys, signed-pre-key
//! history, and one-time pre-key pool.
//!
//! Mirrors the C# `IPreKeyStore` interface and its in-memory + KV adapters.

use async_trait::async_trait;
use std::collections::HashMap;
use std::sync::Arc;
use std::sync::Mutex;

use crate::security::dtos::{
    StoredIdentityKeys, StoredOneTimePreKey, StoredSignedPreKey, StoredSignedPreKeyHistory,
};
use crate::storage::kv::{KeyValueStore, Result as KvResult};

pub type Result<T> = KvResult<T>;

/// Persistent storage for the long-term identity keys, signed-pre-key
/// history, and one-time pre-key pool.
///
/// Implementations are not required to be thread-safe; the
/// [`crate::security::SignalProtocolService`] serialises access through its
/// pre-key mutex before calling.
#[async_trait]
pub trait PreKeyStore: Send + Sync {
    async fn load_identity(&self) -> Result<Option<StoredIdentityKeys>>;
    async fn save_identity(&self, identity: &StoredIdentityKeys) -> Result<()>;
    async fn load_signed_pre_keys(&self) -> Result<StoredSignedPreKeyHistory>;
    async fn save_signed_pre_keys(&self, history: &StoredSignedPreKeyHistory) -> Result<()>;
    async fn load_one_time_pre_keys(&self) -> Result<HashMap<i32, StoredOneTimePreKey>>;
    async fn save_one_time_pre_keys(&self, pool: &HashMap<i32, StoredOneTimePreKey>) -> Result<()>;
    async fn consume_one_time_pre_key(&self, id: i32) -> Result<()>;
}

/// Process-local, volatile [`PreKeyStore`]. Suitable for tests and demos.
pub struct InMemoryPreKeyStore {
    state: Mutex<InMemoryState>,
}

#[derive(Default)]
struct InMemoryState {
    identity: Option<StoredIdentityKeys>,
    spk_history: StoredSignedPreKeyHistory,
    opks: HashMap<i32, StoredOneTimePreKey>,
}

impl InMemoryPreKeyStore {
    pub fn new() -> Self {
        Self {
            state: Mutex::new(InMemoryState::default()),
        }
    }
}

impl Default for InMemoryPreKeyStore {
    fn default() -> Self {
        Self::new()
    }
}

#[async_trait]
impl PreKeyStore for InMemoryPreKeyStore {
    async fn load_identity(&self) -> Result<Option<StoredIdentityKeys>> {
        Ok(self
            .state
            .lock()
            .expect("inmemory pre-key mutex poisoned")
            .identity
            .clone())
    }

    async fn save_identity(&self, identity: &StoredIdentityKeys) -> Result<()> {
        self.state
            .lock()
            .expect("inmemory pre-key mutex poisoned")
            .identity = Some(identity.clone());
        Ok(())
    }

    async fn load_signed_pre_keys(&self) -> Result<StoredSignedPreKeyHistory> {
        Ok(self
            .state
            .lock()
            .expect("inmemory pre-key mutex poisoned")
            .spk_history
            .clone())
    }

    async fn save_signed_pre_keys(&self, history: &StoredSignedPreKeyHistory) -> Result<()> {
        self.state
            .lock()
            .expect("inmemory pre-key mutex poisoned")
            .spk_history = history.clone();
        Ok(())
    }

    async fn load_one_time_pre_keys(&self) -> Result<HashMap<i32, StoredOneTimePreKey>> {
        Ok(self
            .state
            .lock()
            .expect("inmemory pre-key mutex poisoned")
            .opks
            .clone())
    }

    async fn save_one_time_pre_keys(
        &self,
        pool: &HashMap<i32, StoredOneTimePreKey>,
    ) -> Result<()> {
        let mut g = self.state.lock().expect("inmemory pre-key mutex poisoned");
        g.opks.clear();
        for (id, opk) in pool {
            g.opks.insert(*id, opk.clone());
        }
        Ok(())
    }

    async fn consume_one_time_pre_key(&self, id: i32) -> Result<()> {
        self.state
            .lock()
            .expect("inmemory pre-key mutex poisoned")
            .opks
            .remove(&id);
        Ok(())
    }
}

/// [`PreKeyStore`] backed by an arbitrary [`KeyValueStore`].
///
/// Layout:
///   * `signal:identity` — JSON-encoded [`StoredIdentityKeys`]
///   * `signal:spk-history` — JSON-encoded [`StoredSignedPreKeyHistory`]
///   * `signal:opk:<id>` — one [`StoredOneTimePreKey`] per id
///
/// OPKs are written as one entry per id rather than one combined blob so
/// that [`PreKeyStore::consume_one_time_pre_key`] is a single store
/// `remove` without a read-modify-write cycle on the whole pool.
pub struct KvPreKeyStore {
    kv: Arc<dyn KeyValueStore>,
}

impl KvPreKeyStore {
    pub const IDENTITY_KEY: &'static str = "signal:identity";
    pub const SPK_HISTORY_KEY: &'static str = "signal:spk-history";
    pub const OPK_PREFIX: &'static str = "signal:opk:";

    pub fn new(kv: Arc<dyn KeyValueStore>) -> Self {
        Self { kv }
    }

    fn opk_key(id: i32) -> String {
        format!("{}{}", Self::OPK_PREFIX, id)
    }
}

#[async_trait]
impl PreKeyStore for KvPreKeyStore {
    async fn load_identity(&self) -> Result<Option<StoredIdentityKeys>> {
        match self.kv.get(Self::IDENTITY_KEY).await? {
            Some(bytes) => Ok(Some(serde_json::from_slice(&bytes)?)),
            None => Ok(None),
        }
    }

    async fn save_identity(&self, identity: &StoredIdentityKeys) -> Result<()> {
        let bytes = serde_json::to_vec(identity)?;
        self.kv.put(Self::IDENTITY_KEY, &bytes).await
    }

    async fn load_signed_pre_keys(&self) -> Result<StoredSignedPreKeyHistory> {
        match self.kv.get(Self::SPK_HISTORY_KEY).await? {
            Some(bytes) => Ok(serde_json::from_slice(&bytes)?),
            None => Ok(StoredSignedPreKeyHistory::default()),
        }
    }

    async fn save_signed_pre_keys(&self, history: &StoredSignedPreKeyHistory) -> Result<()> {
        let bytes = serde_json::to_vec(history)?;
        self.kv.put(Self::SPK_HISTORY_KEY, &bytes).await
    }

    async fn load_one_time_pre_keys(&self) -> Result<HashMap<i32, StoredOneTimePreKey>> {
        let mut pool = HashMap::new();
        let keys = self.kv.list_keys(Some(Self::OPK_PREFIX)).await?;
        for k in keys {
            if let Some(bytes) = self.kv.get(&k).await? {
                let opk: StoredOneTimePreKey = serde_json::from_slice(&bytes)?;
                pool.insert(opk.id, opk);
            }
        }
        Ok(pool)
    }

    async fn save_one_time_pre_keys(
        &self,
        pool: &HashMap<i32, StoredOneTimePreKey>,
    ) -> Result<()> {
        // Snapshot existing ids so we can prune entries not in the new pool.
        let existing_keys = self.kv.list_keys(Some(Self::OPK_PREFIX)).await?;
        let mut existing_ids: std::collections::HashSet<i32> = existing_keys
            .iter()
            .filter_map(|k| k[Self::OPK_PREFIX.len()..].parse::<i32>().ok())
            .collect();

        for (id, opk) in pool {
            let bytes = serde_json::to_vec(opk)?;
            self.kv.put(&Self::opk_key(*id), &bytes).await?;
            existing_ids.remove(id);
        }
        for id in existing_ids {
            self.kv.remove(&Self::opk_key(id)).await?;
        }
        Ok(())
    }

    async fn consume_one_time_pre_key(&self, id: i32) -> Result<()> {
        self.kv.remove(&Self::opk_key(id)).await
    }
}

/// Convenience: produce a `StoredSignedPreKey` from raw fields. Used by
/// `SignalProtocolService` so it doesn't need to import the DTO directly.
pub fn build_signed_pre_key(
    id: i32,
    private_key: Vec<u8>,
    public_key: Vec<u8>,
    signature: Vec<u8>,
    generated_at_unix_ms: i64,
) -> StoredSignedPreKey {
    StoredSignedPreKey {
        id,
        private_key,
        public_key,
        signature,
        generated_at_unix_ms,
    }
}
