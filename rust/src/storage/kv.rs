// SPDX-License-Identifier: MIT

//! Generic byte-array-keyed-by-string persistence primitive used as the
//! foundation for every Aether store that needs to survive a process restart.
//!
//! Mirrors the C# `IKeyValueStore` interface so adapters built on top
//! (Signal session store, pre-key store, route store, DTN bundle store, etc.)
//! line up across language implementations.
//!
//! Two reference implementations ship with this crate:
//!   * [`super::inmemory::InMemoryKeyValueStore`] — volatile, process-local.
//!   * [`super::filesystem::FileSystemKeyValueStore`] — durable, atomic-via-rename.
//!
//! Hosts that need richer guarantees (transactions, encrypted-at-rest,
//! network-attached) supply their own implementation.

use async_trait::async_trait;

/// Result alias used throughout the storage layer. Errors propagate as a
/// boxed trait object so adapters can return whichever underlying error type
/// they like (I/O, serialisation, network, etc.) without forcing a single
/// concrete error enum on every implementation.
pub type Result<T> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;

/// Key-value persistence primitive. All methods are `async`; implementations
/// are responsible for atomicity and durability guarantees.
///
/// Implementations MUST be `Send + Sync` so a single instance can be shared
/// across tasks (e.g. behind an `Arc`).
#[async_trait]
pub trait KeyValueStore: Send + Sync {
    /// Returns the bytes stored under `key`, or `Ok(None)` if absent.
    async fn get(&self, key: &str) -> Result<Option<Vec<u8>>>;

    /// Inserts or replaces the bytes stored under `key`.
    async fn put(&self, key: &str, value: &[u8]) -> Result<()>;

    /// Removes the entry under `key`, if present. Idempotent — removing a
    /// missing key is not an error.
    async fn remove(&self, key: &str) -> Result<()>;

    /// Enumerates keys currently in the store. If `prefix` is `Some`, only
    /// keys whose string representation starts with that prefix are returned.
    /// Order is implementation-defined.
    async fn list_keys(&self, prefix: Option<&str>) -> Result<Vec<String>>;
}
