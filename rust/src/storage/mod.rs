// SPDX-License-Identifier: MIT

//! Persistence layer. Provides the [`KeyValueStore`] trait — a generic
//! string-keyed byte-blob store — plus reference implementations for
//! in-memory and filesystem backends. Adapters for Signal session and
//! pre-key state live alongside the protocol code in
//! [`crate::security::session_store`] and [`crate::security::prekey_store`].
//!
//! Mirrors `AetherNet.Storage.IKeyValueStore` and friends from the C# reference,
//! including the encryption-at-rest wrapper layout.

pub mod encrypted_kv;
pub mod filesystem;
pub mod inmemory;
pub mod key_provider;
pub mod kv;

pub use encrypted_kv::EncryptedKeyValueStore;
pub use filesystem::FileSystemKeyValueStore;
pub use inmemory::InMemoryKeyValueStore;
pub use key_provider::{
    DataAtRestKeyProvider, DerivedDataAtRestKeyProvider, StaticDataAtRestKeyProvider,
};
pub use kv::{KeyValueStore, Result as KvResult};
