// SPDX-License-Identifier: MIT

//! Process-local, volatile [`KeyValueStore`] backed by a `Mutex<HashMap>`.
//! Suitable for tests and demos. Loses everything on process exit.

use async_trait::async_trait;
use std::collections::HashMap;
use std::sync::Mutex;

use super::kv::{KeyValueStore, Result};

/// Volatile in-memory [`KeyValueStore`]. Cheap to construct and clone-free
/// to share — wrap in an `Arc` if you need multiple owners.
pub struct InMemoryKeyValueStore {
    entries: Mutex<HashMap<String, Vec<u8>>>,
}

impl InMemoryKeyValueStore {
    pub fn new() -> Self {
        Self {
            entries: Mutex::new(HashMap::new()),
        }
    }

    /// Snapshot of the current entry count. Test/diagnostic only.
    pub fn len(&self) -> usize {
        self.entries.lock().expect("inmemory KV mutex poisoned").len()
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }
}

impl Default for InMemoryKeyValueStore {
    fn default() -> Self {
        Self::new()
    }
}

#[async_trait]
impl KeyValueStore for InMemoryKeyValueStore {
    async fn get(&self, key: &str) -> Result<Option<Vec<u8>>> {
        let guard = self.entries.lock().expect("inmemory KV mutex poisoned");
        Ok(guard.get(key).cloned())
    }

    async fn put(&self, key: &str, value: &[u8]) -> Result<()> {
        // Defensive copy so the caller can't subsequently mutate the stored bytes.
        let copy = value.to_vec();
        let mut guard = self.entries.lock().expect("inmemory KV mutex poisoned");
        guard.insert(key.to_string(), copy);
        Ok(())
    }

    async fn remove(&self, key: &str) -> Result<()> {
        let mut guard = self.entries.lock().expect("inmemory KV mutex poisoned");
        guard.remove(key);
        Ok(())
    }

    async fn list_keys(&self, prefix: Option<&str>) -> Result<Vec<String>> {
        let guard = self.entries.lock().expect("inmemory KV mutex poisoned");
        let keys = guard
            .keys()
            .filter(|k| match prefix {
                Some(p) => k.starts_with(p),
                None => true,
            })
            .cloned()
            .collect();
        Ok(keys)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn put_and_get_roundtrip() {
        let kv = InMemoryKeyValueStore::new();
        kv.put("a", b"1").await.unwrap();
        assert_eq!(kv.get("a").await.unwrap().as_deref(), Some(&b"1"[..]));
    }

    #[tokio::test]
    async fn missing_key_returns_none() {
        let kv = InMemoryKeyValueStore::new();
        assert!(kv.get("nope").await.unwrap().is_none());
    }

    #[tokio::test]
    async fn remove_clears_value() {
        let kv = InMemoryKeyValueStore::new();
        kv.put("a", b"1").await.unwrap();
        kv.remove("a").await.unwrap();
        assert!(kv.get("a").await.unwrap().is_none());
    }

    #[tokio::test]
    async fn list_keys_with_prefix() {
        let kv = InMemoryKeyValueStore::new();
        kv.put("foo:1", b"a").await.unwrap();
        kv.put("foo:2", b"b").await.unwrap();
        kv.put("bar:1", b"c").await.unwrap();
        let mut foos = kv.list_keys(Some("foo:")).await.unwrap();
        foos.sort();
        assert_eq!(foos, vec!["foo:1", "foo:2"]);
        assert_eq!(kv.list_keys(None).await.unwrap().len(), 3);
    }

    #[tokio::test]
    async fn put_is_defensive_against_caller_mutation() {
        let kv = InMemoryKeyValueStore::new();
        let mut buf = vec![1u8, 2, 3];
        kv.put("a", &buf).await.unwrap();
        buf[0] = 0xff;
        assert_eq!(kv.get("a").await.unwrap().unwrap(), vec![1, 2, 3]);
    }
}
